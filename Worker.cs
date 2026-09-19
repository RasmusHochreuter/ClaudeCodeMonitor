using ClaudeCodeMonitor.Configuration;
using ClaudeCodeMonitor.Display;
using ClaudeCodeMonitor.Domain;
using ClaudeCodeMonitor.Services;
using Microsoft.Extensions.Options;

namespace ClaudeCodeMonitor;

public class Worker(
    CredentialsReader credentialsReader,
    UsageClient usageClient,
    AwtrixClient awtrixClient,
    ScreenComposer screenComposer,
    IOptions<MonitorOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    private const string SessionApp = "claude_session";
    private const string WeekApp = "claude_week";
    private const string IssueApp = "claude_issue";

    private readonly MonitorOptions _options = options.Value;
    private DisplayState _state = DisplayState.Initial;

    private CancellationToken _stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        logger.LogInformation(
            "Monitoring usage every {Interval} min, pushing to clock at {Host}",
            _options.PollIntervalMinutes, _options.AwtrixHost);

        var pokeWatcher = WatchScenarioPokesAsync(stoppingToken);
        var weekPeeker = PeekAtWeekAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            do
            {
                try
                {
                    await PollGuardedAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Poll cycle failed; retrying on next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }

        await pokeWatcher;
        await weekPeeker;
    }

    // The session screen is home; every so often the weekly screen is shown briefly.
    // Only peeks when the clock is on the session screen, and only returns if it is
    // still on the weekly one, so switching screens by hand is never fought.
    private async Task PeekAtWeekAsync(CancellationToken stoppingToken)
    {
        if (_options.WeekPeekIntervalMinutes <= 0 || _options.WeekPeekSeconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.WeekPeekIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (_state.IsStale || DateTimeOffset.UtcNow < _celebrateUntil ||
                        await awtrixClient.GetCurrentAppAsync(stoppingToken) != SessionApp ||
                        !await awtrixClient.SwitchToAppAsync(WeekApp, stoppingToken))
                    {
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(_options.WeekPeekSeconds), stoppingToken);
                    if (await awtrixClient.GetCurrentAppAsync(stoppingToken) == WeekApp)
                    {
                        await awtrixClient.SwitchToAppAsync(SessionApp, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Weekly peek failed; retrying on next interval");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool _rotationDisabled;
    private bool _iconsUploaded;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private async Task PollGuardedAsync(CancellationToken cancellationToken)
    {
        await _pollLock.WaitAsync(cancellationToken);
        try
        {
            await PollOnceAsync(cancellationToken);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    // Test-mode only: when pointed at a fake usage endpoint, watch its /poke
    // counter and poll immediately when a scenario button changes it.
    private async Task WatchScenarioPokesAsync(CancellationToken stoppingToken)
    {
        if (_options.UsageBaseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri(_options.UsageBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        string? lastSeq = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var poke = await client.GetStringAsync("poke", stoppingToken);
                    var seq = poke.Split(':')[0];
                    if (lastSeq is not null && seq != lastSeq)
                    {
                        logger.LogInformation("Scenario changed on fake endpoint - polling now");
                        _celebrateUntil = DateTimeOffset.MinValue;
                        _suppressCelebrationOnce = !poke.EndsWith(":1");
                        await PollGuardedAsync(stoppingToken);
                    }

                    lastSeq = seq;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                    !stoppingToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!_rotationDisabled)
        {
            _rotationDisabled = await awtrixClient.DisableRotationAsync(cancellationToken);
            if (_rotationDisabled)
            {
                logger.LogInformation("Clock auto-rotation disabled; use the device buttons to switch screens");
            }
        }

        if (!_iconsUploaded)
        {
            _iconsUploaded = true;
            foreach (var (name, gif) in ClawdIcons.Build())
            {
                _iconsUploaded &= await awtrixClient.UploadIconAsync(name, gif, cancellationToken);
            }

            if (_iconsUploaded)
            {
                logger.LogInformation("Clawd icons uploaded to the clock");
            }
        }

        await UpdateStateAsync(cancellationToken);
        await PushScreensAsync(cancellationToken);
    }

    private async Task UpdateStateAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var credentials = credentialsReader.Read();
        if (credentials is null)
        {
            MarkStale("credentials missing or unreadable");
            return;
        }

        // An expired token only means Claude Code has been idle — it refreshes the token on next
        // use. Idle usage cannot grow, so the last reading stays valid apart from windows that
        // have since reset. Only a monitor that never fetched anything has no data to show.
        if (credentials.ExpiresAt <= now)
        {
            if (_state.LastSnapshot is not { } last)
            {
                MarkStale("access token expired and no usage has been fetched yet");
                return;
            }

            if (_state.IsStale)
            {
                logger.LogInformation("Access token expired while idle; showing last known usage");
            }

            _state = _state with { LastSnapshot = ExpireResetWindows(last, now), IsStale = false, ConsecutiveFailures = 0 };
            return;
        }

        if (_state.PausedUntil is { } pausedUntil)
        {
            if (now < pausedUntil)
            {
                logger.LogDebug("Rate-limit pause active until {PausedUntil:u}; skipping usage call", pausedUntil);
                return;
            }

            logger.LogInformation("Rate-limit pause ended; resuming usage polling");
            _state = _state with { PausedUntil = null };
        }

        switch (await usageClient.GetUsageAsync(credentials.AccessToken, cancellationToken))
        {
            case UsageResult.Success success:
                if (_state.IsStale)
                {
                    logger.LogInformation("Usage data is fresh again");
                }

                logger.LogDebug(
                    "Session {Session}%, weekly {Weekly}%",
                    success.Snapshot.SessionUtilization, success.Snapshot.WeeklyUtilization);
                DetectWindowReset(_state.LastSnapshot, success.Snapshot);
                _state = new DisplayState(success.Snapshot, IsStale: false, ConsecutiveFailures: 0, PausedUntil: null);
                break;

            case UsageResult.Unauthorized:
                MarkStale("usage endpoint rejected the token (401/403)");
                break;

            case UsageResult.RateLimited rateLimited:
                var pause = rateLimited.RetryAfter is { } retryAfter && retryAfter > _options.PollInterval
                    ? retryAfter
                    : _options.PollInterval;
                logger.LogInformation("Rate limited; pausing usage calls for {Pause}", pause);
                _state = _state with { PausedUntil = now + pause };
                break;

            case UsageResult.Transient transient:
                var failures = _state.ConsecutiveFailures + 1;
                logger.LogWarning("Transient usage failure ({Reason}), {Failures} in a row", transient.Reason, failures);
                _state = _state with { ConsecutiveFailures = failures };
                if (failures >= _options.StaleAfterFailures)
                {
                    MarkStale($"{failures} consecutive transient failures");
                }

                break;
        }
    }

    private DateTimeOffset _celebrateUntil = DateTimeOffset.MinValue;
    private Task _celebrationTask = Task.CompletedTask;
    private bool _suppressCelebrationOnce;

    private void DetectWindowReset(UsageSnapshot? previous, UsageSnapshot current)
    {
        if (_suppressCelebrationOnce)
        {
            _suppressCelebrationOnce = false;
            return;
        }

        if (previous is null)
        {
            return;
        }

        var sessionReset = IsWindowReset(
            previous.SessionUtilization, previous.SessionResetsAt,
            current.SessionUtilization, current.SessionResetsAt);
        var weeklyReset = IsWindowReset(
            previous.WeeklyUtilization, previous.WeeklyResetsAt,
            current.WeeklyUtilization, current.WeeklyResetsAt);

        if (!sessionReset && !weeklyReset)
        {
            return;
        }

        logger.LogInformation(
            "{Window} window reset — celebrating for {Seconds}s",
            sessionReset ? "Session" : "Weekly", _options.CelebrationSeconds);
        _celebrateUntil = DateTimeOffset.UtcNow.AddSeconds(_options.CelebrationSeconds);
        if (_celebrationTask.IsCompleted)
        {
            _celebrationTask = CelebrateAsync(_stoppingToken);
        }
    }

    /// <summary>
    /// A window has reset when usage dropped and the window either rolled forward to a new
    /// reset time or disappeared entirely. The real API reports <c>resets_at: null</c> once a
    /// window expires and no new request has opened the next one, so a missing reset time
    /// on the current snapshot is a reset, not a non-event.
    /// </summary>
    private static bool IsWindowReset(
        int previousUtilization, DateTimeOffset? previousResetsAt,
        int currentUtilization, DateTimeOffset? currentResetsAt)
    {
        if (previousUtilization <= 0 || currentUtilization >= previousUtilization)
        {
            return false;
        }

        return currentResetsAt is not { } current ||
            previousResetsAt is not { } previous ||
            current > previous;
    }

    private async Task CelebrateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_options.CelebrationSound && !string.IsNullOrWhiteSpace(_options.CelebrationMelody))
            {
                await awtrixClient.PlayMelodyAsync(_options.CelebrationMelody, cancellationToken);
            }

            while (DateTimeOffset.UtcNow < _celebrateUntil && !_state.IsStale)
            {
                var frame = screenComposer.ComposeCelebrationApp();
                await awtrixClient.PushAppAsync(SessionApp, frame, cancellationToken);
                await awtrixClient.PushAppAsync(WeekApp, frame, cancellationToken);
                await Task.Delay(800, cancellationToken);
            }

            var snapshot = _state.LastSnapshot;
            var now = DateTimeOffset.UtcNow;
            await awtrixClient.PushAppAsync(
                SessionApp,
                screenComposer.ComposeSessionIconApp(snapshot?.SessionUtilization ?? 0, UntilReset(snapshot?.SessionResetsAt, now)),
                cancellationToken);
            await awtrixClient.PushAppAsync(
                WeekApp,
                screenComposer.ComposeWeekApp(snapshot?.WeeklyUtilization ?? 0, UntilReset(snapshot?.WeeklyResetsAt, now)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Celebration animation failed");
        }
    }

    private static UsageSnapshot ExpireResetWindows(UsageSnapshot snapshot, DateTimeOffset now) =>
        snapshot with
        {
            SessionUtilization = snapshot.SessionResetsAt is { } session && session <= now ? 0 : snapshot.SessionUtilization,
            SessionResetsAt = snapshot.SessionResetsAt is { } s && s <= now ? null : snapshot.SessionResetsAt,
            WeeklyUtilization = snapshot.WeeklyResetsAt is { } weekly && weekly <= now ? 0 : snapshot.WeeklyUtilization,
            WeeklyResetsAt = snapshot.WeeklyResetsAt is { } w && w <= now ? null : snapshot.WeeklyResetsAt,
        };

    private void MarkStale(string reason)
    {
        if (!_state.IsStale)
        {
            logger.LogInformation("Display is now stale: {Reason}", reason);
        }
        else
        {
            logger.LogDebug("Display remains stale: {Reason}", reason);
        }

        _state = _state with { IsStale = true };
    }

    private async Task PushScreensAsync(CancellationToken cancellationToken)
    {
        var snapshot = _state.LastSnapshot;
        var session = snapshot?.SessionUtilization ?? 0;
        var weekly = snapshot?.WeeklyUtilization ?? 0;
        var now = DateTimeOffset.UtcNow;

        if (_state.IsStale)
        {
            await awtrixClient.PushAppAsync(
                IssueApp, screenComposer.ComposeDataIssueIconApp(session, UntilReset(snapshot?.SessionResetsAt, now)), cancellationToken);
            await awtrixClient.RemoveAppAsync(SessionApp, cancellationToken);
            await awtrixClient.RemoveAppAsync(WeekApp, cancellationToken);
            return;
        }

        await awtrixClient.RemoveAppAsync(IssueApp, cancellationToken);
        if (DateTimeOffset.UtcNow >= _celebrateUntil)
        {
            await awtrixClient.PushAppAsync(
                SessionApp, screenComposer.ComposeSessionIconApp(session, UntilReset(snapshot?.SessionResetsAt, now)), cancellationToken);
            await awtrixClient.PushAppAsync(
                WeekApp, screenComposer.ComposeWeekApp(weekly, UntilReset(snapshot?.WeeklyResetsAt, now)), cancellationToken);
        }
    }

    private static TimeSpan? UntilReset(DateTimeOffset? resetsAt, DateTimeOffset now) =>
        resetsAt is { } at && at > now ? at - now : null;
}
