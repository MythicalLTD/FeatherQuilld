using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Utils.Remote;

/// <summary>
/// FeatherPanel admin/user API client authenticated with an OAuth or user API key
/// (not the daemon Bearer token used by <see cref="PanelClient"/>).
/// </summary>
public sealed class AdminPanelClient : IDisposable
{
    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string BaseUrl { get; }

    public AdminPanelClient(string panelUrl, string apiKey, bool allowInsecure = false, HttpClient? http = null)
    {
        BaseUrl = NormalizePanelUrl(panelUrl);
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

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _ownsHttp = true;
        }

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public static string NormalizePanelUrl(string baseUrl)
    {
        baseUrl = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return baseUrl;

        return $"{uri.Scheme}://{uri.Authority}";
    }

    public static async Task<AdminApiClientInfo> ValidateApiClientAsync(
        string panelUrl,
        string publicKey,
        bool allowInsecure = false,
        CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler();
        if (allowInsecure)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var baseUrl = NormalizePanelUrl(panelUrl);
        var body = JsonSerializer.Serialize(new { public_key = publicKey });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{baseUrl}/api/user/api-clients/validate", content, ct)
            .ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var envelope = DeserializeEnvelope(raw);
        EnsureSuccess(envelope, "panel rejected the API key");

        var data = JsonSerializer.Deserialize<ValidatePayload>(envelope.Data.GetRawText(), ReadJson)
                   ?? throw new InvalidOperationException("Invalid validate payload.");
        if (!data.Valid)
            throw new InvalidOperationException("Panel: API key is not valid.");

        return new AdminApiClientInfo(
            data.ApiClient.Id,
            data.ApiClient.Name ?? "",
            data.User.Username ?? "",
            data.User.Email ?? "");
    }

    public async Task DeleteApiClientAsync(int id, CancellationToken ct = default)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));

        await RequestJsonAsync(HttpMethod.Delete, $"/api/user/api-clients/{id}", null, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminPanelLocation>> ListWebLocationsAsync(CancellationToken ct = default)
    {
        var query = "page=1&limit=100&type=web";
        var data = await GetJsonAsync<LocationsPayload>($"/api/admin/locations?{query}", ct)
            .ConfigureAwait(false);
        return data.Locations ?? [];
    }

    /// <summary>Creates a web location via PUT /api/admin/locations (same shape as FeatherWings, type=web).</summary>
    public async Task<AdminPanelLocation> CreateWebLocationAsync(
        CreateLocationRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Location name is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Type))
            request.Type = "web";

        if (!string.IsNullOrWhiteSpace(request.FlagCode))
            request.FlagCode = request.FlagCode.Trim().ToLowerInvariant();
        else
            request.FlagCode = null;

        if (!string.IsNullOrWhiteSpace(request.Description))
            request.Description = request.Description.Trim();
        else
            request.Description = null;

        request.Name = request.Name.Trim();

        var data = await PutJsonAsync<LocationPayload>("/api/admin/locations", request, ct)
            .ConfigureAwait(false);
        if (data.Location is null || data.Location.Id <= 0)
            throw new InvalidOperationException("Panel: location create response did not include a location id.");
        return data.Location;
    }

    public async Task<AdminWebNode> CreateWebNodeAsync(CreateWebNodeRequest request, CancellationToken ct = default)
    {
        ApplyDefaults(request);
        var data = await PutJsonAsync<WebNodePayload>("/api/admin/web-nodes", request, ct)
            .ConfigureAwait(false);
        if (data.WebNode is null || data.WebNode.Id <= 0)
            throw new InvalidOperationException("Panel: web node create response did not include a node id.");
        return data.WebNode;
    }

    public async Task<string> GetWebNodeJoinDataAsync(int nodeId, CancellationToken ct = default)
    {
        var data = await GetJsonAsync<SetupCommandPayload>($"/api/admin/web-nodes/{nodeId}/setup-command", ct)
            .ConfigureAwait(false);
        var joinData = data.JoinData?.Trim() ?? "";
        if (string.IsNullOrEmpty(joinData))
            throw new InvalidOperationException("Panel: setup command did not include join_data.");
        return joinData;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static void ApplyDefaults(CreateWebNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Scheme))
            request.Scheme = "https";
        if (string.IsNullOrWhiteSpace(request.DaemonBase))
            request.DaemonBase = "/var/lib/featherquilld";
        if (request.DaemonListen is null or 0)
            request.DaemonListen = 8989;
        if (request.SftpPort is null or 0)
            request.SftpPort = 2222;
        if (request.Memory is null or 0)
            request.Memory = 1024;
        if (request.Disk is null or 0)
            request.Disk = 4096;
        if (request.UploadSize is null or 0)
            request.UploadSize = 100;
        request.SftpEnabled ??= true;
        request.Public ??= true;
        request.BehindProxy ??= false;
        request.MaintenanceMode ??= false;
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        var raw = await RequestJsonAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(raw.GetRawText(), ReadJson)
               ?? throw new InvalidOperationException($"Empty response from {path}.");
    }

    private async Task<T> PutJsonAsync<T>(string path, object payload, CancellationToken ct)
    {
        var raw = await RequestJsonAsync(HttpMethod.Put, path, payload, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(raw.GetRawText(), ReadJson)
               ?? throw new InvalidOperationException($"Empty response from {path}.");
    }

    private async Task<JsonElement> RequestJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload, WriteJson);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var envelope = DeserializeEnvelope(raw);
        EnsureSuccess(envelope, $"panel: {method} {path} failed");
        return envelope.Data;
    }

    private static PanelEnvelope DeserializeEnvelope(string raw)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        var root = doc.RootElement;
        return new PanelEnvelope
        {
            Success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True,
            Message = root.TryGetProperty("message", out var m) ? m.GetString() : null,
            ErrorMessage = root.TryGetProperty("error_message", out var em) ? em.GetString() : null,
            ErrorCode = root.TryGetProperty("error_code", out var ec) ? ec.GetString() : null,
            Data = root.TryGetProperty("data", out var d) ? d.Clone() : default,
        };
    }

    private static void EnsureSuccess(PanelEnvelope envelope, string fallback)
    {
        if (envelope.Success)
            return;

        var message = envelope.ErrorMessage ?? envelope.Message ?? fallback;
        if (!string.IsNullOrWhiteSpace(envelope.ErrorCode))
            message = $"{message} ({envelope.ErrorCode})";
        throw new InvalidOperationException(message);
    }

    private sealed class PanelEnvelope
    {
        public bool Success { get; init; }
        public string? Message { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ErrorCode { get; init; }
        public JsonElement Data { get; init; }
    }

    private sealed class ValidatePayload
    {
        public bool Valid { get; set; }
        public ApiClientDto ApiClient { get; set; } = new();
        public UserDto User { get; set; } = new();
    }

    private sealed class ApiClientDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class UserDto
    {
        public string? Username { get; set; }
        public string? Email { get; set; }
    }

    private sealed class LocationsPayload
    {
        public List<AdminPanelLocation>? Locations { get; set; }
    }

    private sealed class LocationPayload
    {
        [JsonPropertyName("location")]
        public AdminPanelLocation? Location { get; set; }
    }

    private sealed class WebNodePayload
    {
        [JsonPropertyName("web_node")]
        public AdminWebNode? WebNode { get; set; }
    }

    private sealed class SetupCommandPayload
    {
        [JsonPropertyName("join_data")]
        public string? JoinData { get; set; }
    }
}

public sealed record AdminApiClientInfo(int Id, string Name, string Username, string Email);

public sealed class AdminPanelLocation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Short { get; set; }
    public string? Type { get; set; }
    public string? Description { get; set; }

    [JsonPropertyName("flag_code")]
    public string? FlagCode { get; set; }
}

/// <summary>Create payload for PUT /api/admin/locations (FeatherWings-compatible).</summary>
public sealed class CreateLocationRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "web";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("flag_code")]
    public string? FlagCode { get; set; }
}

public sealed class AdminWebNode
{
    public int Id { get; set; }
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Fqdn { get; set; } = "";
}

/// <summary>Create payload matching FeatherPanel WebNodeCreate (mixed snake/camel keys).</summary>
public sealed class CreateWebNodeRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("fqdn")]
    public string Fqdn { get; set; } = "";

    [JsonPropertyName("location_id")]
    public int LocationId { get; set; }

    [JsonPropertyName("scheme")]
    public string? Scheme { get; set; }

    [JsonPropertyName("public")]
    public bool? Public { get; set; }

    [JsonPropertyName("behind_proxy")]
    public bool? BehindProxy { get; set; }

    [JsonPropertyName("maintenance_mode")]
    public bool? MaintenanceMode { get; set; }

    [JsonPropertyName("memory")]
    public int? Memory { get; set; }

    [JsonPropertyName("memory_overallocate")]
    public int? MemoryOverallocate { get; set; }

    [JsonPropertyName("disk")]
    public int? Disk { get; set; }

    [JsonPropertyName("disk_overallocate")]
    public int? DiskOverallocate { get; set; }

    [JsonPropertyName("upload_size")]
    public int? UploadSize { get; set; }

    [JsonPropertyName("daemonListen")]
    public int? DaemonListen { get; set; }

    [JsonPropertyName("daemonBase")]
    public string? DaemonBase { get; set; }

    [JsonPropertyName("sftpEnabled")]
    public bool? SftpEnabled { get; set; }

    [JsonPropertyName("sftpPort")]
    public int? SftpPort { get; set; }
}
