using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using FeatherQuilld.Utils.Config.Remote;

namespace FeatherQuilld.Utils.Remote;

public delegate void PanelRequestProgressHandler(PanelRequestProgress progress);

public sealed record PanelRequestProgress(
    int Attempt,
    int MaxAttempts,
    string Path,
    string Message);

/// <summary>
/// HTTP client for FeatherPanel quilld-remote API routes (<c>/api/quilld-remote/*</c>).
/// </summary>
public sealed class PanelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly PanelRequestProgressHandler? _onProgress;

    public PanelClient(
        AppConfig config,
        HttpClient? http = null,
        PanelRequestProgressHandler? onProgress = null)
    {
        _config = config;
        _http = http ?? CreateHttpClient(config);
        _onProgress = onProgress;
    }

    public async Task<AppConfig> FetchRuntimeConfigAsync(CancellationToken cancellationToken = default)
    {
        var yaml = await SendWithRetryAsync(
            HttpMethod.Get,
            _config.Remote.ConfigPath,
            acceptYaml: true,
            cancellationToken);

        return AppConfig.DeserializeYaml(yaml);
    }

    public async Task<PanelHealthResponse> FetchHealthAsync(CancellationToken cancellationToken = default)
    {
        var json = await SendWithRetryAsync(
            HttpMethod.Get,
            _config.Remote.HealthPath,
            acceptYaml: false,
            cancellationToken);

        var response = JsonSerializer.Deserialize<PanelHealthResponse>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Panel health response was empty.");

        if (!response.Success)
            throw new InvalidOperationException(response.Message ?? "Panel health check failed.");

        return response;
    }

    private async Task<string> SendWithRetryAsync(
        HttpMethod method,
        string path,
        bool acceptYaml,
        CancellationToken cancellationToken)
    {
        var panelBase = _config.Remote.Panel.TrimEnd('/');
        var route = RemoteConfig.NormalizePath(path);
        var url = $"{panelBase}{route}";
        var attempts = Math.Max(1, _config.Remote.RetryLimit);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(attempt, attempts, path, attempt == 1 ? "Contacting panel…" : "Retrying…");

            using var request = BuildRequest(method, url, acceptYaml);

            try
            {
                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Report(attempt, attempts, path, "Response received");
                    return body;
                }

                lastError = BuildHttpError(response.StatusCode, path, body);

                if (!ShouldRetry(response.StatusCode))
                    throw lastError;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } code && !ShouldRetry(code))
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
                    throw;

                lastError = ex;
            }

            if (attempt < attempts)
            {
                var delaySeconds = RetryDelaySeconds(attempt);
                Report(attempt, attempts, path, $"Failed — retry in {delaySeconds:0}s");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Panel request to {path} failed after {attempts} attempt(s).",
            lastError);
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        >= HttpStatusCode.InternalServerError => true,
        _ => false,
    };

    private static Exception BuildHttpError(HttpStatusCode statusCode, string path, string body)
    {
        var panelMessage = TryParsePanelMessage(body);
        var detail = panelMessage ?? Truncate(body, 256);

        return statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new HttpRequestException(
                    $"Panel rejected credentials (401) on {path}: {detail}. " +
                    "Use a web node token (fqld_ prefix) from Admin → Web Nodes → FeatherQuilld.",
                    null,
                    statusCode),

            HttpStatusCode.Forbidden =>
                new HttpRequestException(
                    $"Panel denied access (403) on {path}: {detail}.",
                    null,
                    statusCode),

            HttpStatusCode.NotFound =>
                new HttpRequestException(
                    $"Panel route not found (404) on {path}. Check remote.config_path / remote.health_path.",
                    null,
                    statusCode),

            _ => new HttpRequestException(
                $"Panel request failed ({(int)statusCode} {statusCode}) on {path}: {detail}",
                null,
                statusCode),
        };
    }

    private static string? TryParsePanelMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{')
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message))
                return message.GetString();

            if (doc.RootElement.TryGetProperty("error_message", out var errorMessage))
                return errorMessage.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private void Report(int attempt, int maxAttempts, string path, string message) =>
        _onProgress?.Invoke(new PanelRequestProgress(attempt, maxAttempts, path, message));

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, bool acceptYaml)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.BearerToken);

        if (acceptYaml)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-yaml"));

        foreach (var (key, value) in _config.Remote.CustomHeaders)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            request.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    private static double RetryDelaySeconds(int attempt) =>
        Math.Min(30, Math.Pow(2, attempt - 1));

    private static HttpClient CreateHttpClient(AppConfig config)
    {
        var handler = new HttpClientHandler();

        if (config.Api.IgnoreCertificateErrors)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, config.Remote.Timeout)),
        };

        return client;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

public sealed class PanelHealthResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public PanelHealthData? Data { get; set; }
}

public sealed class PanelHealthData
{
    public string? Status { get; set; }
    public PanelInfo? Panel { get; set; }
    public PanelNodeInfo? Node { get; set; }
}

public sealed class PanelInfo
{
    public string? AppName { get; set; }
    public DateTimeOffset? Time { get; set; }
}

public sealed class PanelNodeInfo
{
    public Guid Uuid { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }

    [JsonPropertyName("maintenance_mode")]
    public bool MaintenanceMode { get; set; }
}
