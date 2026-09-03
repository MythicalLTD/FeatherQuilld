using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherQuilld.Utils;
using FeatherQuilld.Utils.Remote;
using Spectre.Console;
// HttpListener intentionally avoided — TcpListener works without URL ACLs on Linux.

namespace FeatherQuilld.Commands;

/// <summary>Options for OAuth2 quick setup (Wings-style panel consent → create web node).</summary>
public sealed class ConfigureOAuthOptions
{
    public string? PanelUrl { get; init; }
    public string? CallbackHost { get; init; }
    public bool AllowInsecure { get; init; }
    public bool KeepOAuthKey { get; init; }
    public string? NodeName { get; init; }
    public string? NodeFqdn { get; init; }
    public int? LocationId { get; init; }
    public int? DaemonListen { get; init; }
    public int? SftpPort { get; init; }
    public string? DaemonBase { get; init; }
}

/// <summary>FeatherWings-style OAuth2 configure flow for FeatherQuilld web nodes.</summary>
public static class ConfigureOAuth
{
    private static readonly TimeSpan OAuthTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions CallbackJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string ResolveJoinData(ConfigureOAuthOptions options)
    {
        return ResolveJoinDataAsync(options).GetAwaiter().GetResult();
    }

    public static async Task<string> ResolveJoinDataAsync(ConfigureOAuthOptions options, CancellationToken ct = default)
    {
        var panelUrl = options.PanelUrl?.Trim();
        if (string.IsNullOrWhiteSpace(panelUrl))
            panelUrl = ConfigurePrompts.PromptPanelUrl();

        panelUrl = AdminPanelClient.NormalizePanelUrl(panelUrl);
        if (string.IsNullOrWhiteSpace(panelUrl))
            throw new InvalidOperationException("Panel URL is required.");

        AnsiConsole.WriteLine();
        ColoredConsole.WriteLine("&8Authorize FeatherQuilld in your browser to continue.&r");
        AnsiConsole.WriteLine();

        var (credentials, callbackHost) = await RunOAuthAsync(panelUrl, options, ct).ConfigureAwait(false);
        var apiKey = string.IsNullOrWhiteSpace(credentials.PublicKey)
            ? credentials.PrivateKey
            : credentials.PublicKey;

        var clientInfo = await AdminPanelClient.ValidateApiClientAsync(
                panelUrl, credentials.PublicKey, options.AllowInsecure, ct)
            .ConfigureAwait(false);

        ColoredConsole.WriteLine($"&a✓&r &7Authorized as &f{clientInfo.Username}&r");
        AnsiConsole.WriteLine();

        using var panel = new AdminPanelClient(panelUrl, apiKey, options.AllowInsecure);
        var createRequest = await PromptWebNodeDetailsAsync(panel, callbackHost, options, ct)
            .ConfigureAwait(false);

        var node = await panel.CreateWebNodeAsync(createRequest, ct).ConfigureAwait(false);
        ColoredConsole.WriteLine($"&a✓&r &7Created web node &f{node.Name}&7 (&8{node.Uuid}&7)&r");
        AnsiConsole.WriteLine();

        var joinData = await panel.GetWebNodeJoinDataAsync(node.Id, ct).ConfigureAwait(false);

        await MaybeRevokeOAuthKeyAsync(panel, clientInfo, options.KeepOAuthKey, ct).ConfigureAwait(false);

        return joinData;
    }

    private static async Task<(OAuthCredentials Credentials, string CallbackHost)> RunOAuthAsync(
        string panelUrl,
        ConfigureOAuthOptions options,
        CancellationToken ct)
    {
        var callbackHost = await ResolveCallbackHostAsync(options.CallbackHost, ct).ConfigureAwait(false);

        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var callbackUrl = BuildCallbackUrl(callbackHost, port);
        var resultTcs = new TaskCompletionSource<OAuthCredentials>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = AcceptCallbackAsync(listener, resultTcs, ct);

        var consentUrl = BuildConsentUrl(panelUrl, callbackUrl);

        ColoredConsole.WriteLine($"&a✓&r &7Using node IP &f{callbackHost}&r");
        ColoredConsole.WriteLine("&8FeatherPanel will send credentials to:&r");
        ColoredConsole.WriteLine($"&f{callbackUrl}&r");
        ColoredConsole.WriteLine("&8Ensure this port is open in your firewall and reachable from the panel.&r");
        AnsiConsole.WriteLine();
        ColoredConsole.WriteLine("&8Open this URL in your browser and approve the request:&r");
        AnsiConsole.WriteLine();
        ColoredConsole.WriteLine($"&b{consentUrl}&r");
        AnsiConsole.WriteLine();

        if (TryOpenBrowser(consentUrl))
            ColoredConsole.WriteLine("&8Opened your browser — waiting for panel delivery…&r");
        else
            ColoredConsole.WriteLine("&8Waiting for panel delivery…&r");
        AnsiConsole.WriteLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(OAuthTimeout);

        try
        {
            var completed = await Task.WhenAny(resultTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                .ConfigureAwait(false);
            if (completed != resultTcs.Task)
                throw new TimeoutException("Timed out waiting for FeatherPanel authorization.");

            var credentials = await resultTcs.Task.ConfigureAwait(false);
            return (credentials, callbackHost);
        }
        finally
        {
            try { listener.Stop(); } catch { /* ignore */ }
            try { await listenTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private static async Task AcceptCallbackAsync(
        TcpListener listener,
        TaskCompletionSource<OAuthCredentials> tcs,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !tcs.Task.IsCompleted)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client, tcs);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            tcs.TrySetException(ex);
        }
    }

    private static async Task HandleClientAsync(TcpClient client, TaskCompletionSource<OAuthCredentials> tcs)
    {
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? "";
            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var method = parts.Length > 0 ? parts[0] : "";
            var path = parts.Length > 1 ? parts[1] : "";

            var contentLength = 0;
            while (true)
            {
                var header = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(header))
                    break;
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(header["Content-Length:".Length..].Trim(), out var len))
                    contentLength = len;
            }

            var body = "";
            if (contentLength > 0)
            {
                var buffer = new char[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = await reader.ReadAsync(buffer.AsMemory(read, contentLength - read)).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    read += n;
                }
                body = new string(buffer, 0, read);
            }

            var ack = Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 16\r\nConnection: close\r\n\r\n{\"received\":true}");
            await stream.WriteAsync(ack).ConfigureAwait(false);

            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                || path is not ("/callback" or "/callback/"))
                return;

            var payload = JsonSerializer.Deserialize<OAuthCallbackPayload>(body, CallbackJson)
                          ?? throw new InvalidOperationException("empty OAuth callback payload");

            if (!payload.Success)
            {
                var message = payload.ErrorDescription ?? payload.Error ?? "authorization denied";
                tcs.TrySetException(new InvalidOperationException($"Panel authorization denied: {message}"));
                return;
            }

            if (string.IsNullOrWhiteSpace(payload.PublicKey) || string.IsNullOrWhiteSpace(payload.PrivateKey))
            {
                tcs.TrySetException(new InvalidOperationException("OAuth callback did not include API credentials"));
                return;
            }

            tcs.TrySetResult(new OAuthCredentials(
                payload.PublicKey.Trim(),
                payload.PrivateKey.Trim(),
                payload.AuthorizationCode?.Trim() ?? ""));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string BuildConsentUrl(string panelUrl, string callbackUrl)
    {
        var hostname = Dns.GetHostName();
        if (string.IsNullOrWhiteSpace(hostname))
            hostname = "node";

        var query = new Dictionary<string, string>
        {
            ["name"] = $"FeatherQuilld on {hostname}",
            ["callbackurl"] = callbackUrl,
            ["mode"] = "server",
            ["appName"] = "FeatherQuilld",
            ["description"] = "Authorize FeatherQuilld CLI to register this machine as a web hosting node",
        };

        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{panelUrl}/dashboard/account/oauth2/api/new?{qs}";
    }

    private static string BuildCallbackUrl(string host, int port)
    {
        host = host.Trim().Trim('[', ']');
        return host.Contains(':')
            ? $"http://[{host}]:{port}/callback"
            : $"http://{host}:{port}/callback";
    }

    private static async Task<string> ResolveCallbackHostAsync(string? forcedHost, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(forcedHost))
            return NormalizeHost(forcedHost);

        var envHost = Environment.GetEnvironmentVariable("FEATHERQUILLD_CALLBACK_HOST");
        if (!string.IsNullOrWhiteSpace(envHost))
            return NormalizeHost(envHost);

        var candidates = await DiscoverHostsAsync(ct).ConfigureAwait(false);
        if (!ConfigureWizard.IsInteractive)
        {
            if (candidates.Count == 0)
                throw new InvalidOperationException(
                    "Could not detect this machine's public IP — set --callback-host to this node's IP address.");
            if (candidates.Count == 1)
                return candidates[0].Host;
            var outbound = candidates.FirstOrDefault(c => c.Source == "outbound");
            if (!string.IsNullOrEmpty(outbound.Host))
                return outbound.Host;
            throw new InvalidOperationException(
                $"Multiple public IPs detected — set --callback-host to this node's IP address.");
        }

        return ConfigurePrompts.PromptCallbackHost(candidates.Select(c => (c.Host, c.Source)).ToList());
    }

    private static async Task<List<(string Host, string Source)>> DiscoverHostsAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Host, string Source)>();

        void Add(string host, string source)
        {
            host = host.Trim();
            if (string.IsNullOrWhiteSpace(host) || IsLoopback(host) || IsPrivateIPv4(host))
                return;
            if (!seen.Add(host))
                return;
            candidates.Add((host, source));
        }

        try
        {
            Add(await FetchPublicIpv4Async(ct).ConfigureAwait(false), "outbound");
        }
        catch
        {
            /* ignore */
        }

        foreach (var ip in ListPublicIpv4Candidates())
            Add(ip, "interface");

        return candidates;
    }

    private static async Task<string> FetchPublicIpv4Async(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = (await http.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false)).Trim();
        if (!IPAddress.TryParse(body, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("invalid public IP response");
        return ip.ToString();
    }

    private static IEnumerable<string> ListPublicIpv4Candidates()
    {
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up
                || iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var ip = addr.Address.ToString();
                if (!IsPrivateIPv4(ip) && !IsLoopback(ip))
                    yield return ip;
            }
        }
    }

    private static string NormalizeHost(string value)
    {
        value = value.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                value = uri.Host;
        }

        value = value.TrimEnd('/').Trim('[', ']');
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Callback host is required.");
        return value;
    }

    private static bool IsPrivateIPv4(string host)
    {
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254)
               || IPAddress.IsLoopback(ip);
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { UseShellExecute = false });
                return true;
            }
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    private static async Task<CreateWebNodeRequest> PromptWebNodeDetailsAsync(
        AdminPanelClient panel,
        string nodeIp,
        ConfigureOAuthOptions options,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.NodeName)
            && !string.IsNullOrWhiteSpace(options.NodeFqdn)
            && options.LocationId is > 0)
        {
            return BuildRequest(options, nodeIp, options.LocationId.Value);
        }

        if (!ConfigureWizard.IsInteractive)
        {
            throw new InvalidOperationException(
                "Missing --node-name, --node-fqdn, and --location-id for non-interactive OAuth setup.");
        }

        var locations = await panel.ListWebLocationsAsync(ct).ConfigureAwait(false);
        if (locations.Count == 0)
            throw new InvalidOperationException("No web locations found on the panel. Create a web location first.");

        return ConfigurePrompts.PromptWebNodeDetails(locations, nodeIp, options);
    }

    internal static CreateWebNodeRequest BuildRequest(ConfigureOAuthOptions options, string nodeIp, int locationId)
    {
        var hostname = Dns.GetHostName();
        if (string.IsNullOrWhiteSpace(hostname))
            hostname = "node";

        return new CreateWebNodeRequest
        {
            Name = string.IsNullOrWhiteSpace(options.NodeName) ? hostname : options.NodeName.Trim(),
            Fqdn = string.IsNullOrWhiteSpace(options.NodeFqdn) ? hostname : options.NodeFqdn.Trim(),
            LocationId = locationId,
            Scheme = "https",
            Public = true,
            DaemonListen = options.DaemonListen is > 0 ? options.DaemonListen : 8989,
            SftpPort = options.SftpPort is > 0 ? options.SftpPort : 2222,
            DaemonBase = string.IsNullOrWhiteSpace(options.DaemonBase)
                ? "/var/lib/featherquilld"
                : options.DaemonBase.Trim(),
            Description = $"FeatherQuilld node at {nodeIp}",
        };
    }

    private static async Task MaybeRevokeOAuthKeyAsync(
        AdminPanelClient panel,
        AdminApiClientInfo clientInfo,
        bool keepOAuthKey,
        CancellationToken ct)
    {
        if (clientInfo.Id <= 0)
            return;

        var revoke = !keepOAuthKey;
        if (!keepOAuthKey && ConfigureWizard.IsInteractive)
            revoke = ConfigurePrompts.PromptRevokeOAuthKey(clientInfo.Name);

        if (!revoke)
        {
            ColoredConsole.WriteLine("&8Keeping temporary OAuth API key on the panel.&r");
            AnsiConsole.WriteLine();
            return;
        }

        try
        {
            await panel.DeleteApiClientAsync(clientInfo.Id, ct).ConfigureAwait(false);
            ColoredConsole.WriteLine($"&a✓&r &7Deleted temporary OAuth API key &f{clientInfo.Name}&r");
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            ColoredConsole.WriteLine($"&e!&r &7Could not delete temporary OAuth API key: {ex.Message}&r");
            AnsiConsole.WriteLine();
        }
    }

    private sealed record OAuthCredentials(string PublicKey, string PrivateKey, string AuthorizationCode);

    private sealed class OAuthCallbackPayload
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("public_key")]
        public string? PublicKey { get; set; }

        [JsonPropertyName("private_key")]
        public string? PrivateKey { get; set; }

        [JsonPropertyName("authorization_code")]
        public string? AuthorizationCode { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
