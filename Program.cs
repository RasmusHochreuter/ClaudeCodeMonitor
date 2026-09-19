using ClaudeCodeMonitor;
using ClaudeCodeMonitor.Configuration;
using ClaudeCodeMonitor.Display;
using ClaudeCodeMonitor.Services;

var builder = Host.CreateApplicationBuilder(args);

// Machine-specific settings (the clock address) live in a git-ignored file.
// Environment variables are re-added so they still win over it.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.Configure<MonitorOptions>(
    builder.Configuration.GetSection(MonitorOptions.SectionName));

var monitorOptions = builder.Configuration
    .GetSection(MonitorOptions.SectionName)
    .Get<MonitorOptions>() ?? new MonitorOptions();

if (string.IsNullOrWhiteSpace(monitorOptions.AwtrixHost))
{
    Console.Error.WriteLine(
        "Configuration error: Monitor:AwtrixHost is required. Set it in appsettings.Local.json to your clock's IP or hostname.");
    return 1;
}

builder.Services.AddHttpClient<UsageClient>(client =>
{
    client.BaseAddress = new Uri(monitorOptions.UsageBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient<AwtrixClient>(client =>
{
    client.BaseAddress = new Uri($"http://{monitorOptions.AwtrixHost}/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<CredentialsReader>();
builder.Services.AddSingleton<ScreenComposer>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
return 0;
