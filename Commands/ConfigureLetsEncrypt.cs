using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using FeatherQuilld.Utils;
using FeatherQuilld.Utils.Startup;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace FeatherQuilld.Commands;

/// <summary>Let's Encrypt certificate issued (or found) for a web node FQDN.</summary>
public sealed record NodeTlsCertificate(string Domain, string CertPath, string KeyPath);

/// <summary>
/// FeatherWings-style Certbot flow: animated checklist, interactive port-80 handling,
/// recovery menu, then restart stopped services.
/// </summary>
public static class ConfigureLetsEncrypt
{
    private const string Teal = "#2DD4BF";
    private const string Ink = "#F4F4F5";
    private const string LiveDir = "/etc/letsencrypt/live";

    private static readonly string[] KnownWebUnits = ["nginx", "apache2", "httpd", "caddy"];
    private static readonly Dictionary<string, string> ProcessToUnit = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nginx"] = "nginx",
        ["apache2"] = "apache2",
        ["httpd"] = "httpd",
        ["caddy"] = "caddy",
    };

    private static readonly Regex SsProcessPattern = new(
        @"users:\(\(""([^""]+)""",
        RegexOptions.Compiled);

    private enum ChallengeType
    {
        Http,
        Dns,
    }

    private enum IssuanceMethod
    {
        Standalone,
        Nginx,
        Webroot,
    }

    private enum RecoveryAction
    {
        StopAndRetry,
        Retry,
        Nginx,
        Webroot,
        Dns,
        Recheck,
        ChangeDomain,
        Cancel,
    }

    private sealed record IssuanceConfig(ChallengeType Challenge, IssuanceMethod Method, string Webroot);

    private const string ManualAuthHook = """
        #!/bin/sh
        set -e
        WORK_DIR="$FEATHERQUILLD_CERTBOT_WORK"
        NAME="_acme-challenge.${CERTBOT_DOMAIN}"
        printf '{"name":"%s","value":"%s"}\n' "$NAME" "${CERTBOT_VALIDATION}" > "$WORK_DIR/challenge.json"
        ELAPSED=0
        while [ ! -f "$WORK_DIR/proceed" ]; do
          sleep 2
          ELAPSED=$((ELAPSED + 2))
          if [ "$ELAPSED" -ge 1800 ]; then
            echo "timed out waiting for DNS TXT record" >&2
            exit 1
          fi
        done
        exit 0
        """;

    private const string ManualCleanupHook = """
        #!/bin/sh
        exit 0
        """;

    public static string CertPathFor(string domain) =>
        Path.Combine(LiveDir, domain.Trim(), "fullchain.pem");

    public static string KeyPathFor(string domain) =>
        Path.Combine(LiveDir, domain.Trim(), "privkey.pem");

    public static bool CertificateExists(string domain)
    {
        var cert = CertPathFor(domain);
        var key = KeyPathFor(domain);
        return File.Exists(cert) && File.Exists(key);
    }

    /// <summary>
    /// When the panel uses HTTPS and the node is not behind a reverse proxy,
    /// ensure a Let's Encrypt cert exists for <paramref name="domain"/> (Wings parity).
    /// </summary>
    public static NodeTlsCertificate Ensure(
        string domain,
        string? contactEmail,
        string? serverIp,
        CancellationToken ct = default)
    {
        domain = NormalizeDomain(domain);
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException("Domain is required for TLS setup.");

        if (IPAddress.TryParse(domain, out _))
            throw new InvalidOperationException(
                "Let's Encrypt needs a hostname FQDN, not a bare IP. Use a domain or enable behind-proxy.");

        if (CertificateExists(domain))
            return AnnounceExisting(domain);

        AnsiConsole.WriteLine();
        ColoredConsole.WriteLine("&eNo Let's Encrypt certificate was found for this domain.&r");
        ColoredConsole.WriteLine(
            "&8FeatherQuilld needs a valid certificate when the panel uses HTTPS and this node is not behind a reverse proxy.&r");
        AnsiConsole.WriteLine();

        var generate = AnsiConsole.Confirm(
            ColoredConsole.ToMarkup(
                $"&7Generate a Let's Encrypt certificate now?&r &8(Certbot for {Markup.Escape(domain)})&r"),
            true);
        if (!generate)
        {
            throw new InvalidOperationException(
                $"A TLS certificate is required for {domain} generate one with certbot or place certificates under {LiveDir}/{domain}/.");
        }

        var email = ResolveEmail(contactEmail);
        var config = PromptVerificationSetup(domain, serverIp);
        var webroot = config.Webroot;

        while (true)
        {
            ClearScreen();
            Exception? failure = null;
            try
            {
                if (config.Challenge == ChallengeType.Dns)
                    RunDnsSequence(domain, email, ct);
                else
                {
                    if (config.Method == IssuanceMethod.Standalone)
                        PromptPortOccupancy(serverIp);
                    RunHttpSequence(domain, email, config.Method, webroot, stopServices: true, ct);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is null && CertificateExists(domain))
            {
                ClearScreen();
                var cert = CertPathFor(domain);
                ColoredConsole.WriteLine($"&a✓&r &7Let's Encrypt certificate issued for &f{domain}&r");
                ColoredConsole.WriteLineLiteral("&8", cert);
                AnsiConsole.WriteLine();
                return new NodeTlsCertificate(domain, cert, KeyPathFor(domain));
            }

            var message = failure?.Message
                          ?? $"Certbot finished but no certificate was found for {domain}.";
            var (action, nextDomain, nextConfig) = PromptRecovery(domain, message, config);
            domain = nextDomain;
            config = nextConfig;
            webroot = config.Webroot;

            switch (action)
            {
                case RecoveryAction.Recheck:
                    if (CertificateExists(domain))
                        return AnnounceExisting(domain);
                    ColoredConsole.WriteLine($"&eStill no certificate found for &f{domain}&r");
                    AnsiConsole.WriteLine();
                    continue;
                case RecoveryAction.Cancel:
                    throw new InvalidOperationException("configure cancelled");
                default:
                    continue;
            }
        }
    }

    private static NodeTlsCertificate AnnounceExisting(string domain)
    {
        var existing = CertPathFor(domain);
        ColoredConsole.WriteLine($"&a✓&r &7TLS certificate found for &f{domain}&r");
        ColoredConsole.WriteLineLiteral("&8", existing);
        AnsiConsole.WriteLine();
        return new NodeTlsCertificate(domain, existing, KeyPathFor(domain));
    }

    private static string ResolveEmail(string? contactEmail)
    {
        var email = (contactEmail ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            ColoredConsole.WriteLine($"&8Using contact email &f{email}&r");
            AnsiConsole.WriteLine();
            return email;
        }

        email = AnsiConsole.Prompt(
            new TextPrompt<string>(ColoredConsole.ToMarkup("&b›&r &7Let's Encrypt contact email&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .Validate(v =>
                    string.IsNullOrWhiteSpace(v) || !v.Contains('@')
                        ? ValidationResult.Error(ColoredConsole.ToMarkup("&cEnter a valid email.&r"))
                        : ValidationResult.Success()));
        return email.Trim();
    }

    private static IssuanceConfig PromptVerificationSetup(string domain, string? serverIp)
    {
        while (true)
        {
            ClearScreen();
            var report = BuildDomainDnsReport(domain, serverIp);
            AnsiConsole.Write(new Panel(new Rows(RenderDomainDnsRows(report)))
                .Header($"[bold {Teal}] domain DNS [/]", Justify.Center)
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.FromHex(Teal))
                .Padding(1, 0));
            AnsiConsole.WriteLine();

            var httpLabel = report.PointsToServer
                ? "HTTP verification (port 80) recommended"
                : "HTTP verification (port 80) requires DNS pointing here first";

            var choices = new List<VerificationChoice>
            {
                new(ChallengeType.Http, httpLabel),
                new(ChallengeType.Dns, "DNS TXT verification (works with CDN/proxy)"),
                new(null, "Re-check DNS"),
                new(null, "Cancel setup", Cancel: true),
            };

            var pick = AnsiConsole.Prompt(
                new SelectionPrompt<VerificationChoice>()
                    .Title(ColoredConsole.ToMarkup("&b&lHow should Let's Encrypt verify this domain?&r"))
                    .HighlightStyle(new Style(Color.FromHex(Teal), decoration: Decoration.Bold))
                    .AddChoices(choices)
                    .UseConverter(c => c.Label));

            if (pick.Cancel)
                throw new InvalidOperationException("configure cancelled");
            if (pick.Challenge is null)
                continue; // re-check DNS

            if (pick.Challenge == ChallengeType.Dns)
                return new IssuanceConfig(ChallengeType.Dns, IssuanceMethod.Standalone, DefaultWebrootPath());

            if (!report.PointsToServer)
            {
                var proceed = AnsiConsole.Confirm(
                    ColoredConsole.ToMarkup(
                        "&eDNS does not point to this server yet.&r &7HTTP verification will likely fail. Continue anyway?&r"),
                    false);
                if (!proceed)
                    continue;
            }

            return new IssuanceConfig(ChallengeType.Http, IssuanceMethod.Standalone, DefaultWebrootPath());
        }
    }

    private static DomainDnsReport BuildDomainDnsReport(string domain, string? serverIp)
    {
        var report = new DomainDnsReport
        {
            Domain = domain,
            ServerIp = (serverIp ?? "").Trim(),
        };

        try
        {
            var entries = Dns.GetHostEntry(domain);
            report.Ipv4 = entries.AddressList
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            report.Ipv6 = entries.AddressList
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(ip => ip.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            report.LookupError = ex.Message;
        }

        report.PointsToServer = !string.IsNullOrWhiteSpace(report.ServerIp)
                                && report.Ipv4.Contains(report.ServerIp);
        return report;
    }

    private static IEnumerable<IRenderable> RenderDomainDnsRows(DomainDnsReport report)
    {
        yield return new Markup(ColoredConsole.ToMarkup("&b&lDomain DNS check&r"));
        yield return new Text("");
        yield return new Markup(ColoredConsole.ToMarkup($"&7domain &f{Markup.Escape(report.Domain)}&r"));
        yield return new Markup(ColoredConsole.ToMarkup(
            $"&7this server &f{Markup.Escape(string.IsNullOrWhiteSpace(report.ServerIp) ? "unknown" : report.ServerIp)}&r"));
        yield return new Text("");

        if (!string.IsNullOrWhiteSpace(report.LookupError))
        {
            yield return new Markup(ColoredConsole.ToMarkup($"&eDNS lookup failed: {Markup.Escape(report.LookupError)}&r"));
        }
        else if (report.Ipv4.Count == 0)
        {
            yield return new Markup(ColoredConsole.ToMarkup("&eNo IPv4 A records found for this domain&r"));
        }
        else
        {
            var status = report.PointsToServer ? "&apoints to this server&r" : "&cdoes not point here&r";
            yield return new Markup(ColoredConsole.ToMarkup(
                $"&7A records &f{string.Join(", ", report.Ipv4)}&7 ({status})"));
        }

        if (report.Ipv6.Count > 0)
        {
            yield return new Markup(ColoredConsole.ToMarkup(
                $"&7AAAA records &f{string.Join(", ", report.Ipv6)}&r"));
        }

        if (!string.IsNullOrWhiteSpace(report.ServerIp) && !report.PointsToServer)
        {
            yield return new Text("");
            yield return new Markup(ColoredConsole.ToMarkup("&8Add or update this DNS record at your provider:&r"));
            yield return new Markup(ColoredConsole.ToMarkup("&f  Type:  A&r"));
            yield return new Markup(ColoredConsole.ToMarkup($"&f  Name:  {Markup.Escape(DnsRecordHostLabel(report.Domain))}&r"));
            yield return new Markup(ColoredConsole.ToMarkup($"&f  Value: {Markup.Escape(report.ServerIp)}&r"));
            yield return new Markup(ColoredConsole.ToMarkup($"&8  Full hostname: {Markup.Escape(report.Domain)}&r"));
        }

        yield return new Text("");
        yield return new Markup(ColoredConsole.ToMarkup(
            "&8HTTP needs port 80 reachable here. DNS TXT works behind Cloudflare/CDN/proxy.&r"));
    }

    private static string DnsRecordHostLabel(string domain)
    {
        var parts = domain.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? domain : parts[0];
    }

    private static void RunDnsSequence(string domain, string email, CancellationToken ct)
    {
        Exception? stepError = null;
        var steps = new List<(string Label, Func<ConfigureReporter, ConfigureStepResult> Work)>
        {
            ("Install Certbot", reporter =>
            {
                InstallCertbotIfNeeded(reporter, ct);
                return new ConfigureStepResult();
            }),
            ("Prepare DNS challenge", reporter =>
            {
                reporter.Detail("&7DNS TXT verification does not need port 80&r");
                return new ConfigureStepResult();
            }),
            ("Request Let's Encrypt certificate", reporter =>
            {
                RequestCertificateDns(domain, email, reporter, ct);
                return new ConfigureStepResult();
            }),
            ("Verify certificate auto-renewal", reporter =>
            {
                VerifyRenewal(reporter, ct);
                return new ConfigureStepResult();
            }),
        };

        var completed = new List<(string Label, ConfigureStepResult Result)>();
        var useLive = !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi;
        if (!useLive)
        {
            foreach (var (_, work) in steps)
                work(new ConfigureReporter());
            return;
        }

        // First two steps animate; the cert request step pauses Live to show TXT UI.
        AnsiConsole.Live(BuildCertChecklist(completed, 0, steps[0].Label))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var (label, work) = steps[i];
                    var reporter = new ConfigureReporter();
                    try
                    {
                        var result = RunStepAnimated(ctx, completed, i, label, work, reporter);
                        foreach (var detail in reporter.Details)
                            result.Details.Add(detail);
                        completed.Add((label, result));
                    }
                    catch (Exception ex)
                    {
                        stepError = ex;
                        return;
                    }
                }
            });

        if (stepError is not null)
            throw stepError;

        // TXT challenge needs a clear interactive screen (not Live).
        ClearScreen();
        try
        {
            var reporter = new ConfigureReporter();
            RequestCertificateDns(domain, email, reporter, ct);
            foreach (var detail in reporter.Details)
                ColoredConsole.WriteLine(detail);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        ClearScreen();
        completed.Add(("Request Let's Encrypt certificate", new ConfigureStepResult
        {
            Details = ["&7DNS TXT challenge completed&r"],
        }));

        AnsiConsole.Live(BuildCertChecklist(completed, completed.Count, steps[3].Label))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                var reporter = new ConfigureReporter();
                try
                {
                    var result = RunStepAnimated(ctx, completed, completed.Count, steps[3].Label, steps[3].Work, reporter);
                    foreach (var detail in reporter.Details)
                        result.Details.Add(detail);
                    completed.Add((steps[3].Label, result));
                    ctx.UpdateTarget(BuildCertChecklist(completed, completed.Count, "Ready"));
                    ctx.Refresh();
                    Thread.Sleep(200);
                }
                catch (Exception ex)
                {
                    stepError = ex;
                }
            });

        if (stepError is not null)
            throw stepError;
    }

    private static void RequestCertificateDns(
        string domain,
        string email,
        ConfigureReporter reporter,
        CancellationToken ct)
    {
        reporter.Progress("Starting DNS TXT verification…");
        reporter.Detail("&7Certbot will provide a TXT record to publish&r");

        var workDir = Directory.CreateTempSubdirectory("featherquilld-certbot-").FullName;
        try
        {
            var authHook = Path.Combine(workDir, "auth.sh");
            var cleanupHook = Path.Combine(workDir, "cleanup.sh");
            var challengePath = Path.Combine(workDir, "challenge.json");
            var proceedPath = Path.Combine(workDir, "proceed");

            File.WriteAllText(authHook, ManualAuthHook);
            File.WriteAllText(cleanupHook, ManualCleanupHook);
            UnixChmod(authHook, Convert.ToInt32("700", 8));
            UnixChmod(cleanupHook, Convert.ToInt32("700", 8));

            var certbotTask = Task.Run(() =>
            {
                var psi = QuietProcess(
                    "certbot",
                    "certonly",
                    "--manual",
                    "--preferred-challenges", "dns",
                    "-d", domain,
                    "--non-interactive",
                    "--agree-tos",
                    "-m", email,
                    "--manual-auth-hook", authHook,
                    "--manual-cleanup-hook", cleanupHook);
                psi.Environment["FEATHERQUILLD_CERTBOT_WORK"] = workDir;

                using var proc = Process.Start(psi)
                                 ?? throw new InvalidOperationException("Failed to start certbot.");
                var stdout = proc.StandardOutput.ReadToEndAsync(ct);
                var stderr = proc.StandardError.ReadToEndAsync(ct);
                proc.WaitForExitAsync(ct).GetAwaiter().GetResult();
                var output = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
                return (proc.ExitCode, output);
            }, ct);

            WaitForChallengeFile(challengePath, TimeSpan.FromMinutes(2), ct);
            var challenge = ReadAcmeChallengeFile(challengePath);

            ClearScreen();
            PromptDnsTxtRecordSetup(challenge);

            reporter.Progress("Checking DNS TXT record…");
            reporter.Detail("&7" + challenge.Name + "&r");
            WaitForTxtPropagation(challenge.Name, challenge.Value, ct);
            reporter.Detail("&7TXT record found in public DNS&r");

            File.WriteAllText(proceedPath, "1");

            var (exit, output) = certbotTask.GetAwaiter().GetResult();
            if (exit != 0)
                throw new InvalidOperationException(SummarizeCertbotError(output, exit));
            if (!CertificateExists(domain))
                throw new InvalidOperationException($"certbot finished but no certificate was found for {domain}");

            reporter.Detail("&7Certificate saved to &f" + CertPathFor(domain) + "&r");
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static void PromptDnsTxtRecordSetup(AcmeDnsChallenge challenge)
    {
        while (true)
        {
            AnsiConsole.Write(new Panel(new Rows(
                    new Markup(ColoredConsole.ToMarkup("&b&lDNS TXT verification&r")),
                    new Text(""),
                    new Markup(ColoredConsole.ToMarkup("&8Add this TXT record at your DNS provider:&r")),
                    new Text(""),
                    new Markup(ColoredConsole.ToMarkup("&f  Type:  TXT&r")),
                    new Markup(ColoredConsole.ToMarkup($"&f  Name:  {Markup.Escape(challenge.Name)}&r")),
                    new Markup(ColoredConsole.ToMarkup($"&f  Value: {Markup.Escape(challenge.Value)}&r")),
                    new Markup(ColoredConsole.ToMarkup("&8  TTL:   300 (or automatic)&r")),
                    new Text(""),
                    new Markup(ColoredConsole.ToMarkup("&8DNS changes can take a few minutes to propagate.&r"))))
                .Header($"[bold {Teal}] dns txt [/]", Justify.Center)
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.FromHex(Teal))
                .Padding(1, 0));
            AnsiConsole.WriteLine();

            var ready = AnsiConsole.Confirm(
                ColoredConsole.ToMarkup(
                    "&7I've added the DNS TXT record&r &8(FeatherQuilld will check public DNS before continuing)&r"),
                false);
            if (ready)
                return;

            ColoredConsole.WriteLine("&8Add the TXT record at your DNS provider, then choose Yes when it is ready.&r");
            AnsiConsole.WriteLine();
        }
    }

    private static void WaitForChallengeFile(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
                return;
            Thread.Sleep(500);
        }

        throw new InvalidOperationException("timed out waiting for certbot DNS challenge");
    }

    private static AcmeDnsChallenge ReadAcmeChallengeFile(string path)
    {
        var raw = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var name = doc.RootElement.GetProperty("name").GetString()?.Trim() ?? "";
        var value = doc.RootElement.GetProperty("value").GetString()?.Trim() ?? "";
        if (name.Length == 0 || value.Length == 0)
            throw new InvalidOperationException("invalid challenge payload");
        return new AcmeDnsChallenge(name, value);
    }

    private static void WaitForTxtPropagation(string name, string expected, CancellationToken ct)
    {
        ColoredConsole.WriteLine("&8Checking DNS TXT record…&r");
        ColoredConsole.WriteLineLiteral("&f", name);
        AnsiConsole.WriteLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (TxtRecordPublished(name, expected))
            {
                ColoredConsole.WriteLine("&a✓&r &7TXT record found in public DNS&r");
                AnsiConsole.WriteLine();
                return;
            }

            ColoredConsole.WriteLine("&8… still waiting for DNS propagation&r");
            Thread.Sleep(5000);
        }

        throw new InvalidOperationException($"timed out waiting for TXT record {name}");
    }

    private static bool TxtRecordPublished(string name, string expected)
    {
        try
        {
            if (TryLookupTxtWithDig(name, out var digRecords))
                return digRecords.Any(r => string.Equals(r.Trim('"'), expected, StringComparison.Ordinal));

            if (TryLookupTxtWithHost(name, out var hostRecords))
                return hostRecords.Any(r => r.Contains(expected, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryLookupTxtWithDig(string name, out List<string> records)
    {
        records = [];
        if (!BinaryOnPath("dig"))
            return false;

        try
        {
            var psi = QuietProcess("dig", "+short", "TXT", name);
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
                return false;

            records = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Trim().Trim('"').Replace("\" \"", "", StringComparison.Ordinal))
                .Where(l => l.Length > 0)
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLookupTxtWithHost(string name, out List<string> records)
    {
        records = [];
        if (!BinaryOnPath("host"))
            return false;

        try
        {
            var psi = QuietProcess("host", "-t", "TXT", name);
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
                return false;

            records = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void UnixChmod(string path, int mode)
    {
        try
        {
            var psi = QuietProcess("chmod", Convert.ToString(mode, 8), path);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch
        {
            /* best-effort */
        }
    }

    private sealed record DomainDnsReport
    {
        public string Domain { get; init; } = "";
        public string ServerIp { get; init; } = "";
        public List<string> Ipv4 { get; set; } = [];
        public List<string> Ipv6 { get; set; } = [];
        public bool PointsToServer { get; set; }
        public string? LookupError { get; set; }
    }

    private sealed record AcmeDnsChallenge(string Name, string Value);

    private sealed record VerificationChoice(ChallengeType? Challenge, string Label, bool Cancel = false);

    private static void PromptPortOccupancy(string? serverIp)
    {
        if (!TcpPortInUse(80) && !TcpPortInUse(443))
            return;

        var listeners80 = DetectPortListeners(80);
        var listeners443 = DetectPortListeners(443);
        var units = UnitsHoldingHttpPorts();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Rows(BuildPortOccupancyRows(listeners80, listeners443, units, serverIp)))
            .Header($"[bold {Teal}] port 80/443 [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));
        AnsiConsole.WriteLine();

        if (units.Count == 0)
        {
            ColoredConsole.WriteLine(
                "&ePort 80 is busy but no known service (nginx/caddy/apache) was detected.&r");
            ColoredConsole.WriteLine(
                "&8Free the port manually, or Certbot standalone will fail.&r");
            AnsiConsole.WriteLine();
            return;
        }

        var stop = AnsiConsole.Confirm(
            ColoredConsole.ToMarkup(
                $"&7Stop &f{string.Join(", ", units)}&7 now, issue the certificate, then start them again?&r"),
            true);
        if (!stop)
        {
            throw new InvalidOperationException(
                $"Port 80 is in use by {string.Join(", ", units)}. Stop those services (or choose behind-proxy) before Let's Encrypt standalone.");
        }

        AnsiConsole.WriteLine();
    }

    private static IEnumerable<IRenderable> BuildPortOccupancyRows(
        IReadOnlyList<string> listeners80,
        IReadOnlyList<string> listeners443,
        IReadOnlyList<string> units,
        string? serverIp)
    {
        yield return new Markup(ColoredConsole.ToMarkup("&b&lSomething is already listening on HTTP ports&r"));
        yield return new Text("");
        yield return new Markup(ColoredConsole.ToMarkup(
            listeners80.Count > 0
                ? $"&7port &f80&7 → &f{string.Join(", ", listeners80)}&r"
                : "&7port &f80&7 → &8free&r"));
        yield return new Markup(ColoredConsole.ToMarkup(
            listeners443.Count > 0
                ? $"&7port &f443&7 → &f{string.Join(", ", listeners443)}&r"
                : "&7port &f443&7 → &8free&r"));
        if (units.Count > 0)
            yield return new Markup(ColoredConsole.ToMarkup($"&7services &f{string.Join(", ", units)}&r"));
        yield return new Text("");
        yield return new Markup(ColoredConsole.ToMarkup(
            "&8Certbot standalone needs port 80. FeatherQuilld can stop these services, get the certificate, then start them again.&r"));
        if (!string.IsNullOrWhiteSpace(serverIp))
        {
            yield return new Text("");
            yield return new Markup(ColoredConsole.ToMarkup(
                $"&8Also confirm DNS A/AAAA for this hostname points at &f{serverIp}&8.&r"));
        }
    }

    private static void RunHttpSequence(
        string domain,
        string email,
        IssuanceMethod method,
        string webroot,
        bool stopServices,
        CancellationToken ct)
    {
        var session = new ServiceSession();
        Exception? stepError = null;

        var steps = new List<(string Label, Func<ConfigureReporter, ConfigureStepResult> Work)>
        {
            ("Install Certbot", reporter =>
            {
                InstallCertbotIfNeeded(reporter, ct);
                return new ConfigureStepResult();
            }),
            ("Prepare web services", reporter =>
            {
                if (method == IssuanceMethod.Standalone && stopServices)
                    session.PrepareStandalone(reporter, ct);
                else
                    reporter.Detail("&7Leaving web services running&r");
                return new ConfigureStepResult();
            }),
            ("Request Let's Encrypt certificate", reporter =>
            {
                try
                {
                    RequestCertificate(domain, email, method, webroot, reporter, ct);
                    return new ConfigureStepResult();
                }
                finally
                {
                    session.Restart(reporter, ct);
                }
            }),
            ("Verify certificate auto-renewal", reporter =>
            {
                VerifyRenewal(reporter, ct);
                return new ConfigureStepResult();
            }),
        };

        var completed = new List<(string Label, ConfigureStepResult Result)>();
        var useLive = !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi;

        if (!useLive)
        {
            foreach (var (label, work) in steps)
            {
                var reporter = new ConfigureReporter();
                try
                {
                    work(reporter);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(ex.Message, ex);
                }
            }

            return;
        }

        AnsiConsole.Live(BuildCertChecklist(completed, 0, steps[0].Label))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    var (label, work) = steps[i];
                    ctx.UpdateTarget(BuildCertChecklist(completed, i, label));
                    ctx.Refresh();

                    var reporter = new ConfigureReporter();
                    ConfigureStepResult result;
                    try
                    {
                        result = RunStepAnimated(ctx, completed, i, label, work, reporter);
                    }
                    catch (Exception ex)
                    {
                        stepError = ex;
                        result = new ConfigureStepResult { Status = ConfigureStepStatus.Failed };
                        result.Details.Add("&c" + Markup.Escape(ex.Message) + "&r");
                        foreach (var detail in reporter.Details)
                            result.Details.Add(detail);
                        completed.Add((label, result));
                        ctx.UpdateTarget(BuildCertChecklist(completed, steps.Count, "Failed"));
                        ctx.Refresh();
                        Thread.Sleep(250);
                        return;
                    }

                    foreach (var detail in reporter.Details)
                        result.Details.Add(detail);
                    completed.Add((label, result));
                    Thread.Sleep(120);
                }

                ctx.UpdateTarget(BuildCertChecklist(completed, steps.Count, "Ready"));
                ctx.Refresh();
                Thread.Sleep(200);
            });

        if (stepError is not null)
            throw stepError;
    }

    private static ConfigureStepResult RunStepAnimated(
        LiveDisplayContext ctx,
        IReadOnlyList<(string Label, ConfigureStepResult Result)> completed,
        int activeIndex,
        string label,
        Func<ConfigureReporter, ConfigureStepResult> work,
        ConfigureReporter reporter)
    {
        ConfigureStepResult? result = null;
        Exception? error = null;

        var workTask = Task.Run(() =>
        {
            try
            {
                result = work(reporter);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        var frame = 0;
        while (!workTask.IsCompleted)
        {
            var status = reporter.Status;
            var activeLabel = string.IsNullOrWhiteSpace(status)
                ? label + new string('.', frame % 3 + 1)
                : $"{label} {status}";
            ctx.UpdateTarget(BuildCertChecklist(completed, activeIndex, activeLabel));
            ctx.Refresh();
            Thread.Sleep(160);
            frame++;
        }

        workTask.GetAwaiter().GetResult();
        if (error is not null)
            throw error;
        return result ?? new ConfigureStepResult();
    }

    private static IRenderable BuildCertChecklist(
        IReadOnlyList<(string Label, ConfigureStepResult Result)> completed,
        int activeIndex,
        string activeLabel)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[bold {Ink}]Let's Encrypt[/]"),
            new Text(""),
        };

        for (var i = 0; i < completed.Count; i++)
        {
            var (label, result) = completed[i];
            var glyph = result.Status switch
            {
                ConfigureStepStatus.Failed => "[bold red]✗[/]",
                ConfigureStepStatus.Warning => "[bold yellow]![/]",
                _ => $"[bold {Teal}]✓[/]",
            };
            rows.Add(new Markup($"  {glyph} [{Ink}]{Markup.Escape(label)}[/]"));
            foreach (var detail in result.Details)
                rows.Add(new Markup($"      [grey]›[/] {ColoredConsole.ToMarkup(detail)}"));
        }

        if (activeIndex < completed.Count + 1 && !string.Equals(activeLabel, "Ready", StringComparison.Ordinal)
            && !string.Equals(activeLabel, "Failed", StringComparison.Ordinal)
            && completed.Count == activeIndex)
        {
            rows.Add(new Markup($"  [bold {Teal}]◉[/] [{Ink}]{Markup.Escape(activeLabel)}[/]"));
        }

        return new Panel(new Rows(rows))
            .Header($"[bold {Teal}] certificate [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0);
    }

    private static (RecoveryAction Action, string Domain, IssuanceConfig Config) PromptRecovery(
        string domain,
        string message,
        IssuanceConfig config)
    {
        ClearScreen();
        RenderFailure(domain, message);

        var choices = new List<RecoveryChoice>
        {
            new(RecoveryAction.StopAndRetry, "Stop web servers and retry (standalone HTTP)"),
            new(RecoveryAction.Retry, "Retry with the same method"),
        };
        if (config.Challenge != ChallengeType.Dns)
            choices.Add(new(RecoveryAction.Dns, "Try DNS TXT verification"));
        if (config.Method != IssuanceMethod.Nginx && SystemdUnitActive("nginx"))
            choices.Add(new(RecoveryAction.Nginx, "Try again using the nginx plugin"));
        if (config.Method != IssuanceMethod.Webroot)
            choices.Add(new(RecoveryAction.Webroot, "Try again using webroot mode"));
        choices.Add(new(RecoveryAction.Recheck, "I already installed the certificate check again"));
        choices.Add(new(RecoveryAction.ChangeDomain, "Use a different domain"));
        choices.Add(new(RecoveryAction.Cancel, "Cancel setup"));

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<RecoveryChoice>()
                .Title(ColoredConsole.ToMarkup("&b&lWhat would you like to do?&r"))
                .HighlightStyle(new Style(Color.FromHex(Teal), decoration: Decoration.Bold))
                .AddChoices(choices)
                .UseConverter(c => c.Label));

        var next = config;
        var nextDomain = domain;

        switch (pick.Action)
        {
            case RecoveryAction.StopAndRetry:
                next = new IssuanceConfig(ChallengeType.Http, IssuanceMethod.Standalone, config.Webroot);
                PromptPortOccupancy(null);
                break;
            case RecoveryAction.Dns:
                next = new IssuanceConfig(ChallengeType.Dns, IssuanceMethod.Standalone, config.Webroot);
                break;
            case RecoveryAction.Nginx:
                next = new IssuanceConfig(ChallengeType.Http, IssuanceMethod.Nginx, config.Webroot);
                break;
            case RecoveryAction.Webroot:
                var webroot = AnsiConsole.Prompt(
                    new TextPrompt<string>(ColoredConsole.ToMarkup("&b›&r &7Webroot path&r"))
                        .PromptStyle(new Style(Color.FromHex(Teal)))
                        .DefaultValue(config.Webroot)
                        .Validate(v => string.IsNullOrWhiteSpace(v)
                            ? ValidationResult.Error(ColoredConsole.ToMarkup("&cWebroot is required.&r"))
                            : ValidationResult.Success()))
                    .Trim();
                next = new IssuanceConfig(ChallengeType.Http, IssuanceMethod.Webroot, webroot);
                break;
            case RecoveryAction.ChangeDomain:
                nextDomain = NormalizeDomain(AnsiConsole.Prompt(
                    new TextPrompt<string>(ColoredConsole.ToMarkup("&b›&r &7Node domain&r"))
                        .PromptStyle(new Style(Color.FromHex(Teal)))
                        .DefaultValue(domain)
                        .Validate(v => string.IsNullOrWhiteSpace(v)
                            ? ValidationResult.Error(ColoredConsole.ToMarkup("&cDomain is required.&r"))
                            : ValidationResult.Success())));
                break;
        }

        return (pick.Action, nextDomain, next);
    }

    private static void RenderFailure(string domain, string message)
    {
        var rows = new List<IRenderable>
        {
            new Markup(ColoredConsole.ToMarkup("&c&lCertificate setup needs attention&r")),
            new Text(""),
            new Markup(ColoredConsole.ToMarkup($"&7domain &f{Markup.Escape(domain)}&r")),
            new Text(""),
            new Markup(ColoredConsole.ToMarkup($"&f{Markup.Escape(message)}&r")),
        };

        AnsiConsole.Write(new Panel(new Rows(rows))
            .Header("[bold yellow] ! certbot [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Padding(1, 0));
        AnsiConsole.WriteLine();
    }

    private static void InstallCertbotIfNeeded(ConfigureReporter reporter, CancellationToken ct)
    {
        if (BinaryOnPath("certbot"))
        {
            reporter.Detail("&7Certbot is already installed&r");
            InstallMissingPlugins(reporter, ct);
            return;
        }

        if (!BinaryOnPath("apt-get"))
            throw new InvalidOperationException("certbot is not installed install certbot on this machine and try again.");

        reporter.Progress("Installing Certbot and plugins…");
        RunShell(ct, "apt-get", "update");
        var packages = PluginPackages();
        var args = new List<string> { "install", "-y", "-qq" };
        args.AddRange(packages);
        RunShell(ct, "apt-get", args.ToArray());
        reporter.Detail("&7Installed &f" + string.Join(", ", packages) + "&r");
    }

    private static void InstallMissingPlugins(ConfigureReporter reporter, CancellationToken ct)
    {
        if (!BinaryOnPath("apt-get"))
            return;

        var missing = new List<string>();
        foreach (var pkg in PluginPackages())
        {
            if (pkg == "certbot")
                continue;
            if (!DpkgInstalled(pkg))
                missing.Add(pkg);
        }

        if (missing.Count == 0)
            return;

        reporter.Progress("Installing Certbot plugins…");
        var args = new List<string> { "install", "-y", "-qq" };
        args.AddRange(missing);
        RunShell(ct, "apt-get", args.ToArray());
        reporter.Detail("&7Installed &f" + string.Join(", ", missing) + "&r");
    }

    private static string[] PluginPackages()
    {
        var packages = new List<string> { "certbot" };
        if (BinaryOnPath("nginx") || SystemdUnitKnown("nginx"))
            packages.Add("python3-certbot-nginx");
        if (BinaryOnPath("apache2") || BinaryOnPath("httpd")
            || SystemdUnitKnown("apache2") || SystemdUnitKnown("httpd"))
            packages.Add("python3-certbot-apache");
        return packages.ToArray();
    }

    private static void RequestCertificate(
        string domain,
        string email,
        IssuanceMethod method,
        string webroot,
        ConfigureReporter reporter,
        CancellationToken ct)
    {
        reporter.Progress($"Requesting certificate ({MethodLabel(method)})…");

        var args = new List<string>
        {
            "certonly",
            "-d", domain,
            "--non-interactive",
            "--agree-tos",
            "-m", email,
        };

        switch (method)
        {
            case IssuanceMethod.Nginx:
                args.Add("--nginx");
                reporter.Detail("&7Using Certbot nginx plugin&r");
                break;
            case IssuanceMethod.Webroot:
                var root = string.IsNullOrWhiteSpace(webroot) ? DefaultWebrootPath() : webroot;
                args.Add("--webroot");
                args.Add("-w");
                args.Add(root);
                reporter.Detail("&7Using webroot at &f" + root + "&r");
                break;
            default:
                args.Add("--standalone");
                args.Add("--preferred-challenges");
                args.Add("http");
                reporter.Detail("&7Using standalone mode on port 80&r");
                break;
        }

        var (exit, output) = RunCertbot(ct, args.ToArray());
        if (exit != 0)
            throw new InvalidOperationException(SummarizeCertbotError(output, exit));

        if (!CertificateExists(domain))
            throw new InvalidOperationException($"certbot finished but no certificate was found for {domain}");

        reporter.Detail("&7Certificate saved to &f" + CertPathFor(domain) + "&r");
    }

    private static void VerifyRenewal(ConfigureReporter reporter, CancellationToken ct)
    {
        reporter.Progress("Checking certificate auto-renewal…");
        var (exit, output) = RunCertbot(ct, "renew", "--dry-run");
        if (exit != 0)
        {
            reporter.Detail("&eRenewal dry-run failed check certbot timer manually&r");
            var summary = ExtractCertbotDetail(output) ?? LastMeaningfulLine(output);
            if (!string.IsNullOrWhiteSpace(summary))
                reporter.Detail("&8" + summary + "&r");
            return;
        }

        reporter.Detail("&7Auto-renewal dry-run succeeded&r");
    }

    private static (int ExitCode, string Output) RunCertbot(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "certbot",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["TERM"] = "dumb";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Failed to start certbot.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        proc.WaitForExitAsync(ct).GetAwaiter().GetResult();
        var output = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
        return (proc.ExitCode, output);
    }

    internal static string SummarizeCertbotError(string output, int exitCode)
    {
        var detail = ExtractCertbotDetail(output);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var lower = detail.ToLowerInvariant();
            if (lower.Contains("nxdomain") || lower.Contains("dns problem"))
                return detail + " create an A/AAAA record for this hostname pointing at this machine, then retry";
            if (lower.Contains("connection") || lower.Contains("timeout") || lower.Contains("unreachable"))
                return detail + " check firewall / that port 80 is reachable from the internet";
            if (lower.Contains("unauthorized") || lower.Contains("invalid response"))
                return detail + " HTTP-01 failed; stop whatever answers on port 80 for this hostname, then retry";
            if (lower.Contains("rate limit"))
                return detail + " wait for the Let's Encrypt rate limit window or use another domain";
            return detail;
        }

        var hint = ExtractCertbotHint(output);
        if (!string.IsNullOrWhiteSpace(hint))
            return hint;

        var line = LastMeaningfulLine(output);
        if (string.IsNullOrWhiteSpace(line)
            || line.Contains("community.letsencrypt.org", StringComparison.OrdinalIgnoreCase)
            || line.Contains("See the logfile", StringComparison.OrdinalIgnoreCase))
            return $"certbot failed (exit {exitCode}) check /var/log/letsencrypt/letsencrypt.log";

        return line;
    }

    internal static string? ExtractCertbotDetail(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Detail:", StringComparison.OrdinalIgnoreCase))
                return line["Detail:".Length..].Trim();
            if (line.Contains("DNS problem:", StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return null;
    }

    private static string? ExtractCertbotHint(string output)
    {
        var lines = output.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("Hint:", StringComparison.OrdinalIgnoreCase))
                continue;
            var hint = lines[i].Trim();
            if (hint.StartsWith("Hint:", StringComparison.OrdinalIgnoreCase))
                hint = hint["Hint:".Length..].Trim();
            return hint;
        }

        return null;
    }

    private static string LastMeaningfulLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            var lower = line.ToLowerInvariant();
            if (lower.StartsWith("usage:") || lower.StartsWith("options:"))
                continue;
            if (lower.Contains("community.letsencrypt.org"))
                continue;
            if (lower.Contains("see the logfile"))
                continue;
            if (lower.StartsWith("ask for help"))
                continue;
            return line;
        }

        return "";
    }

    private sealed class ServiceSession
    {
        private readonly List<string> _stopped = [];

        public void PrepareStandalone(ConfigureReporter reporter, CancellationToken ct)
        {
            reporter.Progress("Preparing ports 80 and 443…");

            foreach (var port in new[] { 80, 443 })
            {
                if (TcpPortInUse(port))
                {
                    var listeners = DetectPortListeners(port);
                    reporter.Detail(listeners.Count > 0
                        ? $"&7Port {port} is used by &f{string.Join(", ", listeners)}&r"
                        : $"&7Port {port} is in use&r");
                }
                else
                {
                    reporter.Detail($"&7Port {port} is free&r");
                }
            }

            var units = UnitsHoldingHttpPorts();
            if (units.Count == 0)
            {
                if (TcpPortInUse(80))
                    throw new InvalidOperationException(
                        "port 80 is in use but no known web service could be stopped automatically");
                reporter.Detail("&7No web services needed to be stopped&r");
                return;
            }

            foreach (var unit in units)
            {
                reporter.Detail("&7Stopping &f" + unit + "&r");
                RunSystemctl(ct, "stop", unit);
                _stopped.Add(unit);
            }

            // Give listeners a moment to release the socket.
            Thread.Sleep(400);
            if (TcpPortInUse(80))
                throw new InvalidOperationException("port 80 is still in use after stopping web services");

            reporter.Detail("&7Ports 80 and 443 are ready for Certbot standalone mode&r");
        }

        public void Restart(ConfigureReporter reporter, CancellationToken ct)
        {
            if (_stopped.Count == 0)
                return;

            reporter.Progress("Restarting web services…");
            for (var i = _stopped.Count - 1; i >= 0; i--)
            {
                var unit = _stopped[i];
                reporter.Detail("&7Starting &f" + unit + "&r");
                try
                {
                    RunSystemctl(ct, "start", unit);
                }
                catch (Exception ex)
                {
                    reporter.Detail($"&eWarning: failed to restart {unit} run: systemctl start {unit} ({ex.Message})&r");
                }
            }
        }
    }

    private static List<string> UnitsHoldingHttpPorts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var units = new List<string>();

        void Add(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit) || !seen.Add(unit))
                return;
            if (!SystemdUnitActive(unit))
                return;
            units.Add(unit);
        }

        foreach (var port in new[] { 80, 443 })
        {
            if (!TcpPortInUse(port))
                continue;
            foreach (var unit in MapProcessesToUnits(DetectPortListeners(port)))
                Add(unit);
        }

        if (TcpPortInUse(80) || TcpPortInUse(443))
        {
            foreach (var unit in KnownWebUnits)
            {
                if (SystemdUnitActive(unit))
                    Add(unit);
            }
        }

        units.Sort(StringComparer.Ordinal);
        return units;
    }

    private static List<string> DetectPortListeners(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ss",
                ArgumentList = { "-H", "-lntp", $"sport = :{port}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return [];
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return ParsePortListeners(output);
        }
        catch
        {
            return [];
        }
    }

    internal static List<string> ParsePortListeners(string ssOutput)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = new List<string>();
        foreach (Match match in SsProcessPattern.Matches(ssOutput))
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Length == 0 || !seen.Add(name))
                continue;
            processes.Add(name);
        }

        processes.Sort(StringComparer.Ordinal);
        return processes;
    }

    private static List<string> MapProcessesToUnits(IEnumerable<string> processes)
    {
        var units = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            if (!ProcessToUnit.TryGetValue(process, out var unit))
                continue;
            if (!SystemdUnitKnown(unit) || !seen.Add(unit))
                continue;
            units.Add(unit);
        }

        units.Sort(StringComparer.Ordinal);
        return units;
    }

    private static bool TcpPortInUse(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static bool BinaryOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return true;
        }

        return false;
    }

    private static bool SystemdUnitKnown(string unit)
    {
        try
        {
            var psi = QuietProcess("systemctl", "cat", unit);
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SystemdUnitActive(string unit)
    {
        try
        {
            var psi = QuietProcess("systemctl", "is-active", "--quiet", unit);
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool DpkgInstalled(string package)
    {
        try
        {
            var psi = QuietProcess("dpkg-query", "-W", "-f=${Status}", package);
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && output.Contains("install ok installed", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void RunSystemctl(CancellationToken ct, params string[] args)
    {
        var psi = QuietProcess("systemctl", args);
        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Failed to start systemctl.");
        var stderr = proc.StandardError.ReadToEndAsync(ct).GetAwaiter().GetResult();
        proc.WaitForExitAsync(ct).GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"systemctl {args[0]} failed" : stderr.Trim());
    }

    private static void RunShell(CancellationToken ct, string fileName, params string[] args)
    {
        var psi = QuietProcess(fileName, args);
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        proc.WaitForExitAsync(ct).GetAwaiter().GetResult();
        if (proc.ExitCode == 0)
            return;

        var output = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
            ? $"{fileName} failed (exit {proc.ExitCode})"
            : output);
    }

    private static ProcessStartInfo QuietProcess(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["SYSTEMD_COLORS"] = "0";
        psi.Environment["TERM"] = "dumb";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static string DefaultWebrootPath()
    {
        foreach (var path in new[] { "/var/www/html", "/usr/share/nginx/html" })
        {
            if (Directory.Exists(path))
                return path;
        }

        return "/var/www/html";
    }

    private static string MethodLabel(IssuanceMethod method) => method switch
    {
        IssuanceMethod.Nginx => "nginx plugin",
        IssuanceMethod.Webroot => "webroot",
        _ => "standalone",
    };

    private static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();

    private static void ClearScreen()
    {
        try
        {
            AnsiConsole.Clear();
        }
        catch
        {
            Console.Write("\u001b[2J\u001b[H");
        }
    }

    private sealed record RecoveryChoice(RecoveryAction Action, string Label);
}
