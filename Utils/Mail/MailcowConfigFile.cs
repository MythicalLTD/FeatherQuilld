using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// mailcow.conf plumbing, kept side-effect free so the rules are unit testable:
/// mailcow's own <c>generate_config.sh</c> writes the file (only it knows every key
/// the compose file expects), the panel afterwards overrides a small, explicit set
/// of keys (hostname, bindings, ACME, API access).
/// </summary>
/// <remarks>
/// Upstream removed <c>mailcow.conf.example</c> and turned <c>.env</c> into a symlink
/// to <c>mailcow.conf</c>; the script refuses to run without that symlink. Parsing the
/// old example file — as an earlier revision of this code did — silently produced an
/// empty config, so the script is the primary path and <see cref="BuildFallbackConf"/>
/// only covers the case where the script is missing from the checkout.
/// </remarks>
public static class MailcowConfigFile
{
    /// <summary>Value mailcow expects in <c>DOCKER_COMPOSE_VERSION</c> for the compose plugin.</summary>
    public const string ComposeVersionNative = "native";

    /// <summary>
    /// Keys written by <see cref="BuildFallbackConf"/>. Mirrors the heredoc of mailcow's
    /// <c>generate_config.sh</c> so a checkout without the script still starts.
    /// </summary>
    public static IReadOnlyList<string> FallbackKeys { get; } = new[]
    {
        "MAILCOW_HOSTNAME", "MAILCOW_PASS_SCHEME", "DBNAME", "DBUSER", "DBPASS", "DBROOT", "REDISPASS",
        "HTTP_PORT", "HTTP_BIND", "HTTPS_PORT", "HTTPS_BIND", "HTTP_REDIRECT",
        "SMTP_PORT", "SMTPS_PORT", "SUBMISSION_PORT", "IMAP_PORT", "IMAPS_PORT", "POP_PORT", "POPS_PORT",
        "SIEVE_PORT", "DOVEADM_PORT", "SQL_PORT", "REDIS_PORT",
        "TZ", "COMPOSE_PROJECT_NAME", "DOCKER_COMPOSE_VERSION", "ACL_ANYONE", "MAILDIR_GC_TIME",
        "ADDITIONAL_SAN", "AUTODISCOVER_SAN", "ADDITIONAL_SERVER_NAMES",
        "SKIP_LETS_ENCRYPT", "ACME_DNS_CHALLENGE", "ACME_DNS_PROVIDER", "ACME_ACCOUNT_EMAIL",
        "ENABLE_SSL_SNI", "SKIP_IP_CHECK", "SKIP_HTTP_VERIFICATION", "SKIP_UNBOUND_HEALTHCHECK",
        "SKIP_CLAMD", "SKIP_OLEFY", "SKIP_SOGO", "SKIP_FTS", "FTS_HEAP", "FTS_PROCS",
        "ALLOW_ADMIN_EMAIL_LOGIN", "USE_WATCHDOG", "WATCHDOG_NOTIFY_BAN", "WATCHDOG_NOTIFY_START",
        "WATCHDOG_EXTERNAL_CHECKS", "WATCHDOG_VERBOSE", "LOG_LINES", "IPV4_NETWORK", "IPV6_NETWORK",
        "MAILDIR_SUB", "SOGO_EXPIRE_SESSION", "SOGO_URL_ENCRYPTION_KEY",
        "DOVECOT_MASTER_USER", "DOVECOT_MASTER_PASS", "WEBAUTHN_ONLY_TRUSTED_VENDORS",
        "SPAMHAUS_DQS_KEY", "ENABLE_IPV6", "DISABLE_NETFILTER_ISOLATION_RULE",
        "API_KEY", "API_KEY_READ_ONLY", "API_ALLOW_FROM",
    };

    /// <summary>Keys the panel always owns, no matter how mailcow.conf was created.</summary>
    public static IReadOnlyList<string> OverriddenKeys { get; } = new[]
    {
        "MAILCOW_HOSTNAME", "HTTP_PORT", "HTTP_BIND", "HTTPS_PORT", "HTTPS_BIND",
        "SKIP_LETS_ENCRYPT", "SKIP_IP_CHECK", "TZ", "API_KEY", "API_KEY_READ_ONLY", "API_ALLOW_FROM",
    };

    /// <summary>
    /// The keys the panel overrides. Binds the UI/API to loopback because the panel's
    /// reverse proxy terminates TLS; the API stays key-protected.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildOverrides(
        MailConfig mail,
        string hostname,
        string timezone,
        string apiKey,
        string apiKeyReadOnly,
        string apiAllowFrom,
        bool skipClamd)
    {
        var mailcow = mail.Mailcow;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MAILCOW_HOSTNAME"] = hostname,
            // The panel's reverse proxy owns 80/443 (and the public interface), so
            // mailcow listens on the configured ports on loopback only.
            ["HTTP_PORT"] = (mailcow.HttpPort > 0 ? mailcow.HttpPort : 8080).ToString(),
            ["HTTPS_PORT"] = (mailcow.HttpsPort > 0 ? mailcow.HttpsPort : 8443).ToString(),
            ["HTTP_BIND"] = "127.0.0.1",
            ["HTTPS_BIND"] = "127.0.0.1",
            ["SKIP_LETS_ENCRYPT"] = mailcow.SkipAcme ? "y" : "n",
            // The API is reached through the published loopback port, where docker
            // rewrites the source IP to the bridge gateway; the key is the real guard.
            ["SKIP_IP_CHECK"] = "y",
            ["TZ"] = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim(),
            ["API_KEY"] = apiKey,
            ["API_KEY_READ_ONLY"] = apiKeyReadOnly,
            ["API_ALLOW_FROM"] = apiAllowFrom,
            ["SKIP_CLAMD"] = skipClamd ? "y" : "n",
        };
    }

    /// <summary>
    /// Replaces <paramref name="overrides"/> in place and appends keys the file does not
    /// have yet, so a re-generated mailcow.conf keeps the panel's settings.
    /// </summary>
    public static List<string> ApplyOverrides(IEnumerable<string> lines, IReadOnlyDictionary<string, string> overrides)
    {
        var result = new List<string>(lines);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < result.Count; i++)
        {
            var text = result[i];
            foreach (var (key, value) in overrides)
            {
                if (!StartsWithKey(text, key))
                    continue;

                result[i] = $"{key}={value}";
                applied.Add(key);
                break;
            }
        }

        foreach (var (key, value) in overrides)
        {
            if (!applied.Contains(key))
                result.Add($"{key}={value}");
        }

        return result;
    }

    /// <summary>True when the line assigns exactly <paramref name="key"/> (not a prefix match).</summary>
    public static bool StartsWithKey(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            return false;

        // "#KEY=" and "KEY_OTHER=" must not match, only "KEY=".
        if (line.Length <= key.Length || line[key.Length] != '=')
            return false;

        return line[0] != '#';
    }

    /// <summary>
    /// Fallback config for checkouts that lost mailcow's generate_config.sh. Values not
    /// owned by the panel stay at the defaults the upstream script writes.
    /// </summary>
    public static List<string> BuildFallbackConf(
        string hostname,
        string timezone,
        IReadOnlyDictionary<string, string> overrides,
        Func<int, string> secretFactory)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MAILCOW_HOSTNAME"] = hostname,
            ["MAILCOW_PASS_SCHEME"] = "BLF-CRYPT",
            ["DBNAME"] = "mailcow",
            ["DBUSER"] = "mailcow",
            ["DBPASS"] = secretFactory(28),
            ["DBROOT"] = secretFactory(28),
            ["REDISPASS"] = secretFactory(28),
            ["HTTP_PORT"] = "80",
            ["HTTP_BIND"] = "",
            ["HTTPS_PORT"] = "443",
            ["HTTPS_BIND"] = "",
            ["HTTP_REDIRECT"] = "y",
            ["SMTP_PORT"] = "25",
            ["SMTPS_PORT"] = "465",
            ["SUBMISSION_PORT"] = "587",
            ["IMAP_PORT"] = "143",
            ["IMAPS_PORT"] = "993",
            ["POP_PORT"] = "110",
            ["POPS_PORT"] = "995",
            ["SIEVE_PORT"] = "4190",
            ["DOVEADM_PORT"] = "127.0.0.1:19991",
            ["SQL_PORT"] = "127.0.0.1:13306",
            ["REDIS_PORT"] = "127.0.0.1:7654",
            ["TZ"] = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim(),
            ["COMPOSE_PROJECT_NAME"] = MailcowPaths.ProjectName,
            ["DOCKER_COMPOSE_VERSION"] = ComposeVersionNative,
            ["ACL_ANYONE"] = "disallow",
            ["MAILDIR_GC_TIME"] = "7200",
            ["ADDITIONAL_SAN"] = "",
            ["AUTODISCOVER_SAN"] = "y",
            ["ADDITIONAL_SERVER_NAMES"] = "",
            ["SKIP_LETS_ENCRYPT"] = "n",
            ["ACME_DNS_CHALLENGE"] = "n",
            ["ACME_DNS_PROVIDER"] = "dns_xxx",
            ["ACME_ACCOUNT_EMAIL"] = "me@example.com",
            ["ENABLE_SSL_SNI"] = "n",
            ["SKIP_IP_CHECK"] = "n",
            ["SKIP_HTTP_VERIFICATION"] = "n",
            ["SKIP_UNBOUND_HEALTHCHECK"] = "n",
            ["SKIP_CLAMD"] = "n",
            ["SKIP_OLEFY"] = "n",
            ["SKIP_SOGO"] = "n",
            ["SKIP_FTS"] = "n",
            ["FTS_HEAP"] = "128",
            ["FTS_PROCS"] = "1",
            ["ALLOW_ADMIN_EMAIL_LOGIN"] = "n",
            ["USE_WATCHDOG"] = "y",
            ["WATCHDOG_NOTIFY_BAN"] = "n",
            ["WATCHDOG_NOTIFY_START"] = "y",
            ["WATCHDOG_EXTERNAL_CHECKS"] = "n",
            ["WATCHDOG_VERBOSE"] = "n",
            ["LOG_LINES"] = "9999",
            ["IPV4_NETWORK"] = "172.22.1",
            ["IPV6_NETWORK"] = "fd4d:6169:6c63:6f77::/64",
            ["MAILDIR_SUB"] = "Maildir",
            ["SOGO_EXPIRE_SESSION"] = "480",
            ["SOGO_URL_ENCRYPTION_KEY"] = secretFactory(16),
            ["DOVECOT_MASTER_USER"] = "",
            ["DOVECOT_MASTER_PASS"] = "",
            ["WEBAUTHN_ONLY_TRUSTED_VENDORS"] = "n",
            ["SPAMHAUS_DQS_KEY"] = "",
            ["ENABLE_IPV6"] = "",
            ["DISABLE_NETFILTER_ISOLATION_RULE"] = "n",
            ["API_KEY"] = "",
            ["API_KEY_READ_ONLY"] = "",
            ["API_ALLOW_FROM"] = "127.0.0.1",
        };

        var lines = new List<string>
        {
            "# Written by FeatherQuilld (mailcow's generate_config.sh was not found in the checkout).",
            "# Delete this file and re-run the mailcow package install after a fresh checkout to get",
            "# mailcow's own defaults for any key that is missing here.",
        };

        foreach (var key in FallbackKeys)
        {
            lines.Add($"{key}={(defaults.TryGetValue(key, out var value) ? value : string.Empty)}");
        }

        return ApplyOverrides(lines, overrides);
    }

    /// <summary>
    /// Non-interactive invocation of mailcow's own config generator: the hostname and
    /// timezone are passed as environment variables (the script only prompts when they
    /// are empty), <c>--dev</c> keeps it from checking out a branch, and the piped
    /// <c>y</c> answers the "overwrite the existing conf?" prompt.
    /// </summary>
    public static string BuildGenerateConfigCommand(string root, string hostname, string timezone, bool skipClamd)
    {
        var tz = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim();

        const string envLink = ".env";

        return string.Join(" && ",
            $"cd {root}",
            $"ln -sf {MailcowPaths.ConfFileName} {envLink}",
            "printf 'y\\n' | env "
                + $"MAILCOW_HOSTNAME={hostname} "
                + $"MAILCOW_TZ={tz} "
                + $"SKIP_CLAMD={(skipClamd ? "y" : "n")} "
                + "./generate_config.sh --dev");
    }

    /// <summary>Random alphanumeric secret in the shape mailcow's own generator uses.</summary>
    public static string GenerateSecret(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Math.Max(1, length));
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];

        return new string(chars);
    }

    /// <summary>API key material for mailcow's <c>api</c> table (hex, like the panel's own file).</summary>
    public static string GenerateApiKey() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
