using System.Text;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Per-mailbox autoresponder for mailcow. mailcow's REST API has no vacation
/// endpoint, and ManageSieve would require the mailbox password (which the panel
/// deliberately does not store). Instead the script is pushed into the mailbox'
/// sieve store with <c>doveadm sieve</c> inside the dovecot container, which runs
/// with the privileges needed and takes effect without a container restart.
/// </summary>
public static class MailcowSieve
{
    /// <summary>Sieve script name the panel owns per mailbox.</summary>
    public const string ScriptName = "feather-autorespond";

    /// <summary>Builds the vacation script. Kept public so tests can assert on the exact script.</summary>
    public static string BuildAutorespondScript(string subject, string body, string email)
    {
        subject = Escape(subject);
        body = Escape(body);
        var sb = new StringBuilder();
        sb.AppendLine("require [\"vacation\", \"variables\"];");
        sb.AppendLine();
        sb.AppendLine("# managed by FeatherQuilld - do not edit manually");
        sb.AppendLine("# :matches (\":contains\") keeps the autoresponder from replying to");
        sb.AppendLine("# list/automated mail, which is what most providers expect.");
        sb.AppendLine("if not exists [\"list-id\", \"list-help\", \"list-unsubscribe\"] {");
        sb.AppendLine("  vacation");
        sb.AppendLine("    :days 1");
        sb.AppendLine($"    :subject \"{subject}\"");
        sb.AppendLine($"    :addresses [\"{Escape(email)}\"]");
        sb.AppendLine($"    \"{body}\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>True when the mailbox has an active panel managed autoresponder.</summary>
    public static bool IsActive(AppConfig config, string email)
    {
        var container = MailcowDocker.FindContainer("dovecot-mailcow");
        if (container is null)
            return false;

        try
        {
            var listing = MailcowDocker.Exec(container, ["doveadm", "sieve", "list", "-u", email]);
            return listing.Contains(ScriptName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(AppConfig config, string email, string subject, string body)
    {
        var container = MailcowDocker.FindContainer("dovecot-mailcow")
            ?? throw new InvalidOperationException("mailcow dovecot container is not running.");

        var script = BuildAutorespondScript(subject, body, email);
        // put -a writes the script and activates it in one step.
        MailcowDocker.Exec(container,
            ["doveadm", "sieve", "put", "-u", email, "-a", ScriptName], script);
    }

    public static void Remove(AppConfig config, string email)
    {
        var container = MailcowDocker.FindContainer("dovecot-mailcow")
            ?? throw new InvalidOperationException("mailcow dovecot container is not running.");

        try
        {
            MailcowDocker.Exec(container, ["doveadm", "sieve", "deactivate", "-u", email, ScriptName]);
        }
        catch
        {
            // already inactive/absent
        }

        try
        {
            MailcowDocker.Exec(container, ["doveadm", "sieve", "delete", "-u", email, ScriptName]);
        }
        catch
        {
            // already deleted
        }
    }

    private static string Escape(string value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", " ")
            .Replace("\n", " ");
}
