using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FeatherQuilld.Utils.Dns;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.WebSpaces.Malware;
using FeatherQuilld.Utils.Mail;
using FeatherQuilld.Utils.Config.System;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.SystemInfo;

/// <summary>Install/remove host packages FeatherQuilld depends on (reverse proxies, Docker).</summary>
public sealed class HostPackageManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly Regex AnsiRegex = new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private readonly SystemPackageWsHub? _wsHub;
    private readonly AppConfig? _config;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationLocks = new();

    public HostPackageManager(SystemPackageWsHub? wsHub = null, AppConfig? config = null)
    {
        _wsHub = wsHub;
        _config = config;
    }

    static HostPackageManager()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("FeatherQuilld", StartupBanner.Version));
    }

    private static readonly string[] ReverseProxyIds = ["caddy", "nginx", "traefik"];

    public IReadOnlyList<HostPackageStatus> List()
    {
        var proxies = ReverseProxyIds
            .Select(DescribeProxy)
            .ToList();
        var activeProxy = proxies.FirstOrDefault(p => p.Installed);

        var packages = new List<HostPackageStatus>(proxies.Count + 1);
        foreach (var proxy in proxies)
        {
            packages.Add(ApplyReverseProxyInstallPolicy(proxy, activeProxy));
        }

        packages.Add(DescribeDocker());
        packages.Add(DescribePowerDns());
        packages.Add(DescribeClamAv());
        packages.Add(DescribeModSecurity());
        packages.Add(DescribeMailServer());
        packages.Add(DescribeWebmail());
        return packages;
    }

    private static HostPackageStatus ApplyReverseProxyInstallPolicy(
        HostPackageStatus package,
        HostPackageStatus? activeProxy)
    {
        if (activeProxy is null || package.Installed || package.Id == activeProxy.Id)
            return package;

        return package with
        {
            InstallBlocked = true,
            BlockedBy = activeProxy.Id,
            BlockedByName = activeProxy.DisplayName,
        };
    }

    private static bool IsReverseProxy(string packageId) =>
        ReverseProxyIds.Contains(NormalizeId(packageId));

    private static string? GetInstalledReverseProxyExcept(string packageId)
    {
        var id = NormalizeId(packageId);
        foreach (var proxyId in ReverseProxyIds)
        {
            if (proxyId == id)
                continue;

            if (ProxyProbe.BinaryOnPath(proxyId))
                return proxyId;
        }

        return null;
    }

    public async Task<HostPackageOperationResult> InstallAsync(string packageId, AppLogger? logger, CancellationToken ct = default)
    {
        var id = NormalizeId(packageId);
        logger?.Info(LoggerTypes.Application, $"Installing host package: {id}");

        return await RunExclusiveAsync(id, "install", async innerCt =>
        {
            if (IsReverseProxy(id))
            {
                var conflict = GetInstalledReverseProxyExcept(id);
                if (conflict is not null)
                {
                    return HostPackageOperationResult.Fail(
                        $"Remove {conflict} before installing {id}. Only one reverse proxy may be installed.");
                }
            }

            return id switch
            {
                "caddy" => await InstallCaddyAsync(id, logger, innerCt).ConfigureAwait(false),
                "nginx" => await InstallNginxAsync(id, logger, innerCt).ConfigureAwait(false),
                "traefik" => await InstallTraefikAsync(id, logger, innerCt).ConfigureAwait(false),
                "docker" => await InstallDockerAsync(id, logger, innerCt).ConfigureAwait(false),
                "powerdns" => await InstallPowerDnsAsync(id, logger, innerCt).ConfigureAwait(false),
                "clamav" => await InstallClamAvAsync(id, logger, innerCt).ConfigureAwait(false),
                "modsecurity" => await InstallModSecurityAsync(id, logger, innerCt).ConfigureAwait(false),
                "mailserver" => await InstallMailServerAsync(id, logger, innerCt).ConfigureAwait(false),
                "webmail" => await InstallWebmailAsync(id, logger, innerCt).ConfigureAwait(false),
                _ => HostPackageOperationResult.Fail($"Unknown package: {packageId}"),
            };
        }, logger, ct).ConfigureAwait(false);
    }

    public async Task<HostPackageOperationResult> RemoveAsync(
        string packageId,
        bool purgeConfig,
        AppLogger? logger,
        CancellationToken ct = default)
    {
        var id = NormalizeId(packageId);
        logger?.Info(LoggerTypes.Application, $"Removing host package: {id}");

        return await RunExclusiveAsync(id, "remove", async innerCt =>
        {
            return id switch
            {
                "caddy" => await RemoveCaddyAsync(id, purgeConfig, logger, innerCt).ConfigureAwait(false),
                "nginx" => await RemoveViaPackageManagerAsync(id, "nginx", purgeConfig, logger, innerCt).ConfigureAwait(false),
                "traefik" => await RemoveTraefikAsync(id, purgeConfig, logger, innerCt).ConfigureAwait(false),
                "docker" => await RemoveViaPackageManagerAsync(id, ResolveDockerPackageName(), purgeConfig, logger, innerCt).ConfigureAwait(false),
                "powerdns" => await RemovePowerDnsAsync(id, purgeConfig, logger, innerCt).ConfigureAwait(false),
                "clamav" => await RemoveViaPackageManagerAsync(id, "clamav clamav-daemon", purgeConfig, logger, innerCt).ConfigureAwait(false),
                "modsecurity" => await RemoveModSecurityAsync(id, purgeConfig, logger, innerCt).ConfigureAwait(false),
                "mailserver" => await RemoveMailServerAsync(id, purgeConfig, logger, innerCt).ConfigureAwait(false),
                "webmail" => await RemoveWebmailAsync(id, purgeConfig, logger, innerCt).ConfigureAwait(false),
                _ => HostPackageOperationResult.Fail($"Unknown package: {packageId}"),
            };
        }, logger, ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> RunExclusiveAsync(
        string packageId,
        string action,
        Func<CancellationToken, Task<HostPackageOperationResult>> work,
        AppLogger? logger,
        CancellationToken ct)
    {
        var gate = _operationLocks.GetOrAdd(packageId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
            return HostPackageOperationResult.Fail($"Another {action} operation is already running for {packageId}.");

        _wsHub?.BeginOperation(packageId, action);
        if (_wsHub is not null)
            await _wsHub.SendStartedAsync(packageId, ct).ConfigureAwait(false);

        try
        {
            var result = await work(ct).ConfigureAwait(false);
            if (_wsHub is not null)
            {
                if (result.Success)
                    await _wsHub.SendCompletedAsync(packageId, ct).ConfigureAwait(false);
                else
                    await _wsHub.SendFailedAsync(packageId, result.Message, ct).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"Package {action} failed for {packageId}: {ex.Message}");
            if (_wsHub is not null)
                await _wsHub.SendFailedAsync(packageId, ex.Message, ct).ConfigureAwait(false);
            return HostPackageOperationResult.Fail(ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static HostPackageStatus DescribeProxy(string provider)
    {
        var binary = ProxyProbe.ResolveBinary(provider);
        return new HostPackageStatus(
            Id: provider,
            DisplayName: char.ToUpper(provider[0]) + provider[1..],
            Category: "reverse_proxy",
            Installed: binary is not null,
            BinaryPath: binary,
            Version: binary is not null ? TryProxyVersion(provider, binary) : null,
            Managed: true);
    }

    private static HostPackageStatus DescribeDocker()
    {
        var binary = FindOnPath("docker");
        return new HostPackageStatus(
            Id: "docker",
            DisplayName: "Docker",
            Category: "runtime",
            Installed: binary is not null,
            BinaryPath: binary,
            Version: binary is not null ? TryDockerVersion(binary) : null,
            Managed: true);
    }

    private static HostPackageStatus DescribePowerDns()
    {
        var binary = FindOnPath("pdns_server") ?? FindOnPath("pdns");
        return new HostPackageStatus(
            Id: "powerdns",
            DisplayName: "PowerDNS",
            Category: "dns",
            Installed: binary is not null,
            BinaryPath: binary,
            Version: null,
            Managed: true);
    }

    private static HostPackageStatus DescribeClamAv()
    {
        var binary = ClamAvProbe.ResolveBinary();
        return new HostPackageStatus(
            Id: "clamav",
            DisplayName: "ClamAV",
            Category: "security",
            Installed: binary is not null,
            BinaryPath: binary,
            Version: binary is not null ? TryVersionWithArgs(binary, "--version") : null,
            Managed: true);
    }

    private static HostPackageStatus DescribeModSecurity()
    {
        var available = ModSecurityProbe.IsAvailable();
        return new HostPackageStatus(
            Id: "modsecurity",
            DisplayName: "ModSecurity (nginx)",
            Category: "security",
            Installed: available,
            BinaryPath: ModSecurityProbe.ResolveRulesFile(),
            Version: null,
            Managed: true);
    }

    private HostPackageStatus DescribeMailServer()
    {
        var running = _config is not null && MailProbe.ContainerRunning(_config);
        return new HostPackageStatus(
            Id: "mailserver",
            DisplayName: "Mail server (docker-mailserver)",
            Category: "mail",
            Installed: running,
            BinaryPath: _config is not null ? MailPaths.ComposeFile(_config) : null,
            Version: running ? "docker-mailserver" : null,
            Managed: true,
            InstallBlocked: FindOnPath("docker") is null,
            BlockedBy: FindOnPath("docker") is null ? "docker" : null,
            BlockedByName: FindOnPath("docker") is null ? "Docker" : null);
    }

    private HostPackageStatus DescribeWebmail()
    {
        var running = _config is not null && WebmailProbe.ContainerRunning(_config);
        return new HostPackageStatus(
            Id: "webmail",
            DisplayName: "Webmail (Roundcube)",
            Category: "mail",
            Installed: running,
            BinaryPath: _config is not null ? WebmailPaths.ComposeFile(_config) : null,
            Version: running ? "roundcube" : null,
            Managed: true,
            InstallBlocked: FindOnPath("docker") is null,
            BlockedBy: FindOnPath("docker") is null ? "docker" : null,
            BlockedByName: FindOnPath("docker") is null ? "Docker" : null);
    }

    private static string NormalizeId(string packageId) =>
        (packageId ?? "").Trim().ToLowerInvariant();

    private async Task<HostPackageOperationResult> InstallViaPackageManagerAsync(
        string packageId,
        string packageName,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return HostPackageOperationResult.Fail("Package installation is only supported on Linux.");

        var pm = DetectPackageManager();
        if (pm is null)
            return HostPackageOperationResult.Fail("No supported package manager found (apt or dnf).");

        var command = pm switch
        {
            LinuxPackageManager.Apt =>
                $"DEBIAN_FRONTEND=noninteractive apt-get install -y -qq -o Dpkg::Use-Pty=0 {packageName}",
            LinuxPackageManager.Dnf =>
                $"dnf install -y -q {packageName}",
            _ => null,
        };

        if (command is null)
            return HostPackageOperationResult.Fail("Unsupported package manager.");

        return await RunShellAsync(packageId, command, logger, ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> RemoveViaPackageManagerAsync(
        string packageId,
        string packageName,
        bool purgeConfig,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return HostPackageOperationResult.Fail("Package removal is only supported on Linux.");

        var pm = DetectPackageManager();
        if (pm is null)
            return HostPackageOperationResult.Fail("No supported package manager found (apt or dnf).");

        var command = pm switch
        {
            LinuxPackageManager.Apt => purgeConfig
                ? $"DEBIAN_FRONTEND=noninteractive apt-get purge -y {packageName}"
                : $"DEBIAN_FRONTEND=noninteractive apt-get remove -y {packageName}",
            LinuxPackageManager.Dnf => $"dnf remove -y {packageName}",
            _ => null,
        };

        if (command is null)
            return HostPackageOperationResult.Fail("Unsupported package manager.");

        return await RunShellAsync(packageId, command, logger, ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> InstallCaddyAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (ProxyProbe.BinaryOnPath("caddy"))
            return HostPackageOperationResult.Ok("caddy is already installed");

        var arch = ResolveLinuxArch();
        if (arch is not null)
        {
            var github = await InstallCaddyFromGitHubAsync(packageId, arch, logger, ct).ConfigureAwait(false);
            if (github.Success && ProxyProbe.BinaryOnPath("caddy"))
                return github;
        }

        var apt = await InstallViaPackageManagerAsync(packageId, "caddy", logger, ct).ConfigureAwait(false);
        return apt.Success
            ? apt
            : HostPackageOperationResult.Fail(
                string.IsNullOrWhiteSpace(apt.Message)
                    ? "Failed to install caddy from GitHub or apt"
                    : apt.Message);
    }

    private async Task<HostPackageOperationResult> InstallCaddyFromGitHubAsync(
        string packageId,
        string arch,
        AppLogger? logger,
        CancellationToken ct)
    {
        try
        {
            var tag = await FetchLatestGitHubReleaseTagAsync("caddyserver/caddy", ct).ConfigureAwait(false);
            await EmitOutputAsync(packageId, $"Installing Caddy v{tag} from GitHub…\n", ct).ConfigureAwait(false);

            var asset = $"caddy_{tag}_{arch}.tar.gz";
            var url = $"https://github.com/caddyserver/caddy/releases/download/v{tag}/{asset}";
            return await InstallTarGzBinaryAsync(packageId, url, "caddy", "/usr/local/bin/caddy", logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"Caddy GitHub install failed: {ex.Message}");
            await EmitOutputAsync(packageId, $"GitHub install failed: {ex.Message}\n", ct).ConfigureAwait(false);
            return HostPackageOperationResult.Fail(ex.Message);
        }
    }

    private async Task<HostPackageOperationResult> InstallNginxAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (ProxyProbe.BinaryOnPath("nginx"))
            return HostPackageOperationResult.Ok("nginx is already installed");

        if (DetectPackageManager() == LinuxPackageManager.Apt)
        {
            await EmitOutputAsync(packageId, "Installing nginx from nginx.org official repository…\n", ct).ConfigureAwait(false);
            var official = await RunShellAsync(packageId, BuildNginxOfficialRepoInstallCommand(), logger, ct).ConfigureAwait(false);
            if (official.Success && ProxyProbe.BinaryOnPath("nginx"))
                return official;
        }

        return await InstallViaPackageManagerAsync(packageId, "nginx", logger, ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> InstallDockerAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (ProxyProbe.BinaryOnPath("docker"))
            return HostPackageOperationResult.Ok("docker is already installed");

        await EmitOutputAsync(packageId, "Installing Docker from get.docker.com…\n", ct).ConfigureAwait(false);
        var official = await RunShellAsync(
            packageId,
            "curl -fsSL https://get.docker.com | sh",
            logger,
            ct).ConfigureAwait(false);
        if (official.Success && ProxyProbe.BinaryOnPath("docker"))
            return official;

        return await InstallViaPackageManagerAsync(packageId, ResolveDockerPackageName(), logger, ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> InstallPowerDnsAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (PowerDnsProbe.ResolveBinary() is not null)
            return HostPackageOperationResult.Ok("powerdns is already installed");

        var apt = await InstallViaPackageManagerAsync(
            packageId,
            "pdns-server pdns-backend-sqlite3",
            logger,
            ct).ConfigureAwait(false);
        if (!apt.Success)
            return apt;

        try
        {
            var apiKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var dropInDir = "/etc/powerdns/pdns.d";
            Directory.CreateDirectory(dropInDir);
            var conf = $"""
                api=yes
                api-key={apiKey}
                webserver=yes
                webserver-address=127.0.0.1
                webserver-port=8081
                webserver-allow-from=127.0.0.1,::1
                launch=gsqlite3
                gsqlite3-database=/var/lib/powerdns/pdns.sqlite3
                local-address=0.0.0.0,::
                """;
            await File.WriteAllTextAsync(Path.Combine(dropInDir, "featherquilld.conf"), conf, ct).ConfigureAwait(false);

            var root = Environment.GetEnvironmentVariable("FEATHERQUILLD_ROOT") ?? "/var/lib/featherquilld";
            var keyDir = Path.Combine(root, "dns");
            Directory.CreateDirectory(keyDir);
            await File.WriteAllTextAsync(Path.Combine(keyDir, "powerdns-api-key"), apiKey + "\n", ct).ConfigureAwait(false);

            await RunShellAsync(packageId, "systemctl enable --now pdns 2>/dev/null || service pdns restart", logger, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"PowerDNS post-install config failed: {ex.Message}");
        }

        return PowerDnsProbe.ResolveBinary() is not null
            ? HostPackageOperationResult.Ok("powerdns installed open port 53/tcp+udp on this host for authoritative DNS")
            : HostPackageOperationResult.Fail("powerdns package installed but pdns_server not found on PATH");
    }

    private async Task<HostPackageOperationResult> InstallClamAvAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (ClamAvProbe.IsAvailable())
            return HostPackageOperationResult.Ok("clamav is already installed");

        var apt = await InstallViaPackageManagerAsync(
            packageId,
            "clamav clamav-daemon",
            logger,
            ct).ConfigureAwait(false);
        if (!apt.Success)
            return apt;

        await RunShellAsync(packageId, "systemctl enable --now clamav-daemon 2>/dev/null || true", logger, ct)
            .ConfigureAwait(false);
        _ = RunShellAsync(packageId, "freshclam 2>/dev/null || true", logger, ct);

        return ClamAvProbe.IsAvailable()
            ? HostPackageOperationResult.Ok("clamav installed run freshclam if virus definitions are outdated")
            : HostPackageOperationResult.Fail("clamav packages installed but clamscan was not found on PATH");
    }

    private async Task<HostPackageOperationResult> InstallModSecurityAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (ModSecurityProbe.IsAvailable())
            return HostPackageOperationResult.Ok("modsecurity is already configured");

        var apt = await InstallViaPackageManagerAsync(
            packageId,
            "libmodsecurity3 modsecurity-crs libnginx-mod-http-modsecurity",
            logger,
            ct).ConfigureAwait(false);
        if (!apt.Success)
            return apt;

        if (!ModSecuritySetup.TryPrepare(out var modSecConf, out var crsSetup, out var rulesInclude, out var prepareError))
        {
            logger?.Warning(LoggerTypes.Application, $"ModSecurity post-install incomplete: {prepareError}");
            return HostPackageOperationResult.Fail(
                $"modsecurity packages installed but configuration is incomplete: {prepareError}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModSecuritySetup.MainConfPath)!);
            var mainConf = ModSecuritySetup.BuildMainConf(modSecConf, crsSetup, rulesInclude);
            await File.WriteAllTextAsync(ModSecuritySetup.MainConfPath, mainConf, ct).ConfigureAwait(false);

            if (!File.Exists(ModSecuritySetup.NginxModSecurityConfPath))
            {
                await File.WriteAllTextAsync(
                    ModSecuritySetup.NginxModSecurityConfPath,
                    $"Include {ModSecuritySetup.MainConfPath}\n",
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"ModSecurity post-install config failed: {ex.Message}");
            return HostPackageOperationResult.Fail($"modsecurity packages installed but config write failed: {ex.Message}");
        }

        if (!ModSecuritySetup.IsValidRulesFile(ModSecuritySetup.MainConfPath))
        {
            return HostPackageOperationResult.Fail(
                "modsecurity packages installed but /etc/nginx/modsec/main.conf Includes are not valid");
        }

        return ModSecurityProbe.IsAvailable()
            ? HostPackageOperationResult.Ok("modsecurity installed for nginx — reload nginx after enabling WAF on WebSpaces")
            : HostPackageOperationResult.Fail(
                "modsecurity packages and rules installed but the nginx modsecurity module was not detected");
    }

    private async Task<HostPackageOperationResult> RemoveModSecurityAsync(
        string packageId,
        bool purgeConfig,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (purgeConfig)
        {
            foreach (var path in new[] { "/etc/nginx/modsec/main.conf", "/etc/nginx/modsecurity.conf" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    logger?.Warning(LoggerTypes.Application, $"Failed to remove {path}: {ex.Message}");
                }
            }
        }

        return await RemoveViaPackageManagerAsync(
            packageId,
            "libnginx-mod-http-modsecurity libmodsecurity3 modsecurity-crs",
            purgeConfig,
            logger,
            ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> InstallMailServerAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (_config is null)
            return HostPackageOperationResult.Fail("FeatherQuilld config is not available.");

        if (FindOnPath("docker") is null)
            return HostPackageOperationResult.Fail("Install Docker before the mail server package.");

        if (MailProbe.ContainerRunning(_config))
            return HostPackageOperationResult.Ok("mailserver is already running");

        var root = MailPaths.Root(_config);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(MailPaths.MailDataDir(_config));
        Directory.CreateDirectory(MailPaths.MailStateDir(_config));
        Directory.CreateDirectory(MailPaths.ConfigDir(_config));

        var hostname = (_config.System.Mail.Hostname ?? "").Trim();
        if (hostname.Length == 0)
            hostname = Environment.MachineName;

        var apiKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await File.WriteAllTextAsync(MailPaths.ApiKeyPath(_config), apiKey + "\n", ct).ConfigureAwait(false);

        var compose = $"""
            services:
              mailserver:
                image: ghcr.io/docker-mailserver/docker-mailserver:latest
                container_name: {MailPaths.ContainerName}
                hostname: {hostname}
                ports:
                  - "25:25"
                  - "{_config.System.Mail.SmtpPort}:{_config.System.Mail.SmtpPort}"
                  - "{_config.System.Mail.ImapPort}:{_config.System.Mail.ImapPort}"
                volumes:
                  - ./mail-data:/var/mail
                  - ./mail-state:/var/mail-state
                  - ./config/:/tmp/docker-mailserver/
                environment:
                  - ENABLE_RSPAMD=0
                  - ENABLE_CLAMAV=0
                  - ONE_DIR=1
                  - SSL_TYPE=self-signed
                  - POSTMASTER_ADDRESS=postmaster@{hostname}
                  - PERMIT_DOCKER=connected-networks
                cap_add:
                  - NET_ADMIN
                restart: unless-stopped
            """;
        await File.WriteAllTextAsync(MailPaths.ComposeFile(_config), compose, ct).ConfigureAwait(false);

        await EmitOutputAsync(packageId, "Pulling docker-mailserver image…\n", ct).ConfigureAwait(false);
        var pull = await RunShellAsync(
            packageId,
            $"docker pull ghcr.io/docker-mailserver/docker-mailserver:latest",
            logger,
            ct).ConfigureAwait(false);
        if (!pull.Success)
            return pull;

        await EmitOutputAsync(packageId, "Starting mailserver container…\n", ct).ConfigureAwait(false);
        var up = await RunShellAsync(
            packageId,
            $"cd {Quote(root)} && docker compose up -d",
            logger,
            ct).ConfigureAwait(false);
        if (!up.Success)
            return up;

        return MailProbe.ContainerRunning(_config)
            ? HostPackageOperationResult.Ok("mailserver installed open ports 25/587/993/tcp on this host")
            : HostPackageOperationResult.Fail("mailserver compose finished but container is not running");
    }

    private async Task<HostPackageOperationResult> InstallWebmailAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (_config is null)
            return HostPackageOperationResult.Fail("FeatherQuilld config is not available.");

        if (FindOnPath("docker") is null)
            return HostPackageOperationResult.Fail("Install Docker before the webmail package.");

        if (WebmailProbe.ContainerRunning(_config))
            return HostPackageOperationResult.Ok("webmail is already running");

        var root = WebmailPaths.Root(_config);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(WebmailPaths.DataDir(_config));

        var compose = $"""
            services:
              webmail:
                image: roundcube/roundcubemail:latest
                container_name: {WebmailPaths.ContainerName}
                ports:
                  - "127.0.0.1:{WebmailPaths.DefaultPort}:80"
                volumes:
                  - ./data:/var/roundcube/db
                environment:
                  - ROUNDCUBEMAIL_DEFAULT_HOST=host.docker.internal
                  - ROUNDCUBEMAIL_SMTP_SERVER=host.docker.internal
                extra_hosts:
                  - "host.docker.internal:host-gateway"
                restart: unless-stopped
            """;
        await File.WriteAllTextAsync(WebmailPaths.ComposeFile(_config), compose, ct).ConfigureAwait(false);

        await EmitOutputAsync(packageId, "Pulling Roundcube image…\n", ct).ConfigureAwait(false);
        var pull = await RunShellAsync(
            packageId,
            "docker pull roundcube/roundcubemail:latest",
            logger,
            ct).ConfigureAwait(false);
        if (!pull.Success)
            return pull;

        await EmitOutputAsync(packageId, "Starting webmail container…\n", ct).ConfigureAwait(false);
        var up = await RunShellAsync(
            packageId,
            $"cd {Quote(root)} && docker compose up -d",
            logger,
            ct).ConfigureAwait(false);
        if (!up.Success)
            return up;

        return WebmailProbe.ContainerRunning(_config)
            ? HostPackageOperationResult.Ok($"webmail installed http://127.0.0.1:{WebmailPaths.DefaultPort}")
            : HostPackageOperationResult.Fail("webmail compose finished but container is not running");
    }

    private async Task<HostPackageOperationResult> RemoveMailServerAsync(
        string packageId,
        bool purgeConfig,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (_config is not null)
        {
            var root = MailPaths.Root(_config);
            if (Directory.Exists(root) && File.Exists(MailPaths.ComposeFile(_config)))
            {
                await RunShellAsync(
                    packageId,
                    $"cd {Quote(root)} && docker compose down 2>/dev/null || true",
                    logger,
                    ct).ConfigureAwait(false);
            }

            if (purgeConfig && Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch (Exception ex)
                {
                    logger?.Warning(LoggerTypes.Application, $"Failed to purge mail dir: {ex.Message}");
                }
            }
        }

        return HostPackageOperationResult.Ok("mailserver removed");
    }

    private async Task<HostPackageOperationResult> RemoveWebmailAsync(
        string packageId,
        bool purgeConfig,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (_config is not null)
        {
            var root = WebmailPaths.Root(_config);
            if (Directory.Exists(root) && File.Exists(WebmailPaths.ComposeFile(_config)))
            {
                await RunShellAsync(
                    packageId,
                    $"cd {Quote(root)} && docker compose down 2>/dev/null || true",
                    logger,
                    ct).ConfigureAwait(false);
            }

            if (purgeConfig && Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch (Exception ex)
                {
                    logger?.Warning(LoggerTypes.Application, $"Failed to purge webmail dir: {ex.Message}");
                }
            }
        }

        return HostPackageOperationResult.Ok("webmail removed");
    }

    private async Task<HostPackageOperationResult> RemovePowerDnsAsync(
        string packageId,
        bool purgeConfig,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (purgeConfig)
        {
            try
            {
                var dropIn = "/etc/powerdns/pdns.d/featherquilld.conf";
                if (File.Exists(dropIn))
                    File.Delete(dropIn);
            }
            catch (Exception ex)
            {
                logger?.Warning(LoggerTypes.Application, $"Failed to remove PowerDNS drop-in: {ex.Message}");
            }
        }

        return await RemoveViaPackageManagerAsync(packageId, "pdns-server", purgeConfig, logger, ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> RemoveTraefikAsync(
        string packageId,
        bool purgeConfig,
        AppLogger? logger,
        CancellationToken ct)
    {
        await RunShellAsync(packageId, "systemctl disable --now traefik 2>/dev/null || true", logger, ct)
            .ConfigureAwait(false);

        if (purgeConfig)
        {
            foreach (var path in new[] { "/etc/traefik/traefik.yml", "/etc/systemd/system/traefik.service" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    logger?.Warning(LoggerTypes.Application, $"Failed to remove {path}: {ex.Message}");
                }
            }

            await RunShellAsync(packageId, "systemctl daemon-reload 2>/dev/null || true", logger, ct)
                .ConfigureAwait(false);
        }

        return await RemoveBinaryAsync(packageId, "/usr/local/bin/traefik", logger, ct).ConfigureAwait(false);
    }

    private async Task<HostPackageOperationResult> RemoveCaddyAsync(string packageId, bool purgeConfig, AppLogger? logger, CancellationToken ct)
    {
        if (File.Exists("/usr/local/bin/caddy"))
            await RemoveBinaryAsync(packageId, "/usr/local/bin/caddy", logger, ct).ConfigureAwait(false);

        if (DetectPackageManager() == LinuxPackageManager.Apt && File.Exists("/usr/bin/caddy"))
            return await RemoveViaPackageManagerAsync(packageId, "caddy", purgeConfig, logger, ct).ConfigureAwait(false);

        return ProxyProbe.BinaryOnPath("caddy")
            ? HostPackageOperationResult.Fail("caddy is still present on PATH after removal attempt")
            : HostPackageOperationResult.Ok("caddy removed");
    }

    private async Task<HostPackageOperationResult> InstallTraefikAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        if (ProxyProbe.BinaryOnPath("traefik"))
        {
            if (File.Exists("/etc/traefik/traefik.yml"))
                return HostPackageOperationResult.Ok("traefik is already installed");

            return await BootstrapTraefikAsync(packageId, logger, ct).ConfigureAwait(false);
        }

        var arch = ResolveLinuxArch() switch
        {
            "linux_amd64" => "linux_amd64",
            "linux_arm64" => "linux_arm64",
            _ => null,
        };

        if (arch is null)
            return HostPackageOperationResult.Fail("Unsupported CPU architecture for Traefik download.");

        try
        {
            var tag = await FetchLatestGitHubReleaseTagAsync("traefik/traefik", ct).ConfigureAwait(false);
            await EmitOutputAsync(packageId, $"Installing Traefik v{tag} from GitHub…\n", ct).ConfigureAwait(false);

            var asset = $"traefik_v{tag}_{arch}.tar.gz";
            var url = $"https://github.com/traefik/traefik/releases/download/v{tag}/{asset}";
            var result = await InstallTarGzBinaryAsync(packageId, url, "traefik", "/usr/local/bin/traefik", logger, ct).ConfigureAwait(false);
            if (!result.Success)
                return result;

            return await BootstrapTraefikAsync(packageId, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"Traefik GitHub install failed: {ex.Message}");
            return HostPackageOperationResult.Fail(ex.Message);
        }
    }

    private async Task<HostPackageOperationResult> BootstrapTraefikAsync(string packageId, AppLogger? logger, CancellationToken ct)
    {
        try
        {
            var root = _config?.System.RootDirectory
                       ?? Environment.GetEnvironmentVariable("FEATHERQUILLD_ROOT")
                       ?? SystemConfig.DefaultRootDirectory;
            var proxyDir = Path.Combine(root, "proxy");
            Directory.CreateDirectory(proxyDir);

            var acmeEmail = (_config?.System.Proxy.AcmeEmail ?? "").Trim();
            if (string.IsNullOrWhiteSpace(acmeEmail))
                acmeEmail = TryReadAcmeEmailFromConfigFile()?.Trim() ?? "";

            var sb = new StringBuilder();
            sb.AppendLine("entryPoints:");
            sb.AppendLine("  web:");
            sb.AppendLine("    address: \":80\"");
            sb.AppendLine("  websecure:");
            sb.AppendLine("    address: \":443\"");
            sb.AppendLine();
            sb.AppendLine("providers:");
            sb.AppendLine("  file:");
            sb.AppendLine($"    directory: {proxyDir}");
            sb.AppendLine("    watch: true");

            if (!string.IsNullOrWhiteSpace(acmeEmail))
            {
                sb.AppendLine();
                sb.AppendLine("certificatesResolvers:");
                sb.AppendLine("  featherquilld:");
                sb.AppendLine("    acme:");
                sb.AppendLine($"      email: {acmeEmail}");
                sb.AppendLine("      storage: /var/lib/traefik/acme.json");
                sb.AppendLine("      httpChallenge:");
                sb.AppendLine("        entryPoint: web");
            }

            Directory.CreateDirectory("/etc/traefik");
            await File.WriteAllTextAsync("/etc/traefik/traefik.yml", sb.ToString(), ct).ConfigureAwait(false);

            Directory.CreateDirectory("/var/lib/traefik");
            var unit = """
                [Unit]
                Description=Traefik reverse proxy
                After=network-online.target

                [Service]
                ExecStart=/usr/local/bin/traefik --configFile=/etc/traefik/traefik.yml
                Restart=on-failure

                [Install]
                WantedBy=multi-user.target
                """;
            await File.WriteAllTextAsync("/etc/systemd/system/traefik.service", unit, ct).ConfigureAwait(false);
            await RunShellAsync(packageId, "systemctl daemon-reload && systemctl enable --now traefik", logger, ct)
                .ConfigureAwait(false);

            return HostPackageOperationResult.Ok("traefik installed with static config and systemd service");
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"Traefik bootstrap failed: {ex.Message}");
            return HostPackageOperationResult.Ok($"traefik binary installed but bootstrap failed: {ex.Message}");
        }
    }

    private static string? TryReadAcmeEmailFromConfigFile()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("FEATHERQUILLD_CONFIG") ?? global::FeatherQuilld.Utils.Config.Config.DefaultPath();
            if (!File.Exists(path))
                return null;

            var config = global::FeatherQuilld.Utils.Config.Config.Load(path);
            return config.System.Proxy.AcmeEmail;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> FetchLatestGitHubReleaseTagAsync(string repository, CancellationToken ct)
    {
        using var response = await Http.GetAsync(
            $"https://api.github.com/repos/{repository}/releases/latest",
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var json = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v')
               ?? throw new InvalidOperationException($"GitHub release tag missing for {repository}");
    }

    private static string BuildNginxOfficialRepoInstallCommand() =>
        """
        set -e
        . /etc/os-release
        DEBIAN_FRONTEND=noninteractive apt-get update -qq
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq -o Dpkg::Use-Pty=0 gnupg curl ca-certificates
        curl -fsSL https://nginx.org/keys/nginx_signing.key | gpg --dearmor -o /usr/share/keyrings/nginx-archive-keyring.gpg
        if [ "${ID}" = "ubuntu" ]; then
          echo "deb [signed-by=/usr/share/keyrings/nginx-archive-keyring.gpg] https://nginx.org/packages/ubuntu ${VERSION_CODENAME} nginx" > /etc/apt/sources.list.d/nginx.list
        else
          echo "deb [signed-by=/usr/share/keyrings/nginx-archive-keyring.gpg] https://nginx.org/packages/debian ${VERSION_CODENAME} nginx" > /etc/apt/sources.list.d/nginx.list
        fi
        DEBIAN_FRONTEND=noninteractive apt-get update -qq
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq -o Dpkg::Use-Pty=0 nginx
        """;

    private async Task<HostPackageOperationResult> InstallTarGzBinaryAsync(
        string packageId,
        string url,
        string binaryName,
        string destPath,
        AppLogger? logger,
        CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "featherquilld-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            await EmitOutputAsync(packageId, $"Downloading {binaryName} from {url}…\n", ct).ConfigureAwait(false);

            var archive = Path.Combine(work, "pkg.tar.gz");
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var local = File.Create(archive);
                await remote.CopyToAsync(local, ct).ConfigureAwait(false);
            }

            await EmitOutputAsync(packageId, "Extracting archive…\n", ct).ConfigureAwait(false);
            var extract = await RunShellAsync(packageId, $"tar -xzf {Quote(archive)} -C {Quote(work)}", logger, ct).ConfigureAwait(false);
            if (!extract.Success)
                return extract;

            var extracted = Directory.EnumerateFiles(work, binaryName, SearchOption.AllDirectories).FirstOrDefault();
            if (extracted is null)
                return HostPackageOperationResult.Fail($"Archive did not contain {binaryName}");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(extracted, destPath, overwrite: true);
            TryMarkExecutable(destPath);
            await EmitOutputAsync(packageId, $"Installed {binaryName} to {destPath}\n", ct).ConfigureAwait(false);

            return ProxyProbe.BinaryOnPath(binaryName)
                ? HostPackageOperationResult.Ok($"{binaryName} installed to {destPath}")
                : HostPackageOperationResult.Fail($"{binaryName} installed but not found on PATH");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* ignore */ }
        }
    }

    private async Task<HostPackageOperationResult> RemoveBinaryAsync(
        string packageId,
        string path,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (!File.Exists(path))
            return HostPackageOperationResult.Ok("already removed");

        try
        {
            File.Delete(path);
            await EmitOutputAsync(packageId, $"removed {path}\n", ct).ConfigureAwait(false);
            return HostPackageOperationResult.Ok($"removed {path}");
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"Failed to delete {path}: {ex.Message}");
            return HostPackageOperationResult.Fail(ex.Message);
        }
    }

    private async Task EmitOutputAsync(string packageId, string chunk, CancellationToken ct)
    {
        if (_wsHub is null || string.IsNullOrEmpty(chunk))
            return;

        var sanitized = SanitizeOutputChunk(chunk);
        if (string.IsNullOrEmpty(sanitized))
            return;

        await _wsHub.SendOutputAsync(packageId, sanitized, ct).ConfigureAwait(false);
    }

    private static string SanitizeOutputChunk(string chunk)
    {
        var text = AnsiRegex.Replace(chunk, string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (text.Contains("inotify watch limit reached", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.Contains("Reading database ...", StringComparison.Ordinal))
                continue;
            kept.Add(line.TrimEnd());
        }

        return kept.Count == 0 ? string.Empty : string.Join('\n', kept) + '\n';
    }

    private async Task<HostPackageOperationResult> RunShellAsync(
        string packageId,
        string command,
        AppLogger? logger,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return HostPackageOperationResult.Fail("Shell package commands require Linux.");

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-lc", command + " 2>&1" },
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return HostPackageOperationResult.Fail("Failed to start shell.");

        var output = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;

            var chunk = line + "\n";
            output.Append(chunk);
            await EmitOutputAsync(packageId, chunk, ct).ConfigureAwait(false);
        }

        await Task.Run(() => proc.WaitForExit(), ct).ConfigureAwait(false);
        var log = output.ToString().Trim();

        if (proc.ExitCode != 0)
        {
            logger?.Warning(LoggerTypes.Application, $"Package command failed ({proc.ExitCode}): {log}");
            return HostPackageOperationResult.Fail(string.IsNullOrWhiteSpace(log) ? $"exit {proc.ExitCode}" : log);
        }

        logger?.Info(LoggerTypes.Application, $"Package command ok: {log}");
        return HostPackageOperationResult.Ok(log);
    }

    private static LinuxPackageManager? DetectPackageManager()
    {
        if (File.Exists("/usr/bin/apt-get"))
            return LinuxPackageManager.Apt;
        if (File.Exists("/usr/bin/dnf") || File.Exists("/usr/bin/yum"))
            return LinuxPackageManager.Dnf;
        return null;
    }

    private static string ResolveDockerPackageName() =>
        DetectPackageManager() == LinuxPackageManager.Dnf ? "docker" : "docker.io";

    private static string? ResolveLinuxArch() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux_amd64",
            Architecture.Arm64 => "linux_arm64",
            _ => null,
        };

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? TryProxyVersion(string provider, string binary) =>
        provider switch
        {
            "nginx" => TryVersionWithArgs(binary, "-v") ?? TryVersionWithArgs(binary, "-V"),
            _ => TryVersion(binary) ?? TryVersionWithArgs(binary, "--version"),
        };

    private static string? TryDockerVersion(string binary) =>
        TryVersionWithArgs(binary, "--version") ?? TryVersion(binary);

    private static string? TryVersionWithArgs(string binary, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var part in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(part);

            using var proc = Process.Start(psi);
            if (proc is null || !proc.WaitForExit(3000))
                return null;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            if (output.Length == 0)
                output = proc.StandardError.ReadToEnd().Trim();
            return output.Split('\n').FirstOrDefault()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryVersion(string binary) =>
        TryVersionWithArgs(binary, "version");

    private static void TryMarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // best-effort
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private enum LinuxPackageManager
    {
        Apt,
        Dnf,
    }
}

public sealed record HostPackageStatus(
    string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    string Category,
    bool Installed,
    [property: JsonPropertyName("binary_path")] string? BinaryPath,
    string? Version,
    bool Managed,
    [property: JsonPropertyName("install_blocked")] bool InstallBlocked = false,
    [property: JsonPropertyName("blocked_by")] string? BlockedBy = null,
    [property: JsonPropertyName("blocked_by_name")] string? BlockedByName = null);

public sealed record HostPackageOperationResult(
    bool Success,
    string Message,
    [property: JsonPropertyName("output")] string? Output = null)
{
    public static HostPackageOperationResult Ok(string message, string? output = null) =>
        new(true, message, output);

    public static HostPackageOperationResult Fail(string message, string? output = null) =>
        new(false, message, output);
}

public sealed record HostPackagesResponse(
    [property: JsonPropertyName("package_manager")] string? PackageManager,
    IReadOnlyList<HostPackageStatus> Packages,
    [property: JsonPropertyName("active_reverse_proxy")] string? ActiveReverseProxy = null);
