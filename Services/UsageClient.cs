using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCodeMonitor.Domain;

namespace ClaudeCodeMonitor.Services;

public abstract record UsageResult
{
    public sealed record Success(UsageSnapshot Snapshot) : UsageResult;
    public sealed record Unauthorized : UsageResult;
    public sealed record RateLimited(TimeSpan? RetryAfter) : UsageResult;
    public sealed record Transient(string Reason) : UsageResult;
}

public class UsageClient(HttpClient httpClient, ILogger<UsageClient> logger)
{
    public async Task<UsageResult> GetUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            using var response = await httpClient.SendAsync(request, cancellationToken);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new UsageResult.Unauthorized();
                case HttpStatusCode.TooManyRequests:
                    return new UsageResult.RateLimited(response.Headers.RetryAfter?.Delta);
                case not HttpStatusCode.OK:
                    return new UsageResult.Transient($"HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<UsageResponse>(stream, cancellationToken: cancellationToken);

            var snapshot = new UsageSnapshot(
                ClampPercent(payload?.FiveHour?.Utilization),
                payload?.FiveHour?.ResetsAt,
                ClampPercent(payload?.SevenDay?.Utilization),
                payload?.SevenDay?.ResetsAt,
                DateTimeOffset.UtcNow);

            return new UsageResult.Success(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Usage request failed: {Reason}", ex.GetType().Name);
            return new UsageResult.Transient(ex.GetType().Name);
        }
    }

    private static int ClampPercent(double? value) =>
        (int)Math.Clamp(Math.Round(value ?? 0), 0, 100);

    private sealed class UsageResponse
    {
        [JsonPropertyName("five_hour")]
        public UsageWindow? FiveHour { get; set; }

        [JsonPropertyName("seven_day")]
        public UsageWindow? SevenDay { get; set; }
    }

    private sealed class UsageWindow
    {
        [JsonPropertyName("utilization")]
        public double? Utilization { get; set; }

        [JsonPropertyName("resets_at")]
        public DateTimeOffset? ResetsAt { get; set; }
    }
}
