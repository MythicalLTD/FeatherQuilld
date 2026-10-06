using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Utils.Remote;

/// <summary>
/// FeatherPanel OAuth2 device authorization (no callback URL).
/// Starts a grant, then polls until the user approves in the panel UI.
/// </summary>
public sealed class DeviceAuthClient : IDisposable
{
    public const string DefaultAppName = "FeatherQuilld";
    public const int DefaultExpiresInSeconds = 900;
    public const int DefaultPollIntervalSeconds = 5;
    public const int MaxPollIntervalSeconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string BaseUrl { get; }

    public DeviceAuthClient(string panelUrl, bool allowInsecure = false, HttpClient? http = null)
    {
        BaseUrl = AdminPanelClient.NormalizePanelUrl(panelUrl);
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new ArgumentException("Panel URL is required.", nameof(panelUrl));

        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            var handler = new HttpClientHandler();
            if (allowInsecure)
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }

        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<DeviceAuthorizationGrant> StartAsync(
        DeviceAuthorizationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("API client name is required.", nameof(request));

        var payload = new Dictionary<string, string>
        {
            ["name"] = request.Name.Trim(),
            ["alertCors"] = string.IsNullOrWhiteSpace(request.AlertCors) ? "false" : request.AlertCors.Trim(),
            ["allowedips"] = request.AllowedIps ?? "",
        };

        if (!string.IsNullOrWhiteSpace(request.Description))
            payload["description"] = request.Description.Trim();
        if (!string.IsNullOrWhiteSpace(request.AppName))
            payload["appName"] = request.AppName.Trim();
        if (!string.IsNullOrWhiteSpace(request.AppLogo))
            payload["appLogo"] = request.AppLogo.Trim();

        var envelope = await PostEnvelopeAsync("/api/user/api-clients/oauth2/device", payload, ct)
            .ConfigureAwait(false);

        if (!envelope.Success || envelope.Data.ValueKind is not JsonValueKind.Object)
        {
            throw new DeviceAuthException(
                ResolveErrorCode(envelope) ?? "device_start_failed",
                envelope.ErrorMessage ?? envelope.Message ?? "Failed to start device authorization.");
        }

        var grant = JsonSerializer.Deserialize<DeviceAuthorizationGrant>(envelope.Data.GetRawText(), JsonOptions)
                    ?? throw new DeviceAuthException("invalid_response", "Empty device authorization response.");

        if (string.IsNullOrWhiteSpace(grant.DeviceCode) || string.IsNullOrWhiteSpace(grant.UserCode))
            throw new DeviceAuthException("invalid_response", "Device authorization response missing codes.");

        if (grant.ExpiresIn <= 0)
            grant.ExpiresIn = DefaultExpiresInSeconds;
        if (grant.Interval <= 0)
            grant.Interval = DefaultPollIntervalSeconds;

        if (string.IsNullOrWhiteSpace(grant.VerificationUri))
            grant.VerificationUri = $"{BaseUrl}/dashboard/account/oauth2/api/device";
        if (string.IsNullOrWhiteSpace(grant.VerificationUriComplete))
            grant.VerificationUriComplete =
                $"{grant.VerificationUri}?user_code={Uri.EscapeDataString(grant.UserCode)}";

        return grant;
    }

    /// <summary>
    /// Polls until credentials are issued or a terminal error occurs.
    /// Never logs <paramref name="deviceCode"/>.
    /// </summary>
    public async Task<DeviceAuthCredentials> PollForCredentialsAsync(
        string deviceCode,
        int intervalSeconds = DefaultPollIntervalSeconds,
        int expiresInSeconds = DefaultExpiresInSeconds,
        IProgress<DeviceAuthPollStatus>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
            throw new ArgumentException("device_code is required.", nameof(deviceCode));

        var interval = Math.Clamp(intervalSeconds <= 0 ? DefaultPollIntervalSeconds : intervalSeconds, 1, MaxPollIntervalSeconds);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(
            expiresInSeconds <= 0 ? DefaultExpiresInSeconds : expiresInSeconds);

        // Initial wait before first poll (RFC 8628 style).
        await DelayAsync(interval, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new DeviceAuthException("expired_token", "Device authorization expired. Start again.");

            var remaining = Math.Max(0, (int)(deadline - DateTimeOffset.UtcNow).TotalSeconds);
            progress?.Report(new DeviceAuthPollStatus(DeviceAuthPollPhase.Polling, remaining, interval));

            var envelope = await PostEnvelopeAsync(
                    "/api/user/api-clients/oauth2/device/token",
                    new { device_code = deviceCode },
                    ct)
                .ConfigureAwait(false);

            if (envelope.Success && envelope.Data.ValueKind is JsonValueKind.Object)
            {
                var credentials = JsonSerializer.Deserialize<DeviceAuthCredentials>(
                                      envelope.Data.GetRawText(), JsonOptions)
                                  ?? throw new DeviceAuthException(
                                      "invalid_response", "Empty device token response.");

                if (string.IsNullOrWhiteSpace(credentials.PublicKey)
                    || string.IsNullOrWhiteSpace(credentials.PrivateKey))
                {
                    throw new DeviceAuthException(
                        "invalid_response",
                        "Device authorization did not include API credentials.");
                }

                progress?.Report(new DeviceAuthPollStatus(DeviceAuthPollPhase.Approved, remaining, interval));
                return credentials;
            }

            var code = ResolveErrorCode(envelope) ?? "";
            switch (NormalizeErrorCode(code))
            {
                case "authorization_pending":
                    progress?.Report(new DeviceAuthPollStatus(
                        DeviceAuthPollPhase.Pending, remaining, interval));
                    break;

                case "slow_down":
                    if (envelope.Data.ValueKind is JsonValueKind.Object
                        && envelope.Data.TryGetProperty("interval", out var intervalEl)
                        && intervalEl.TryGetInt32(out var serverInterval)
                        && serverInterval > 0)
                    {
                        interval = Math.Clamp(serverInterval, 1, MaxPollIntervalSeconds);
                    }
                    else
                    {
                        interval = Math.Clamp(interval + 5, 1, MaxPollIntervalSeconds);
                    }

                    progress?.Report(new DeviceAuthPollStatus(
                        DeviceAuthPollPhase.SlowDown, remaining, interval));
                    break;

                case "access_denied":
                    throw new DeviceAuthException(
                        "access_denied",
                        envelope.ErrorMessage ?? envelope.Message ?? "Authorization denied on FeatherPanel.");

                case "expired_token":
                    throw new DeviceAuthException(
                        "expired_token",
                        envelope.ErrorMessage ?? envelope.Message ?? "Device authorization expired. Start again.");

                case "invalid_device_code":
                    throw new DeviceAuthException(
                        "INVALID_DEVICE_CODE",
                        envelope.ErrorMessage ?? envelope.Message ?? "Invalid device code. Start again.");

                default:
                    throw new DeviceAuthException(
                        string.IsNullOrWhiteSpace(code) ? "device_token_failed" : code,
                        envelope.ErrorMessage ?? envelope.Message
                        ?? "Device authorization failed.");
            }

            await DelayAsync(interval, ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException(ct);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<PanelApiEnvelope> PostEnvelopeAsync(string path, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseEnvelope(raw);
    }

    internal static PanelApiEnvelope ParseEnvelope(string raw)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        var root = doc.RootElement;
        return new PanelApiEnvelope
        {
            Success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True,
            Message = ReadStringish(root, "message"),
            ErrorMessage = ReadStringish(root, "error_message"),
            ErrorCode = ReadStringish(root, "error_code"),
            // FeatherPanel often returns "error": false (bool) on success — never call GetString() blindly.
            Error = ReadErrorCode(root),
            Data = root.TryGetProperty("data", out var d) ? d.Clone() : default,
        };
    }

    internal static string? ResolveErrorCode(PanelApiEnvelope envelope) =>
        FirstNonEmpty(envelope.ErrorCode, envelope.Error);

    /// <summary>
    /// Reads <c>error</c> when it is a string code; ignores boolean/null (panel success shape).
    /// </summary>
    private static string? ReadErrorCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var err))
            return null;

        return err.ValueKind switch
        {
            JsonValueKind.String => err.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => null,
            _ => err.ToString(),
        };
    }

    private static string? ReadStringish(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.True or JsonValueKind.False => null,
            JsonValueKind.Number => el.ToString(),
            _ => el.ToString(),
        };
    }

    private static string NormalizeErrorCode(string code) =>
        code.Trim().ToLowerInvariant();

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static async Task DelayAsync(int seconds, CancellationToken ct)
    {
        if (seconds <= 0)
            return;
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
    }
}

public sealed class DeviceAuthorizationRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? AppName { get; init; }
    public string? AppLogo { get; init; }
    public string? AllowedIps { get; init; }
    public string? AlertCors { get; init; }
}

public sealed class DeviceAuthorizationGrant
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = "";

    [JsonPropertyName("verification_uri_complete")]
    public string VerificationUriComplete { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

public sealed class DeviceAuthCredentials
{
    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("private_key")]
    public string PrivateKey { get; set; } = "";

    [JsonPropertyName("authorization_code")]
    public string? AuthorizationCode { get; set; }

    [JsonPropertyName("issued_at")]
    public string? IssuedAt { get; set; }
}

public enum DeviceAuthPollPhase
{
    Polling,
    Pending,
    SlowDown,
    Approved,
}

public sealed record DeviceAuthPollStatus(DeviceAuthPollPhase Phase, int SecondsRemaining, int IntervalSeconds);

public sealed class DeviceAuthException : Exception
{
    public string ErrorCode { get; }

    public DeviceAuthException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

internal sealed class PanelApiEnvelope
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public JsonElement Data { get; init; }
}
