using System.Net.Http.Json;
using System.Text.Json;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Thin REST client for mailcow's <c>/api/v1</c> endpoints. Authentication uses
/// the <c>X-API-Key</c> header; the key comes from <c>system.mail.mailcow.api_key</c>
/// or from the <c>feather-api-key</c> file next to the compose file.
/// </summary>
public sealed class MailcowApiClient : IMailcowApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly bool _ownsHttpClient;
    private readonly int _domainMailboxLimit;

    public MailcowApiClient(AppConfig config, HttpClient? httpClient = null)
    {
        BaseUrl = ResolveBaseUrl(config);
        _apiKey = ResolveApiKey(config);
        // mailcow treats 0 as "no mailboxes"; keep a usable value even with a bad setting.
        _domainMailboxLimit = config.System.Mail.Mailcow.DomainMailboxLimit > 0
            ? config.System.Mail.Mailcow.DomainMailboxLimit
            : 10;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? CreateDefaultClient(config);
        if (_http.BaseAddress is null)
            _http.BaseAddress = BaseUrl;
        if (!string.IsNullOrEmpty(_apiKey) && !_http.DefaultRequestHeaders.Contains("X-API-Key"))
            _http.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
    }

    public Uri BaseUrl { get; }

    public bool HasApiKey => _apiKey.Length > 0;

    public static Uri ResolveBaseUrl(AppConfig config)
    {
        var mc = config.System.Mail.Mailcow;
        var configured = (mc.Url ?? string.Empty).Trim();
        if (configured.Length > 0)
        {
            if (!configured.Contains("://", StringComparison.Ordinal))
                configured = "https://" + configured;
            return new Uri(configured.TrimEnd('/'));
        }

        var host = (mc.MailHost ?? string.Empty).Trim();
        if (host.Length == 0)
            host = (config.System.Mail.Hostname ?? string.Empty).Trim();
        if (host.Length == 0)
            host = "mail." + Environment.MachineName.ToLowerInvariant();

        var port = mc.HttpsPort > 0 ? mc.HttpsPort : 8443;
        var authority = port is 443 ? host : $"{host}:{port}";
        return new Uri($"https://{authority}");
    }

    public static string ResolveApiKey(AppConfig config)
    {
        var configured = (config.System.Mail.Mailcow.ApiKey ?? string.Empty).Trim();
        if (configured.Length > 0)
            return configured;

        var path = MailcowPaths.ApiKeyFile(config);
        try
        {
            if (File.Exists(path))
                return File.ReadAllText(path).Trim();
        }
        catch
        {
            // best effort, an empty key surfaces as a failed API call with mailcow's message
        }

        return string.Empty;
    }

    private static HttpClient CreateDefaultClient(AppConfig config)
    {
        var handler = new HttpClientHandler();
        if (config.System.Mail.Mailcow.InsecureTls)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public Task<MailcowResult> AddDomainAsync(string domain, CancellationToken ct = default) =>
        PostAsync("/api/v1/add/domain", new Dictionary<string, object?>
        {
            ["domain"] = domain,
            ["description"] = "managed by FeatherQuilld",
            ["aliases"] = "400",
            // NOT 0: mailcow's mailbox add rejects everything while count >= mailboxes.
            ["mailboxes"] = _domainMailboxLimit.ToString(),
            ["defquota"] = "3072",
            ["maxquota"] = "10240",
            ["quota"] = "10240",
            // active must be "1": an inactive domain makes mailcow reject mail for it.
            ["active"] = "1",
            ["backupmx"] = "0",
            ["relay_all_recipients"] = "0",
            ["gal"] = "1",
        }, ct);

    public Task<MailcowResult> DeleteDomainAsync(string domain, CancellationToken ct = default) =>
        // delete/* expects the BARE array as the request body: json_api.php assigns the whole
        // request to $_POST['items'] and the handler json_decodes it, so {"items":[…]}
        // arrives as items = {"items": […]} and mailcow answers "access_denied".
        PostAsync("/api/v1/delete/domain", new[] { domain }, ct);

    public Task<MailcowResult> AddMailboxAsync(string email, string password, bool active = true,
        long quotaMb = 0, CancellationToken ct = default)
    {
        var (localPart, domain) = SplitEmail(email);
        return PostAsync("/api/v1/add/mailbox", new Dictionary<string, object?>
        {
            ["local_part"] = localPart,
            ["domain"] = domain,
            ["name"] = localPart,
            ["password"] = password,
            ["password2"] = password,
            ["active"] = active ? "1" : "0",
            ["quota"] = quotaMb > 0 ? quotaMb.ToString() : "3072",
            ["force_pw_update"] = "0",
            ["tls_enforce_in"] = "0",
            ["tls_enforce_out"] = "0",
        }, ct);
    }

    public Task<MailcowResult> DeleteMailboxAsync(string email, CancellationToken ct = default) =>
        // bare array body, see DeleteDomainAsync
        PostAsync("/api/v1/delete/mailbox", new[] { email }, ct);

    public Task<MailcowResult> EditMailboxAsync(string email, IReadOnlyDictionary<string, object?> attributes,
        CancellationToken ct = default) =>
        PostAsync("/api/v1/edit/mailbox", new Dictionary<string, object?>
        {
            ["items"] = new[] { email },
            ["attr"] = attributes,
        }, ct);

    public Task<MailcowResult> AddAliasAsync(string address, string destination, CancellationToken ct = default) =>
        PostAsync("/api/v1/add/alias", new Dictionary<string, object?>
        {
            ["address"] = address,
            ["goto"] = destination,
            ["active"] = "1",
        }, ct);

    public async Task<MailcowResult> DeleteAliasAsync(string address, CancellationToken ct = default)
    {
        // mailcow deletes aliases by database id (see json_api.php: delete/alias
        // maps items to the alias id), so the address has to be resolved first.
        var id = await FindAliasIdAsync(address, ct).ConfigureAwait(false);
        if (id is null)
            return MailcowResult.Success("alias not found");

        return await PostAsync("/api/v1/delete/alias", new[] { id }, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a mailcow alias id from its address; null when it does not exist.</summary>
    public async Task<string?> FindAliasIdAsync(string address, CancellationToken ct = default)
    {
        address = (address ?? string.Empty).Trim().ToLowerInvariant();
        if (address.Length == 0)
            return null;

        var direct = await GetAsync($"/api/v1/get/alias/{Uri.EscapeDataString(address)}", ct).ConfigureAwait(false);
        var id = FindIdForAddress(direct, address);
        if (id is not null)
            return id;

        var all = await GetAsync("/api/v1/get/alias/all", ct).ConfigureAwait(false);
        return FindIdForAddress(all, address);
    }

    internal static string? FindIdForAddress(string? json, string address)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;
                if (!element.TryGetProperty("address", out var addrEl))
                    continue;
                if (!string.Equals(addrEl.GetString(), address, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (element.TryGetProperty("id", out var idEl))
                    return idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetInt64().ToString()
                        : idEl.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? body : null;
        }
        catch
        {
            return null;
        }
    }

    public Task<MailcowResult> AddDkimAsync(string domain, CancellationToken ct = default) =>
        PostAsync("/api/v1/add/dkim", new Dictionary<string, object?>
        {
            ["domains"] = domain,
            ["dkim_selector"] = "dkim",
            ["key_size"] = "2048",
        }, ct);

    public Task<MailcowResult> SetSpamScoreAsync(string email, double score, CancellationToken ct = default) =>
        // mailcow exposes the per-mailbox rspamd spam threshold through the
        // spam-score endpoint, which takes the mailbox in the body (items/attr),
        // not in the URL. A very high value effectively bypasses scoring.
        PostAsync("/api/v1/edit/spam-score/", new Dictionary<string, object?>
        {
            ["items"] = new[] { email },
            ["attr"] = new Dictionary<string, object?> { ["spam_score"] = score },
        }, ct);

    public async Task<string?> GetContainersStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/v1/get/status/containers", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body;
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }

    /// <summary>
    /// True when the API answers the public version endpoint with a version document —
    /// which is what "the mail stack is reachable" means for a mailcow on another host.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!HasApiKey)
            return false;

        var body = await GetAsync("/api/v1/get/status/version", ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(body)
            && body.Contains("\"version\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Public DKIM TXT for a domain. mailcow assembles it from its redis keys
    /// (<c>dkim_selector</c>/<c>dkim_txt</c>) and splits long values into quoted
    /// chunks; both are normalized into one usable record value.
    /// </summary>
    public async Task<(string? Selector, string? Txt)> GetDkimAsync(string domain, CancellationToken ct = default)
    {
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
            return (null, null);

        var body = await GetAsync("/api/v1/get/dkim/" + Uri.EscapeDataString(normalized), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // mailcow answers "[]" when a domain has no key yet; a non-object root has
            // no properties and would otherwise throw.
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);

            var selector = root.TryGetProperty("dkim_selector", out var sel) ? sel.GetString() : null;
            var txt = root.TryGetProperty("dkim_txt", out var value) ? value.GetString() : null;

            return (NormalizeDkimValue(selector), NormalizeDkimValue(txt));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <summary>Joins mailcow's <c>"chunk1" "chunk2"</c> split back into a single TXT value.</summary>
    private static string? NormalizeDkimValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var joined = value
            .Replace("\" \"", string.Empty, StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Trim();

        return joined.Length == 0 ? null : joined;
    }

    private async Task<MailcowResult> PostAsync(string path, object payload, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, payload, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return MailcowResult.Failure($"HTTP {(int)response.StatusCode}: {Truncate(body)}");

            return Interpret(body);
        }
        catch (Exception e)
        {
            return MailcowResult.Failure(e.Message);
        }
    }

    /// <summary>
    /// mailcow answers with <c>[{"type":"success"|"danger"|"error","log":[...],"msg":[...]}]</c>.
    /// Anything that is not an explicit success is treated as a failure, the raw
    /// text is kept so the API caller sees mailcow's own wording.
    /// </summary>
    internal static MailcowResult Interpret(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return MailcowResult.Success();

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return MailcowResult.Success(body);

            var ok = true;
            var messages = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var type = element.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (!string.Equals(type, "success", StringComparison.OrdinalIgnoreCase))
                    ok = false;

                if (element.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in msg.EnumerateArray())
                        messages.Add(m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : m.ToString());
                }
            }

            return new MailcowResult(ok, string.Join("; ", messages.Where(m => m.Length > 0)));
        }
        catch (JsonException)
        {
            return MailcowResult.Success(body);
        }
    }

    private static (string LocalPart, string Domain) SplitEmail(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at >= email.Length - 1)
            throw new InvalidOperationException($"Invalid email address '{email}'.");
        return (email[..at], email[(at + 1)..]);
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
