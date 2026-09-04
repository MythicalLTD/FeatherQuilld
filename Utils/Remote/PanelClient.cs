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
public sealed class PanelClient : IPanelClient
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
        var yaml = await FetchRuntimeConfigYamlAsync(cancellationToken);
        return AppConfig.DeserializeYaml(yaml);
    }

    public Task<string> FetchRuntimeConfigYamlAsync(CancellationToken cancellationToken = default) =>
        SendWithRetryAsync(
            HttpMethod.Get,
            _config.Remote.ConfigPath,
            acceptYaml: true,
            cancellationToken);

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

    /// <summary>GET /api/quilld-remote/webspaces/{uuid} full WebSpace settings from the panel.</summary>
    public async Task<PanelWebSpaceConfig> FetchWebSpaceAsync(
        Guid uuid,
        CancellationToken cancellationToken = default)
    {
        var json = await SendWithRetryAsync(
            HttpMethod.Get,
            $"/api/quilld-remote/webspaces/{uuid}",
            acceptYaml: false,
            cancellationToken);

        return UnwrapData<PanelWebSpaceConfig>(json, $"webspace {uuid}");
    }

    /// <summary>GET /api/quilld-remote/webspaces/{uuid}/install egg install script from the panel.</summary>
    public async Task<PanelInstallScript> FetchWebSpaceInstallAsync(
        Guid uuid,
        CancellationToken cancellationToken = default)
    {
        var json = await SendWithRetryAsync(
            HttpMethod.Get,
            $"/api/quilld-remote/webspaces/{uuid}/install",
            acceptYaml: false,
            cancellationToken);

        return UnwrapData<PanelInstallScript>(json, $"webspace install {uuid}");
    }

    /// <summary>POST /api/quilld-remote/webspaces/{uuid}/install report install completion.</summary>
    public async Task ReportWebSpaceInstallAsync(
        Guid uuid,
        bool successful,
        bool reinstall = false,
        CancellationToken cancellationToken = default)
    {
        var panelBase = _config.Remote.Panel.TrimEnd('/');
        var url = $"{panelBase}/api/quilld-remote/webspaces/{uuid}/install";
        var payload = JsonSerializer.Serialize(new { successful, reinstall }, JsonOptions);

        using var request = BuildRequest(HttpMethod.Post, url, acceptYaml: false);
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw BuildHttpError(response.StatusCode, $"/api/quilld-remote/webspaces/{uuid}/install", body);
    }

    /// <summary>PATCH /api/quilld-remote/webspaces/{uuid} sync backend_port + runtime state.</summary>
    public async Task SyncWebSpaceStateAsync(
        Guid uuid,
        int backendPort,
        string state,
        CancellationToken cancellationToken = default)
    {
        var panelBase = _config.Remote.Panel.TrimEnd('/');
        var url = $"{panelBase}/api/quilld-remote/webspaces/{uuid}";
        var payload = JsonSerializer.Serialize(new
        {
            backend_port = backendPort,
            state,
        }, JsonOptions);

        using var request = BuildRequest(HttpMethod.Patch, url, acceptYaml: false);
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw BuildHttpError(response.StatusCode, $"/api/quilld-remote/webspaces/{uuid}", body);
    }

    /// <summary>POST /api/quilld-remote/transfers/{uuid} report transfer outcome.</summary>
    public async Task ReportTransferAsync(
        Guid uuid,
        bool successful,
        CancellationToken cancellationToken = default)
    {
        var panelBase = _config.Remote.Panel.TrimEnd('/');
        var url = $"{panelBase}/api/quilld-remote/transfers/{uuid}";
        var payload = JsonSerializer.Serialize(new { successful }, JsonOptions);

        using var request = BuildRequest(HttpMethod.Post, url, acceptYaml: false);
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw BuildHttpError(response.StatusCode, $"/api/quilld-remote/transfers/{uuid}", body);
    }

    /// <summary>POST /api/quilld-remote/activity batch activity ingest.</summary>
    public async Task ReportActivitiesAsync(
        IReadOnlyList<PanelActivityEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            return;

        var panelBase = _config.Remote.Panel.TrimEnd('/');
        var url = $"{panelBase}/api/quilld-remote/activity";
        var payload = JsonSerializer.Serialize(new
        {
            data = entries.Select(e => new
            {
                webspace = e.Webspace.ToString("D"),
                e.Event,
                metadata = e.Metadata,
                user = e.User,
                ip = e.Ip,
                timestamp = (e.Timestamp ?? DateTimeOffset.UtcNow).ToString("O"),
            }),
        }, JsonOptions);

        using var request = BuildRequest(HttpMethod.Post, url, acceptYaml: false);
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw BuildHttpError(response.StatusCode, "/api/quilld-remote/activity", body);
    }

    /// <summary>POST /api/quilld-remote/sftp/auth authenticate SFTP user for a WebSpace.</summary>
    public async Task<Utils.Sftp.SftpAuthResult?> AuthenticateSftpAsync(
        string type,
        string username,
        string password,
        string? publicKey = null,
        CancellationToken cancellationToken = default)
    {
        var panelBase = _config.Remote.Panel.TrimEnd('/');
        var url = $"{panelBase}/api/quilld-remote/sftp/auth";
        var payload = JsonSerializer.Serialize(new
        {
            type = NormalizeSftpAuthType(type, publicKey),
            username,
            password = !string.IsNullOrEmpty(publicKey) ? publicKey : password,
            ip = "",
        }, JsonOptions);

        using var request = BuildRequest(HttpMethod.Post, url, acceptYaml: false);
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        try
        {
            return UnwrapData<Utils.Sftp.SftpAuthResult>(body, "sftp auth");
        }
        catch
        {
            return JsonSerializer.Deserialize<Utils.Sftp.SftpAuthResult>(body, JsonOptions);
        }
    }

    /// <summary>POST /api/quilld-remote/webspaces/{uuid}/acme-dns set/clear ACME DNS-01 TXT via panel.</summary>
    public async Task AcmeDnsAsync(
        Guid uuid,
        string action,
        string name,
        string content,
        CancellationToken cancellationToken = default)
    {
        var panelBase = _config.Remote.Panel.TrimEnd('/');
        var url = $"{panelBase}/api/quilld-remote/webspaces/{uuid}/acme-dns";
        var payload = JsonSerializer.Serialize(new
        {
            action = (action ?? "set").Trim().ToLowerInvariant(),
            name,
            content,
        }, JsonOptions);

        using var request = BuildRequest(HttpMethod.Post, url, acceptYaml: false);
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw BuildHttpError(response.StatusCode, $"/api/quilld-remote/webspaces/{uuid}/acme-dns", body);
    }

    private static string NormalizeSftpAuthType(string type, string? publicKey)
    {
        if (!string.IsNullOrEmpty(publicKey))
            return "public_key";

        var t = (type ?? "").Trim().ToLowerInvariant();
        return t is "publickey" or "public_key" ? "public_key" : "password";
    }

    private static T UnwrapData<T>(string json, string label)
    {
        var envelope = JsonSerializer.Deserialize<PanelApiEnvelope<T>>(json, JsonOptions);
        if (envelope is null)
            throw new InvalidOperationException($"Panel {label} response was empty.");

        // Some routes may return the object at the root (compat).
        if (envelope.Data is null && json.TrimStart().StartsWith('{'))
        {
            try
            {
                var direct = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (direct is not null)
                    return direct;
            }
            catch (JsonException)
            {
            }
        }

        if (!envelope.Success && envelope.Data is null)
            throw new InvalidOperationException(envelope.Message ?? $"Panel {label} failed.");

        return envelope.Data
               ?? throw new InvalidOperationException($"Panel {label} response missing data.");
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
                Report(attempt, attempts, path, $"Failed retry in {delaySeconds:0}s");
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
