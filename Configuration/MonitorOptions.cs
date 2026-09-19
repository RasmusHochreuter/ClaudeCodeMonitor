namespace ClaudeCodeMonitor.Configuration;

public class MonitorOptions
{
    public const string SectionName = "Monitor";

    public string AwtrixHost { get; set; } = string.Empty;
    public int PollIntervalMinutes { get; set; } = 2;
    public int StaleAfterFailures { get; set; } = 3;
    public int CelebrationSeconds { get; set; } = 60;
    public bool CelebrationSound { get; set; } = true;
    public string CelebrationMelody { get; set; } =
        "refill:d=8,o=6,b=200:c,e,g,4c7,p,g,4c7,p,2c7";
    public int WeekPeekIntervalMinutes { get; set; } = 10;
    public int WeekPeekSeconds { get; set; } = 5;
    public string UsageBaseUrl { get; set; } = "https://api.anthropic.com";
    public string CredentialsPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public TimeSpan PollInterval => TimeSpan.FromMinutes(PollIntervalMinutes);
    public int AppLifetimeSeconds => (int)(PollInterval.TotalSeconds * 3);
}
