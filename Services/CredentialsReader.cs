using System.Text.Json;
using ClaudeCodeMonitor.Configuration;
using Microsoft.Extensions.Options;

namespace ClaudeCodeMonitor.Services;

public record Credentials(string AccessToken, DateTimeOffset ExpiresAt);

public class CredentialsReader(IOptions<MonitorOptions> options, ILogger<CredentialsReader> logger)
{
    private readonly string _path = Environment.ExpandEnvironmentVariables(options.Value.CredentialsPath);

    public Credentials? Read()
    {
        try
        {
            using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                logger.LogWarning("Credentials file does not contain an access token");
                return null;
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var expiresAt = DateTimeOffset.MaxValue;
            if (oauth.TryGetProperty("expiresAt", out var expiresElement) &&
                expiresElement.ValueKind == JsonValueKind.Number)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresElement.GetInt64());
            }

            return new Credentials(token, expiresAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Could not read credentials: {Reason}", ex.GetType().Name);
            return null;
        }
    }
}
