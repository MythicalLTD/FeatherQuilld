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
// HttpListener intentionally avoided TcpListener works without URL ACLs on Linux.

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
    public bool? BehindProxy { get; init; }
    public string? Scheme { get; init; }
    public string? AcmeEmail { get; init; }
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
        return ResolveJoinDataAsync(options).GetAwaiter().GetResult().JoinData;
    }

    public static async Task<OAuthJoinResult> ResolveJoinDataAsync(ConfigureOAuthOptions options, CancellationToken ct = default)
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

        var oauthOptions = options;
        if (string.IsNullOrWhiteSpace(oauthOptions.AcmeEmail) && !string.IsNullOrWhiteSpace(clientInfo.Email))
        {
            oauthOptions = new ConfigureOAuthOptions
            {
                PanelUrl = options.PanelUrl,
                CallbackHost = options.CallbackHost,
                AllowInsecure = options.AllowInsecure,
                KeepOAuthKey = options.KeepOAuthKey,
                NodeName = options.NodeName,
                NodeFqdn = options.NodeFqdn,
                LocationId = options.LocationId,
                DaemonListen = options.DaemonListen,
                SftpPort = options.SftpPort,
                DaemonBase = options.DaemonBase,
                BehindProxy = options.BehindProxy,
                Scheme = options.Scheme,
                AcmeEmail = clientInfo.Email,
            };
        }

        using var panel = new AdminPanelClient(panelUrl, apiKey, options.AllowInsecure);
        var (createRequest, tls) = await PromptWebNodeDetailsAsync(panel, callbackHost, oauthOptions, ct)
            .ConfigureAwait(false);

        var node = await panel.CreateWebNodeAsync(createRequest, ct).ConfigureAwait(false);
        ColoredConsole.WriteLine($"&a✓&r &7Created web node &f{node.Name}&7 (&8{node.Uuid}&7)&r");
        AnsiConsole.WriteLine();

        var joinData = await panel.GetWebNodeJoinDataAsync(node.Id, ct).ConfigureAwait(false);

        await MaybeRevokeOAuthKeyAsync(panel, clientInfo, options.KeepOAuthKey, ct).ConfigureAwait(false);

        return new OAuthJoinResult(joinData, tls);
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
        ColoredConsole.WriteLineLiteral("&f", callbackUrl);
        ColoredConsole.WriteLine("&8Ensure this port is open in your firewall and reachable from the panel.&r");
        AnsiConsole.WriteLine();
        ColoredConsole.WriteLine("&8Open this URL in your browser and approve the request:&r");
        AnsiConsole.WriteLine();
        ColoredConsole.WriteLineLiteral("&b", consentUrl);
        AnsiConsole.WriteLine();

        if (TryOpenBrowser(consentUrl))
            ColoredConsole.WriteLine("&8Opened your browser waiting for panel delivery…&r");
        else
            ColoredConsole.WriteLine("&8Open the URL above in your browser waiting for panel delivery…&r");
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
                    "Could not detect this machine's public IP set --callback-host to this node's IP address.");
            if (candidates.Count == 1)
                return candidates[0].Host;
            var outbound = candidates.FirstOrDefault(c => c.Source == "outbound");
            if (!string.IsNullOrEmpty(outbound.Host))
                return outbound.Host;
            throw new InvalidOperationException(
                $"Multiple public IPs detected set --callback-host to this node's IP address.");
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
                return TryOpenLinuxBrowser(url);

            if (OperatingSystem.IsMacOS())
                return LaunchSilently("open", url);

            if (OperatingSystem.IsWindows())
                return LaunchSilently("cmd", "/c", "start", "", url);
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    /// <summary>
    /// <c>sudo quilld configure</c> has no X/Wayland session. Open as SUDO_USER
    /// when possible; never dump xdg-open errors into the wizard.
    /// </summary>
    private static bool TryOpenLinuxBrowser(string url)
    {
        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (RootPrivileges.IsRoot())
        {
            if (!string.IsNullOrWhiteSpace(sudoUser)
                && !sudoUser.Equals("root", StringComparison.Ordinal)
                && TryOpenLinuxBrowserAsUser(sudoUser, url))
                return true;

            // Root's xdg-open cannot talk to the user's display and only
            // prints "cannot open display" / "no method available" noise.
            return false;
        }

        return LaunchSilently("xdg-open", url);
    }

    private static bool TryOpenLinuxBrowserAsUser(string user, string url)
    {
        var uid = TryReadUserId(user);
        var home = TryReadHomeDirectory(user) ?? $"/home/{user}";

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var xauth = Environment.GetEnvironmentVariable("XAUTHORITY");

        if (uid is not null
            && string.IsNullOrWhiteSpace(wayland)
            && Directory.Exists($"/run/user/{uid}/wayland-0"))
            wayland = "wayland-0";

        if (string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(wayland))
            display = ":0";

        if (string.IsNullOrWhiteSpace(xauth))
        {
            var candidate = Path.Combine(home, ".Xauthority");
            if (File.Exists(candidate))
                xauth = candidate;
        }

        var psi = NewHiddenProcess();
        if (TryFindRunuser() is { } runuser)
        {
            psi.FileName = runuser;
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(user);
            psi.ArgumentList.Add("--");
        }
        else
        {
            psi.FileName = "sudo";
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(user);
            psi.ArgumentList.Add("--");
        }

        psi.ArgumentList.Add("env");
        psi.ArgumentList.Add($"HOME={home}");
        if (uid is not null)
        {
            psi.ArgumentList.Add($"XDG_RUNTIME_DIR=/run/user/{uid}");
            psi.ArgumentList.Add($"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{uid}/bus");
        }

        if (!string.IsNullOrWhiteSpace(display))
            psi.ArgumentList.Add($"DISPLAY={display}");
        if (!string.IsNullOrWhiteSpace(wayland))
            psi.ArgumentList.Add($"WAYLAND_DISPLAY={wayland}");
        if (!string.IsNullOrWhiteSpace(xauth))
            psi.ArgumentList.Add($"XAUTHORITY={xauth}");
        psi.ArgumentList.Add("xdg-open");
        psi.ArgumentList.Add(url);

        return LaunchSilently(psi);
    }

    private static string? TryFindRunuser()
    {
        foreach (var path in new[] { "/usr/sbin/runuser", "/usr/bin/runuser" })
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static uint? TryReadUserId(string user) =>
        uint.TryParse(ReadCommandOutput("id", "-u", user), out var uid) ? uid : null;

    private static string? TryReadHomeDirectory(string user)
    {
        var passwd = ReadCommandOutput("getent", "passwd", user);
        if (string.IsNullOrWhiteSpace(passwd))
            return null;

        var parts = passwd.Split(':');
        return parts.Length >= 6 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : null;
    }

    private static string? ReadCommandOutput(string fileName, params string[] args)
    {
        try
        {
            var psi = NewHiddenProcess();
            psi.FileName = fileName;
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var text = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(1000) ? text.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LaunchSilently(string fileName, params string[] args)
    {
        var psi = NewHiddenProcess();
        psi.FileName = fileName;
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return LaunchSilently(psi);
    }

    private static ProcessStartInfo NewHiddenProcess() => new()
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    private static bool LaunchSilently(ProcessStartInfo psi)
    {
        try
        {
            var process = Process.Start(psi);
            if (process is null)
                return false;

            _ = DrainAsync(process);

            // xdg-open returns quickly; a hang means a browser likely started.
            if (!process.WaitForExit(1500))
                return true;

            try
            {
                return process.ExitCode == 0;
            }
            finally
            {
                process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task DrainAsync(Process process)
    {
        try
        {
            await Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync()).ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }
    }

    private static async Task<(CreateWebNodeRequest Request, NodeTlsCertificate? Tls)> PromptWebNodeDetailsAsync(
        AdminPanelClient panel,
        string nodeIp,
        ConfigureOAuthOptions options,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.NodeName)
            && !string.IsNullOrWhiteSpace(options.NodeFqdn)
            && options.LocationId is > 0)
        {
            var request = BuildRequest(options, nodeIp, options.LocationId.Value, panel.BaseUrl);
            NodeTlsCertificate? tls = null;
            if (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                && request.BehindProxy != true
                && !IPAddress.TryParse(request.Fqdn, out _))
            {
                tls = ConfigureLetsEncrypt.Ensure(request.Fqdn, options.AcmeEmail, nodeIp, ct);
            }

            return (request, tls);
        }

        if (!ConfigureWizard.IsInteractive)
        {
            throw new InvalidOperationException(
                "Missing --node-name, --node-fqdn, and --location-id for non-interactive OAuth setup. " +
                "Create a web location in the panel first, or run interactively to create one.");
        }

        var locations = await panel.ListWebLocationsAsync(ct).ConfigureAwait(false);
        return await ConfigurePrompts.PromptWebNodeDetailsAsync(
                panel, locations, nodeIp, panel.BaseUrl, options, ct)
            .ConfigureAwait(false);
    }

    internal static CreateWebNodeRequest BuildRequest(
        ConfigureOAuthOptions options,
        string nodeIp,
        int locationId,
        string? panelUrl = null)
    {
        var hostname = Dns.GetHostName();
        if (string.IsNullOrWhiteSpace(hostname))
            hostname = "node";

        var behindProxy = options.BehindProxy ?? false;
        var scheme = ResolveScheme(panelUrl, behindProxy, options.Scheme);
        var fqdn = string.IsNullOrWhiteSpace(options.NodeFqdn)
            ? (behindProxy || string.IsNullOrWhiteSpace(nodeIp) ? hostname : nodeIp)
            : options.NodeFqdn.Trim();

        return new CreateWebNodeRequest
        {
            Name = string.IsNullOrWhiteSpace(options.NodeName) ? hostname : options.NodeName.Trim(),
            Fqdn = fqdn,
            LocationId = locationId,
            Scheme = scheme,
            Public = true,
            BehindProxy = behindProxy,
            DaemonListen = options.DaemonListen is > 0 ? options.DaemonListen : 8989,
            SftpPort = options.SftpPort is > 0 ? options.SftpPort : 2222,
            DaemonBase = string.IsNullOrWhiteSpace(options.DaemonBase)
                ? "/var/lib/featherquilld"
                : options.DaemonBase.Trim(),
            Description = $"FeatherQuilld node at {nodeIp}",
            SftpEnabled = true,
        };
    }

    private static string ResolveScheme(string? panelUrl, bool behindProxy, string? forcedScheme)
    {
        if (!string.IsNullOrWhiteSpace(forcedScheme))
            return forcedScheme.Trim().ToLowerInvariant();

        if (behindProxy)
            return "https";

        if (string.IsNullOrWhiteSpace(panelUrl)
            || !Uri.TryCreate(panelUrl.Trim(), UriKind.Absolute, out var uri))
            return "https";

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out _))
            return "http";

        return "https";
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

/// <summary>OAuth configure result: join-data plus optional Let's Encrypt paths.</summary>
public sealed record OAuthJoinResult(string JoinData, NodeTlsCertificate? Tls);
