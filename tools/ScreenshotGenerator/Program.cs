using System.Text.Json;
using ClaudeCodeMonitor.Configuration;
using ClaudeCodeMonitor.Display;
using Microsoft.Extensions.Options;

// Renders the README images from the monitor's own ScreenComposer and ClawdIcons,
// so they show exactly what the clock shows. Usage: dotnet run -- <output dir>
const int Scale = 8;
const int Padding = 8;

var outputDir = args.Length > 0 ? args[0] : Path.Combine("docs", "images");
Directory.CreateDirectory(outputDir);

var composer = new ScreenComposer(Options.Create(new MonitorOptions()));
var icons = ClawdIcons.Animations();

Save("session", composer.ComposeSessionIconApp(42, TimeSpan.FromHours(3)));
Save("panic", composer.ComposeSessionIconApp(92, TimeSpan.FromMinutes(30)));
Save("sad", composer.ComposeSessionIconApp(100, TimeSpan.FromMinutes(20)));
Save("issue", composer.ComposeDataIssueIconApp(42, TimeSpan.FromHours(3)));
Save("week", composer.ComposeWeekApp(15, TimeSpan.FromDays(4.5)));

// 14 frames covers a full loop of the confetti's fall and Clawd's steps.
var celebration = Enumerable.Range(0, 14).Select(tick => Layer(composer.ComposeCelebrationApp(tick), null)).ToList();
Write("celebration", celebration, 80);

void Save(string name, object app)
{
    var iconName = JsonSerializer.SerializeToElement(app).GetProperty("icon").GetString();
    if (iconName is null)
    {
        Write(name, [Layer(app, null)], 80);
        return;
    }

    var icon = icons[iconName];
    Write(name, icon.Frames.Select(frame => Layer(app, frame)).ToList(), icon.DelayCentiseconds);
}

// The clock draws the icon first and the app's draw commands on top.
string?[][] Layer(object app, byte[]? iconFrame)
{
    var grid = Enumerable.Range(0, 8).Select(_ => new string?[32]).ToArray();
    if (iconFrame is not null)
    {
        for (var i = 0; i < iconFrame.Length; i++)
        {
            if (iconFrame[i] != 0)
            {
                var rgb = ClawdIcons.Palette[iconFrame[i]];
                grid[i / 32][i % 32] = $"#{rgb[0]:x2}{rgb[1]:x2}{rgb[2]:x2}";
            }
        }
    }

    foreach (var command in JsonSerializer.SerializeToElement(app).GetProperty("draw").EnumerateArray())
    {
        var dp = command.GetProperty("dp");
        grid[dp[1].GetInt32()][dp[0].GetInt32()] = dp[2].GetString();
    }

    return grid;
}

void Write(string name, List<string?[][]> grids, int delayCentiseconds)
{
    var colors = new List<string> { "#000000" };
    foreach (var color in grids.SelectMany(grid => grid).SelectMany(row => row).OfType<string>().Distinct())
    {
        if (!colors.Contains(color))
        {
            colors.Add(color);
        }
    }

    if (colors.Count > 8)
    {
        throw new InvalidOperationException($"{name} uses {colors.Count} colors; GifWriter supports 8");
    }

    var palette = Enumerable.Range(0, 8)
        .Select(i => Convert.FromHexString((i < colors.Count ? colors[i] : colors[0])[1..]))
        .ToArray();

    var width = 32 * Scale + 2 * Padding;
    var height = 8 * Scale + 2 * Padding;
    var frames = grids.Select(grid =>
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                if (grid[y][x] is not { } color)
                {
                    continue;
                }

                // One pixel short of the cell on each axis leaves the dark gap between LEDs.
                for (var dy = 0; dy < Scale - 1; dy++)
                {
                    for (var dx = 0; dx < Scale - 1; dx++)
                    {
                        pixels[(Padding + y * Scale + dy) * width + Padding + x * Scale + dx] = (byte)colors.IndexOf(color);
                    }
                }
            }
        }

        return pixels;
    }).ToList();

    var path = Path.Combine(outputDir, $"{name}.gif");
    File.WriteAllBytes(path, GifWriter.Animated(frames, width, height, palette, delayCentiseconds));
    Console.WriteLine($"{path} ({frames.Count} frame(s))");
}
