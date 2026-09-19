namespace ClaudeCodeMonitor.Domain;

public record UsageSnapshot(
    int SessionUtilization,
    DateTimeOffset? SessionResetsAt,
    int WeeklyUtilization,
    DateTimeOffset? WeeklyResetsAt,
    DateTimeOffset RetrievedAt);

public record DisplayState(
    UsageSnapshot? LastSnapshot,
    bool IsStale,
    int ConsecutiveFailures,
    DateTimeOffset? PausedUntil)
{
    public static DisplayState Initial { get; } = new(null, true, 0, null);
}
