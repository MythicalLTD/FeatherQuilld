using FeatherQuilld.Utils.Config.System;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Paths and container names for a mailcow: dockerized installation.
/// mailcow keeps its own layout (compose file plus <c>data/</c> next to it), so
/// these deliberately do not reuse the docker-mailserver paths.
/// </summary>
public static class MailcowPaths
{
    /// <summary>docker compose project prefix mailcow uses for all its containers.</summary>
    public const string ProjectName = "mailcowdockerized";

    public const string ComposeFileName = "docker-compose.yml";
    public const string ConfFileName = "mailcow.conf";

    /// <summary>Container that answers the API after a successful start.</summary>
    public const string ApiContainerName = "mailcowdockerized-nginx-mailcow-1";

    /// <summary>Directory that holds the mailcow repository/compose file and its data.</summary>
    public static string Root(AppConfig config)
    {
        var configured = config.System.Mail.Mailcow.Path;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        return Path.Combine(MailPaths.Root(config), "mailcow");
    }

    public static string ComposeFile(AppConfig config) =>
        Path.Combine(Root(config), ComposeFileName);

    public static string ConfFile(AppConfig config) =>
        Path.Combine(Root(config), ConfFileName);

    /// <summary>API key used for the panel -> mailcow calls (kept outside mailcow.conf).</summary>
    public static string ApiKeyFile(AppConfig config) =>
        Path.Combine(Root(config), "feather-api-key");

    public static string DataDir(AppConfig config) =>
        Path.Combine(Root(config), "data");

    /// <summary>mailcow stores generated DKIM keys as <c>data/dkim/&lt;domain&gt;/&lt;selector&gt;.txt</c>.</summary>
    public static string DkimKeyFile(AppConfig config, string domain, string selector) =>
        Path.Combine(DataDir(config), "dkim", domain.Trim().ToLowerInvariant(), selector + ".txt");

    /// <summary>Dovecot vmail tree (used for vacation/sieve fallbacks and diagnostics).</summary>
    public static string VmailDir(AppConfig config) =>
        Path.Combine(DataDir(config), "vmail");
}
