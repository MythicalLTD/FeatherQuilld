using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Dns;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.SystemInfo;
using FeatherQuilld.Utils.WebSpaces;
using FeatherQuilld.Utils.WebSpaces.Malware;
using FeatherQuilld.Utils.Mail;
using FeatherQuilld.Utils.Ftp;
using FeatherQuilld.Utils.WebSpaces.Disk;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.Startup;

/// <summary>Boot-time / on-demand checks: paths, disk limiter, WebSpaces, proxy, panel.</summary>
public static class StartupSelfTest
{
    public static BootStepResult Run(
        AppConfig config,
        WebSpaceStore spaces,
        AppLogger logger,
        BootReporter? reporter = null,
        DiagnosticsRegistry? diagnostics = null)
    {
        var checks = Collect(config, spaces, logger, reporter);
        diagnostics?.SetBootChecks(checks);

        var failures = checks.Count(c => c.Status == "fail");
        var warnings = checks.Count(c => c.Status == "warn");
        var result = new BootStepResult();

        if (failures > 0)
        {
            result.Status = BootStepStatus.Failed;
            logger.Error(LoggerTypes.SelfTest, $"Self-tests finished with {failures} failure(s), {warnings} warning(s)");
        }
        else if (warnings > 0)
        {
            result.Status = BootStepStatus.Warning;
            logger.Warning(LoggerTypes.SelfTest, $"Self-tests finished with {warnings} warning(s)");
        }
        else
        {
            logger.Info(LoggerTypes.SelfTest, "Self-tests passed");
        }

        reporter?.Detail(failures > 0
            ? $"{failures} failed · {warnings} warn"
            : warnings > 0 ? $"{warnings} warning(s)" : "ok");

        return result;
    }

    /// <summary>Re-run checks for the diagnostics API without failing boot.</summary>
    public static IReadOnlyList<DiagnosticCheck> RunLive(
        AppConfig config,
        WebSpaceStore spaces,
        AppLogger logger,
        DiagnosticsRegistry diagnostics)
    {
        var checks = Collect(config, spaces, logger, reporter: null);
        diagnostics.SetLiveChecks(checks);
        return checks;
    }

    private static List<DiagnosticCheck> Collect(
        AppConfig config,
        WebSpaceStore spaces,
        AppLogger logger,
        BootReporter? reporter)
    {
        if (reporter is not null)
            logger.Info(LoggerTypes.SelfTest, "Running startup self-tests…");
        else
            logger.Debug(LoggerTypes.SelfTest, "Running live diagnostics checks…");
        logger.Debug(LoggerTypes.SelfTest, $"debug={config.Debug} quiet={config.Quiet}");

        var checks = new List<DiagnosticCheck>();

        checks.Add(CheckDirectory("data", config.System.Data, logger, reporter));
        checks.Add(CheckDirectory("vmounts", config.System.VmountDirectory, logger, reporter));
        checks.Add(CheckDirectory("tmp", config.System.TmpDirectory, logger, reporter));
        checks.Add(CheckWritableProbe(config.System.Data, logger, reporter));
        checks.AddRange(CheckDiskLimiter(config, logger, reporter));
        checks.Add(CheckWebSpaces(spaces, config, logger, reporter));
        checks.Add(CheckDockerNetwork(config, logger, reporter));
        checks.AddRange(CheckProxy(config, logger, reporter));
        checks.Add(CheckPowerDns(config, logger, reporter));
        checks.AddRange(CheckMail(config, logger, reporter));
        checks.Add(CheckFtp(config, logger, reporter));
        checks.Add(CheckClamAv(logger, reporter));
        checks.AddRange(CheckModSecurity(config, spaces, logger, reporter));
        checks.Add(CheckDockerCli(logger, reporter));
        checks.Add(CheckPanel(config, logger, reporter));

        return checks;
    }

    private static DiagnosticCheck CheckDirectory(
        string label, string path, AppLogger logger, BootReporter? reporter)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (!Directory.Exists(path))
            {
                Fail($"Directory missing: {label} → {path}", logger, reporter);
                return new DiagnosticCheck($"dir.{label}", "fail", $"Directory missing: {label}", path);
            }

            logger.Debug(LoggerTypes.SelfTest, $"dir ok [{label}] {path}");
            return new DiagnosticCheck($"dir.{label}", "ok", $"Directory ok: {label}", path);
        }
        catch (Exception ex)
        {
            Fail($"Cannot access {label} ({path}): {ex.Message}", logger, reporter);
            return new DiagnosticCheck($"dir.{label}", "fail", $"Cannot access {label}", ex.Message);
        }
    }

    private static DiagnosticCheck CheckWritableProbe(
        string dataPath, AppLogger logger, BootReporter? reporter)
    {
        var probe = Path.Combine(dataPath, $".featherquilld-write-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "ok");
            var roundTrip = File.ReadAllText(probe);
            File.Delete(probe);
            if (roundTrip != "ok")
            {
                Warn($"Write probe mismatch under {dataPath}", logger, reporter);
                return new DiagnosticCheck("write_probe", "warn", "Write probe mismatch", dataPath);
            }

            logger.Debug(LoggerTypes.SelfTest, $"write probe ok → {dataPath}");
            return new DiagnosticCheck("write_probe", "ok", "Data directory writable", dataPath);
        }
        catch (Exception ex)
        {
            Fail($"Data directory not writable ({dataPath}): {ex.Message}", logger, reporter);
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* ignore */ }
            return new DiagnosticCheck("write_probe", "fail", "Data directory not writable", ex.Message);
        }
    }

    private static IEnumerable<DiagnosticCheck> CheckDiskLimiter(
        AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        var mode = config.System.EffectiveDiskLimiterMode;
        logger.Info(LoggerTypes.Disk,
            $"Disk limiter: configured={config.System.DiskLimiterMode} effective={mode} quotas.enabled={config.System.Quotas.Enabled}");

        yield return new DiagnosticCheck(
            "disk_limiter",
            "ok",
            $"Disk limiter mode: {mode}",
            $"configured={config.System.DiskLimiterMode}");

        if (mode == DiskLimiterModeKind.FuseQuota)
        {
            _ = FuseQuotaBinaryProvisioner.Ensure(config.System, logger);
        }

        if (FuseQuotaLimiter.TryResolveBinaryPath(config.System, out var binPath))
        {
            logger.Info(LoggerTypes.Disk, $"fusequota binary → {binPath}");
            yield return new DiagnosticCheck("fusequota_binary", "ok", "fusequota binary found", binPath);
        }
        else
        {
            logger.Debug(LoggerTypes.Disk, $"fusequota not found (configured={config.System.FusequotaPath})");
            if (mode == DiskLimiterModeKind.FuseQuota)
            {
                Fail("FuseQuota mode is on but binary was not found.", logger, reporter);
                yield return new DiagnosticCheck("fusequota_binary", "fail", "fusequota binary missing", config.System.FusequotaPath);
            }
            else
            {
                yield return new DiagnosticCheck("fusequota_binary", "ok", "fusequota not required", null);
            }
        }

        if (mode == DiskLimiterModeKind.FuseQuota && OperatingSystem.IsLinux() && !File.Exists("/dev/fuse"))
        {
            Warn("/dev/fuse missing — install fuse3", logger, reporter);
            yield return new DiagnosticCheck("fuse_device", "warn", "/dev/fuse missing — install fuse3", null);
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return new DiagnosticCheck(
                "fuse_device",
                File.Exists("/dev/fuse") ? "ok" : "warn",
                File.Exists("/dev/fuse") ? "/dev/fuse present" : "/dev/fuse missing",
                null);
        }
    }

    private static DiagnosticCheck CheckWebSpaces(
        WebSpaceStore spaces, AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        var list = spaces.List();
        logger.Info(LoggerTypes.WebSpaces, $"{list.Count} WebSpace(s) under {config.System.Data}");

        var missing = 0;
        foreach (var space in list)
        {
            var dataPath = spaces.DataPath(space.Uuid);
            logger.Debug(LoggerTypes.WebSpaces,
                $"webspace {space.Uuid} webplate={space.WebPlateId} runtime={space.Runtime} domains=[{string.Join(",", space.Domains)}]");

            if (!Directory.Exists(dataPath))
            {
                missing++;
                Warn($"WebSpace {space.Uuid} metadata present but data dir missing", logger, reporter);
            }
        }

        if (missing > 0)
            return new DiagnosticCheck("webspaces", "warn", $"{list.Count} WebSpace(s), {missing} missing data dir", null);

        return new DiagnosticCheck("webspaces", "ok", $"{list.Count} WebSpace(s) loaded", null);
    }

    private static DiagnosticCheck CheckDockerNetwork(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!DockerNetworkEnsurer.ShouldEnsure(config.Docker))
        {
            logger.Debug(LoggerTypes.Application,
                $"Docker network mode={config.Docker.Network.NetworkMode} (built-in — skip ensure)");
            return new DiagnosticCheck("docker_network", "ok", "Docker network ensure not required", null);
        }

        var name = config.Docker.Network.NetworkMode.Trim();
        try
        {
            DockerNetworkEnsurer.Ensure(config.Docker, logger);
            return new DiagnosticCheck("docker_network", "ok", $"Docker network ready: {name}", null);
        }
        catch (Exception ex)
        {
            Fail($"Docker network '{name}' could not be ensured: {ex.Message}", logger, reporter);
            return new DiagnosticCheck("docker_network", "fail", $"Docker network missing: {name}", ex.Message);
        }
    }

    private static IEnumerable<DiagnosticCheck> CheckProxy(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!config.System.Proxy.Enabled)
        {
            logger.Debug(LoggerTypes.Proxy, "Reverse proxy disabled");
            yield return new DiagnosticCheck("proxy", "ok", "Reverse proxy disabled", null);
            yield break;
        }

        var provider = ProxyProbe.NormalizeProvider(config.System.Proxy.Provider);
        logger.Info(LoggerTypes.Proxy, $"Proxy enabled provider={provider}");

        yield return new DiagnosticCheck(
            "proxy",
            "ok",
            $"Reverse proxy enabled ({provider})",
            $"provider={provider}");

        if (provider == "traefik")
        {
            var binary = ProxyProbe.ResolveBinary("traefik");
            var staticConfig = File.Exists("/etc/traefik/traefik.yml");
            if (binary is null)
            {
                Warn("traefik is not installed or not on PATH", logger, reporter);
                yield return new DiagnosticCheck(
                    "proxy.binary",
                    "fail",
                    "traefik binary not found on PATH",
                    "Install traefik from the host package manager.");
            }
            else
            {
                yield return new DiagnosticCheck("proxy.binary", "ok", "traefik binary found", binary);
            }

            if (!staticConfig)
            {
                Warn("Traefik static config missing at /etc/traefik/traefik.yml — reinstall traefik from package manager", logger, reporter);
                yield return new DiagnosticCheck(
                    "proxy.traefik_static",
                    "warn",
                    "Traefik static config missing",
                    "/etc/traefik/traefik.yml");
            }
            else
            {
                yield return new DiagnosticCheck("proxy.traefik_static", "ok", "Traefik static config present", "/etc/traefik/traefik.yml");
            }
        }
        else
        {
            var binary = ProxyProbe.ResolveBinary(provider);
            if (binary is null)
            {
                Warn($"{provider} is not installed or not on PATH — domains will not be served until it is installed", logger, reporter);
                yield return new DiagnosticCheck(
                    "proxy.binary",
                    "fail",
                    $"{provider} binary not found on PATH",
                    $"Install {provider} on the web node or change system.proxy.provider in config.yml");
            }
            else
            {
                logger.Debug(LoggerTypes.Proxy, $"{provider} binary → {binary}");
                yield return new DiagnosticCheck("proxy.binary", "ok", $"{provider} binary found", binary);
            }
        }

        var acmeEmail = (config.System.Proxy.AcmeEmail ?? "").Trim();
        if (string.IsNullOrWhiteSpace(acmeEmail))
        {
            Warn("No node acme_email — HTTPS uses each WebSpace owner's account email; Traefik still needs a node fallback", logger, reporter);
            yield return new DiagnosticCheck(
                "proxy.acme_email",
                "warn",
                "No node ACME fallback email",
                "Certificates use the site owner's account email. Set system.proxy.acme_email for Traefik or as operator fallback.");
        }
        else
        {
            yield return new DiagnosticCheck("proxy.acme_email", "ok", "Node ACME fallback email configured", acmeEmail);
        }
    }

    private static DiagnosticCheck CheckPowerDns(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!PowerDnsProbe.IsAvailable(config))
        {
            var binary = PowerDnsProbe.ResolveBinary();
            if (binary is null)
            {
                Warn("PowerDNS not installed — install the powerdns package for node DNS hosting", logger, reporter);
                return new DiagnosticCheck(
                    "dns.powerdns",
                    "warn",
                    "PowerDNS not installed",
                    "Install the powerdns package from the host package manager for node DNS hosting.");
            }

            Warn("PowerDNS is installed but the HTTP API is not reachable", logger, reporter);
            return new DiagnosticCheck(
                "dns.powerdns",
                "warn",
                "PowerDNS API not reachable",
                "pdns_server is installed but the HTTP API is not responding on the configured URL.");
        }

        logger.Debug(LoggerTypes.SelfTest, "PowerDNS available");
        return new DiagnosticCheck("dns.powerdns", "ok", "PowerDNS API available", config.System.Dns.PowerDnsApiUrl);
    }

    private static DiagnosticCheck CheckFtp(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!config.Ftp.Enabled)
            return new DiagnosticCheck("ftp.listener", "ok", "Classic FTP disabled in config", null);

        if (FtpProbe.IsListening(config.Ftp))
        {
            logger.Debug(LoggerTypes.SelfTest, $"FTP listening on port {config.Ftp.Port}");
            return new DiagnosticCheck("ftp.listener", "ok", $"FTP listening on port {config.Ftp.Port}", null);
        }

        Warn($"FTP enabled but not listening on port {config.Ftp.Port}", logger, reporter);
        return new DiagnosticCheck(
            "ftp.listener",
            "warn",
            "FTP enabled but listener not ready",
            $"port={config.Ftp.Port}");
    }

    private static IEnumerable<DiagnosticCheck> CheckMail(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!config.System.Mail.Enabled)
        {
            yield return new DiagnosticCheck("mail.stack", "ok", "Mail server disabled in config", null);
            yield break;
        }

        if (MailProbe.ContainerRunning(config))
        {
            yield return new DiagnosticCheck("mail.stack", "ok", "Mail server container running", MailPaths.ContainerName);
        }
        else if (MailProbe.DockerOnPath() && File.Exists(MailPaths.ComposeFile(config)))
        {
            Warn("Mail server compose exists but container is not running", logger, reporter);
            yield return new DiagnosticCheck("mail.stack", "warn", "Mail server container not running", MailPaths.ComposeFile(config));
        }
        else
        {
            Warn("Mail server not installed — install the mailserver package for SMTP/IMAP", logger, reporter);
            yield return new DiagnosticCheck(
                "mail.stack",
                "warn",
                "Mail server not installed",
                "Install the mailserver package from the host package manager.");
        }

        if (!MailProbe.ContainerRunning(config))
            yield break;

        var anyPortWarn = false;
        foreach (var (port, label) in new[] { (25, "SMTP"), (config.System.Mail.SmtpPort, "submission"), (config.System.Mail.ImapPort, "IMAP") })
        {
            if (MailProbe.PortOpen(port))
            {
                yield return new DiagnosticCheck($"mail.port.{port}", "ok", $"{label} port {port} listening", null);
            }
            else
            {
                anyPortWarn = true;
                Warn($"Mail {label} port {port} is not listening — open firewall if needed", logger, reporter);
                yield return new DiagnosticCheck($"mail.port.{port}", "warn", $"{label} port {port} not listening", null);
            }
        }

        if (anyPortWarn)
        {
            yield return new DiagnosticCheck(
                "mail.deliverability",
                "warn",
                "Mail container is up but required ports are closed",
                "Open 25/587/993/tcp and configure PTR/rDNS for the node public IP before expecting inbound MX or good outbound reputation.");
        }
        else
        {
            yield return new DiagnosticCheck(
                "mail.deliverability",
                "ok",
                "Mail ports listening — verify PTR/rDNS and SPF at the provider",
                "Outbound deliverability still depends on PTR matching the mail hostname and clean IP reputation.");
        }
    }

    private static DiagnosticCheck CheckClamAv(AppLogger logger, BootReporter? reporter)
    {
        if (ClamAvProbe.IsAvailable())
            return new DiagnosticCheck("malware.clamav", "ok", "ClamAV available", ClamAvProbe.ResolveBinary());

        Warn("ClamAV not installed — install the clamav package for malware scans", logger, reporter);
        return new DiagnosticCheck(
            "malware.clamav",
            "warn",
            "ClamAV not installed",
            "Install the clamav package from the host package manager.");
    }

    private static IEnumerable<DiagnosticCheck> CheckModSecurity(
        AppConfig config,
        WebSpaceStore spaces,
        AppLogger logger,
        BootReporter? reporter)
    {
        var provider = ProxyProbe.NormalizeProvider(config.System.Proxy.Provider);
        var wafSpaces = spaces.List().Count(s => s.WafEnabled);

        if (!string.Equals(provider, "nginx", StringComparison.OrdinalIgnoreCase))
        {
            yield return new DiagnosticCheck(
                "proxy.modsecurity",
                "ok",
                wafSpaces > 0
                    ? "ModSecurity applies to nginx only — WAF headers/denylist still active"
                    : "ModSecurity is nginx-only",
                $"provider={provider}");
            yield break;
        }

        if (ModSecurityProbe.IsAvailable())
        {
            yield return new DiagnosticCheck(
                "proxy.modsecurity",
                "ok",
                "ModSecurity + CRS available for nginx WAF",
                ModSecurityProbe.ResolveRulesFile());
            yield break;
        }

        if (wafSpaces > 0)
        {
            Warn("nginx WAF enabled on WebSpaces but ModSecurity/CRS is not installed", logger, reporter);
            yield return new DiagnosticCheck(
                "proxy.modsecurity",
                "warn",
                "ModSecurity not installed",
                "Install the modsecurity package for OWASP CRS on nginx.");
        }
        else
        {
            yield return new DiagnosticCheck(
                "proxy.modsecurity",
                "warn",
                "ModSecurity not installed",
                "Optional — install modsecurity for nginx OWASP CRS WAF.");
        }
    }

    private static DiagnosticCheck CheckDockerCli(AppLogger logger, BootReporter? reporter)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "docker.exe" : "docker",
                ArgumentList = { "version", "--format", "{{.Server.Version}}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                Warn("docker CLI not found — Docker WebPlates cannot be installed", logger, reporter);
                return new DiagnosticCheck("docker.cli", "fail", "Docker CLI not found", "Install Docker on the web node");
            }

            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                Warn("docker version timed out", logger, reporter);
                return new DiagnosticCheck("docker.cli", "warn", "Docker CLI did not respond in time", null);
            }

            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd().Trim();
                Warn($"docker not usable: {err}", logger, reporter);
                return new DiagnosticCheck("docker.cli", "fail", "Docker daemon not reachable", err.Length > 0 ? err : null);
            }

            var version = proc.StandardOutput.ReadToEnd().Trim();
            logger.Debug(LoggerTypes.SelfTest, $"docker ok → {version}");
            return new DiagnosticCheck("docker.cli", "ok", "Docker available", version.Length > 0 ? version : null);
        }
        catch (Exception ex)
        {
            Warn($"docker check failed: {ex.Message}", logger, reporter);
            return new DiagnosticCheck("docker.cli", "fail", "Docker CLI not found", ex.Message);
        }
    }

    private static DiagnosticCheck CheckPanel(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!config.HasPanelCredentials())
        {
            Warn("No panel credentials — WebSpace create (pull from panel) will fail until configured", logger, reporter);
            return new DiagnosticCheck("panel", "warn", "No panel credentials configured", null);
        }

        logger.Debug(LoggerTypes.SelfTest, $"Panel → {config.Remote.Panel}");
        return new DiagnosticCheck("panel", "ok", "Panel credentials present", config.Remote.Panel);
    }

    private static void Fail(string message, AppLogger logger, BootReporter? reporter)
    {
        logger.Error(LoggerTypes.SelfTest, message);
        reporter?.Detail($"FAIL {message}");
    }

    private static void Warn(string message, AppLogger logger, BootReporter? reporter)
    {
        logger.Warning(LoggerTypes.SelfTest, message);
        reporter?.Detail($"WARN {message}");
    }
}
