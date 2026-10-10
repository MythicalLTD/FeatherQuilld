using System.Text;
using System.Text.RegularExpressions;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

public static class MailDnsHelper
{
    private const int DEFAULT_IMAPS_PORT = 993;
    private const int DEFAULT_SUBMISSION_PORT = 587;
    private const int DEFAULT_POP3S_PORT = 995;

    /// <summary>
    /// One record the panel should write for a mail domain.
    ///
    /// <paramref name="Optional"/> marks the client-autoconfiguration records (autodiscover,
    /// autoconfig, SRV): they make mail clients configure themselves but must never make a
    /// provisioning run fail. SRV records carry their structured parts as well, because DNS
    /// providers want them as separate fields.
    /// </summary>
    public sealed record DnsHintRecord(
        string Type,
        string Name,
        string Value,
        int? Priority = null,
        bool Optional = false,
        string? Service = null,
        string? Protocol = null,
        int? Port = null,
        int? Weight = null,
        string? Target = null);

    public static IReadOnlyList<DnsHintRecord> BuildHints(AppConfig config, string domain, IMailcowApi? mailcowApi = null)
    {
        domain = NormalizeDomain(domain);
        var hostname = ResolveMailHostname(config, domain);
        var host = hostname.TrimEnd('.');
        var imapPort = config.System.Mail.ImapPort > 0 ? config.System.Mail.ImapPort : DEFAULT_IMAPS_PORT;
        var submissionPort = config.System.Mail.SmtpPort > 0 ? config.System.Mail.SmtpPort : DEFAULT_SUBMISSION_PORT;
        var records = new List<DnsHintRecord>
        {
            new("MX", "@", hostname, 10),
            new("TXT", "@", BuildSpf(hostname)),
            // Client autoconfiguration: Outlook/Thunderbird read these before asking the user.
            new("CNAME", "autodiscover", host + ".", Optional: true),
            new("CNAME", "autoconfig", host + ".", Optional: true),
            new("SRV", "_imaps._tcp", $"0 1 {imapPort} {hostname}", Optional: true,
                Service: "_imaps", Protocol: "_tcp", Port: imapPort, Weight: 1, Target: hostname),
            new("SRV", "_submission._tcp", $"0 1 {submissionPort} {hostname}", Optional: true,
                Service: "_submission", Protocol: "_tcp", Port: submissionPort, Weight: 1, Target: hostname),
            new("SRV", "_pop3s._tcp", $"0 1 {DEFAULT_POP3S_PORT} {hostname}", Optional: true,
                Service: "_pop3s", Protocol: "_tcp", Port: DEFAULT_POP3S_PORT, Weight: 1, Target: hostname),
        };

        var dkim = TryGetDkimRecord(config, domain, mailcowApi);
        if (dkim is { } dkimRecord)
        {
            records.Add(new DnsHintRecord("TXT", dkimRecord.Selector + "._domainkey", dkimRecord.Value));
        }

        records.Add(new DnsHintRecord("TXT", "_dmarc", BuildDmarc(domain)));

        return records;
    }

    public static string BuildDmarc(string domain, string? ruaEmail = null)
    {
        domain = NormalizeDomain(domain);
        var rua = string.IsNullOrWhiteSpace(ruaEmail)
            ? $"postmaster@{domain}"
            : ruaEmail.Trim();
        return $"v=DMARC1; p=none; rua=mailto:{rua}";
    }

    public static bool IsDkimReady(AppConfig config, string domain, IMailcowApi? mailcowApi = null) =>
        TryGetDkimRecord(config, domain, mailcowApi) is not null;

    /// <summary>Hints plus whether DKIM TXT is available for auto-provision.</summary>
    public static object BuildHintsPayload(AppConfig config, string domain, IMailcowApi? mailcowApi = null)
    {
        domain = NormalizeDomain(domain);
        var records = BuildHints(config, domain, mailcowApi);
        var dkim = TryGetDkimRecord(config, domain, mailcowApi);
        return new
        {
            domain,
            mx_host = ResolveMailHostname(config, domain),
            dkim_ready = dkim is not null,
            dkim_selector = dkim?.Selector,
            dkim_record = dkim?.Value,
            dmarc_record = BuildDmarc(domain),
            records = records.Select(r => new
            {
                type = r.Type,
                name = r.Name,
                value = r.Value,
                priority = r.Priority,
                optional = r.Optional,
                service = r.Service,
                protocol = r.Protocol,
                port = r.Port,
                weight = r.Weight,
                target = r.Target,
            }),
        };
    }

    public static string BuildSpf(string hostname) =>
        "v=spf1 mx a:" + hostname.TrimEnd('.') + " -all";

    /// <summary>
    /// Hostname clients should use for MX/SPF.
    ///
    /// Prefers the configured <c>mail.hostname</c>, then the mail backend's own host (a remote
    /// mailcow usually has one) and only invents <c>mail.&lt;domain&gt;</c> when nothing is
    /// configured - that last fallback produced an MX record pointing at a host that does not
    /// exist whenever a node left <c>mail.hostname</c> empty.
    /// </summary>
    public static string ResolveMailHostname(AppConfig config, string domain)
    {
        var configured = ResolveConfiguredMailHost(config);

        return configured.Length > 0 ? configured + "." : "mail." + NormalizeDomain(domain) + ".";
    }

    /// <summary>
    /// Configured mail host without the per-domain fallback; empty when nothing is configured.
    /// </summary>
    public static string ResolveConfiguredMailHost(AppConfig config)
    {
        string?[] candidates =
        [
            config.System.Mail.Hostname,
            config.System.Mail.Mailcow.MailHost,
            config.System.Mail.Mailcow.Url,
        ];

        foreach (var candidate in candidates)
        {
            var value = NormalizeHostnameCandidate(candidate);
            if (value.Length > 0)
                return value;
        }

        return string.Empty;
    }

    /// <summary>Bare hostname from a hostname, URL or "host:port" value.</summary>
    private static string NormalizeHostnameCandidate(string? candidate)
    {
        var value = (candidate ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        if (value.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            value = uri.Host;

        var cut = value.IndexOfAny(['/', ':']);
        if (cut >= 0)
            value = value[..cut];

        return value.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// DKIM TXT for a domain. With the mailcow backend the key lives in mailcow's redis
    /// (read through the API); docker-mailserver keeps it as a file. The file candidates
    /// stay as a fallback so a half-migrated host still yields a record.
    /// </summary>
    public static (string Selector, string Value)? TryGetDkimRecord(AppConfig config, string domain, IMailcowApi? mailcowApi = null)
    {
        domain = NormalizeDomain(domain);
        var selector = (config.System.Mail.DkimSelector ?? "mail").Trim();
        if (selector.Length == 0)
            selector = "mail";

        if (mailcowApi is not null && MailBackendKind.IsMailcow(config.System.Mail.Backend))
        {
            var fromApi = TryGetDkimRecordFromApi(mailcowApi, domain);
            if (fromApi is { } apiRecord)
                return apiRecord;
        }

        var candidates = new[]
        {
            Path.Combine(MailPaths.MailStateDir(config), "opendkim", "keys", domain, $"{selector}.txt"),
            Path.Combine(MailPaths.MailStateDir(config), "opendkim", "keys", domain, "mail.txt"),
            Path.Combine(MailPaths.ConfigDir(config), "opendkim", "keys", domain, $"{selector}.txt"),
            // legacy mailcow layouts (< 2024 releases shipped key files); current
            // mailcow keeps them in redis, hence the API path above.
            MailcowPaths.DkimKeyFile(config, domain, selector),
            MailcowPaths.DkimKeyFile(config, domain, "dkim"),
            MailcowPaths.DkimKeyFile(config, domain, "mail"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var raw = File.ReadAllText(path);
                var value = ParseDkimTxt(raw);
                if (value.Length > 0)
                    return (selector, value);
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    /// <summary>
    /// Synchronous bridge to the (async) mailcow client. The hints endpoint runs without a
    /// synchronization context, so waiting here cannot deadlock; a missing key simply
    /// falls back to the file candidates.
    /// </summary>
    private static (string Selector, string Value)? TryGetDkimRecordFromApi(IMailcowApi mailcowApi, string domain)
    {
        try
        {
            var (selector, txt) = mailcowApi.GetDkimAsync(domain).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(txt))
                return null;

            return (string.IsNullOrWhiteSpace(selector) ? "dkim" : selector.Trim(), txt.Trim());
        }
        catch
        {
            return null;
        }
    }

    internal static string ParseDkimTxt(string raw)
    {
        var sb = new StringBuilder();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(';') || line.StartsWith('#'))
                continue;
            var cleaned = line.Trim().Trim('"');
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            sb.Append(cleaned);
        }

        var text = sb.ToString().Trim();
        if (text.StartsWith("v=DKIM1", StringComparison.OrdinalIgnoreCase))
            return text;

        var match = Regex.Match(text, @"(v=DKIM1[^""]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : text;
    }
}
