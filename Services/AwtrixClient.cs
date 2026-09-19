using System.Text;
using System.Text.Json;

namespace ClaudeCodeMonitor.Services;

public class AwtrixClient(HttpClient httpClient, ILogger<AwtrixClient> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<bool> PushAppAsync(string name, object payload, CancellationToken cancellationToken) =>
        PostAsync(name, JsonSerializer.Serialize(payload, SerializerOptions), cancellationToken);

    public Task<bool> RemoveAppAsync(string name, CancellationToken cancellationToken) =>
        PostAsync(name, string.Empty, cancellationToken);

    public async Task<bool> UploadIconAsync(string name, byte[] gif, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(gif);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/gif");
            // The clock's multipart parser treats everything after `filename="` as the name,
            // so the header must not carry the RFC 5987 `filename*` parameter .NET adds by default.
            file.Headers.TryAddWithoutValidation(
                "Content-Disposition", $"form-data; name=\"file\"; filename=\"/ICONS/{name}.gif\"");
            content.Add(file);
            using var response = await httpClient.PostAsync("edit", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Clock rejected icon {Icon}: HTTP {Status}", name, (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Clock unreachable while uploading icon {Icon}: {Reason}", name, ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> PlayMelodyAsync(string rtttl, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(rtttl, Encoding.UTF8, "text/plain");
            using var response = await httpClient.PostAsync("api/rtttl", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Clock rejected melody: HTTP {Status}", (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Clock unreachable while playing melody: {Reason}", ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> DisableRotationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent("""{"ATRANS":false}""", Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync("api/settings", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Clock rejected settings update: HTTP {Status}", (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Clock unreachable while disabling rotation: {Reason}", ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> SwitchToAppAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync("api/switch", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Clock rejected switch to {App}: HTTP {Status}", name, (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Clock unreachable while switching to {App}: {Reason}", name, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Name of the app the clock is showing right now, or null when it cannot be determined.</summary>
    public async Task<string?> GetCurrentAppAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("api/stats", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stats = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return stats.RootElement.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.String
                ? app.GetString()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug("Could not read the clock's current app: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    private async Task<bool> PostAsync(string name, string body, CancellationToken cancellationToken)
    {
        try
        {
            // The clock intermittently 500s when large payloads arrive back-to-back
            // (ESP32 heap pressure), so give it a breather and retry once.
            for (var attempt = 1; ; attempt++)
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync($"api/custom?name={name}", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (attempt == 2)
                {
                    logger.LogWarning("Clock rejected app {App}: HTTP {Status}", name, (int)response.StatusCode);
                    return false;
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Clock unreachable while pushing app {App}: {Reason}", name, ex.GetType().Name);
            return false;
        }
    }
}
