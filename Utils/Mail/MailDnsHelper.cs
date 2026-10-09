using System.Text;
using System.Text.RegularExpressions;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

public static class MailDnsHelper
{
    public sealed record DnsHintRecord(string Type, string Name, string Value, int? Priority = null);

    public static IReadOnlyList<DnsHintRecord> BuildHints(AppConfig config, string domain, IMailcowApi? mailcowApi = null)
    {
        domain = NormalizeDomain(domain);
        var hostname = ResolveMailHostname(config, domain);
        var records = new List<DnsHintRecord>
        {
            new("MX", "@", hostname, 10),
            new("TXT", "@", BuildSpf(hostname)),
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
            }),
        };
    }

    public static string BuildSpf(string hostname) =>
        "v=spf1 mx a:" + hostname.TrimEnd('.') + " -all";

    public static string ResolveMailHostname(AppConfig config, string domain)
    {
        var configured = (config.System.Mail.Hostname ?? "").Trim();
        if (configured.Length > 0)
            return configured.TrimEnd('.') + ".";

        return "mail." + NormalizeDomain(domain) + ".";
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
