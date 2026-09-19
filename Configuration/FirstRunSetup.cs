using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeMonitor.Configuration;

/// <summary>
/// Asks for the clock's address on first run and saves it to appsettings.Local.json.
/// </summary>
public static class FirstRunSetup
{
    public const string LocalSettingsFile = "appsettings.Local.json";

    private const int StdInputHandle = -10;
    private const uint FileTypeUnknown = 0;

    /// <returns>The address the user entered, or null when nobody is there to ask.</returns>
    public static async Task<string?> PromptForHostAsync(string contentRoot)
    {
        if (!OpenConsole())
        {
            return null;
        }

        Console.Title = "ClaudeCodeMonitor setup";
        Console.WriteLine("Welcome to ClaudeCodeMonitor!");
        Console.WriteLine();
        Console.WriteLine("What is your clock's IP address? It scrolls across the clock when it");
        Console.WriteLine("starts up, and looks like 192.168.1.42.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        while (true)
        {
            Console.WriteLine();
            Console.Write("Clock address: ");
            if (Console.ReadLine() is not { } line)
            {
                return null;
            }

            var host = line.Trim();
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                host = host["http://".Length..];
            }

            host = host.TrimEnd('/');
            if (!Uri.TryCreate($"http://{host}/", UriKind.Absolute, out var clock) || clock.AbsolutePath != "/")
            {
                Console.WriteLine("That doesn't look like an address. Try something like 192.168.1.42.");
                continue;
            }

            Console.WriteLine("Looking for the clock...");
            if (!await IsClockAsync(http, clock))
            {
                Console.WriteLine($"No AWTRIX clock answered at {host}. Check that the clock is on and on the same network.");
                Console.Write("Press Enter to try another address, or type 'save' to use this one anyway: ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "save", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var path = Path.Combine(contentRoot, LocalSettingsFile);
            Save(path, host);
            Console.WriteLine();
            Console.WriteLine($"Saved to {path}.");
            Console.WriteLine("Clawd should show up on your clock in a few seconds.");
            Console.WriteLine();
            Console.WriteLine("Keep this window open: closing it stops the monitor. From the next start on");
            Console.WriteLine("it runs silently in the background, with no window.");
            Console.WriteLine();
            return host;
        }
    }

    private static async Task<bool> IsClockAsync(HttpClient http, Uri clock)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(clock, "api/stats"));
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static void Save(string path, string host)
    {
        // Keeps whatever else is already in the file.
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        if (root[MonitorOptions.SectionName] is not JsonObject monitor)
        {
            root[MonitorOptions.SectionName] = monitor = [];
        }

        monitor[nameof(MonitorOptions.AwtrixHost)] = host;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    // The monitor is a windowless app, so it normally has no console and no input handle
    // at all. An input handle that is not a console (a pipe, a file, NUL) means a script is
    // driving it, and there is nobody to ask.
    private static bool OpenConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return !Console.IsInputRedirected;
        }

        var input = GetStdHandle(StdInputHandle);
        if (GetConsoleMode(input, out _))
        {
            return true;
        }

        return GetFileType(input) == FileTypeUnknown && AllocConsole();
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
}
