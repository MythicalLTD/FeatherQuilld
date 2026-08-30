using System.Text;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Builds docker-mailserver Sieve vacation scripts and resolves install paths.
/// DMS installs <c>{email}.dovecot.sieve</c> from config at user setup, and also
/// honors <c>.dovecot.sieve</c> under the mailbox home for immediate effect.
/// </summary>
public static class MailVacationHelper
{
    public static string BuildSieveScript(string email, string subject, string body, int days = 1)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        subject = string.IsNullOrWhiteSpace(subject) ? "Out of office" : subject.Trim();
        body = body ?? "";
        days = Math.Clamp(days, 1, 30);

        var sb = new StringBuilder();
        sb.AppendLine(@"require [""vacation""];");
        sb.Append("vacation :days ").Append(days);
        sb.Append(" :subject \"").Append(EscapeSieve(subject)).Append('"');
        if (!string.IsNullOrWhiteSpace(email))
            sb.Append(" :addresses [\"").Append(EscapeSieve(email)).Append("\"]");
        sb.AppendLine();
        sb.Append('"').Append(EscapeSieve(body)).Append('"').Append(';');
        sb.AppendLine();
        return sb.ToString();
    }

    public static string ConfigSievePath(AppConfig config, string email) =>
        Path.Combine(MailPaths.ConfigDir(config), SanitizeEmail(email) + ".dovecot.sieve");

    /// <summary>Possible live maildir locations for an active sieve script.</summary>
    public static IReadOnlyList<string> MaildirSievePaths(AppConfig config, string email)
    {
        email = SanitizeEmail(email);
        var at = email.LastIndexOf('@');
        if (at <= 0 || at >= email.Length - 1)
            return Array.Empty<string>();

        var local = email[..at];
        var domain = email[(at + 1)..];
        var baseDir = Path.Combine(MailPaths.MailDataDir(config), domain, local);
        return
        [
            Path.Combine(baseDir, "home", ".dovecot.sieve"),
            Path.Combine(baseDir, ".dovecot.sieve"),
        ];
    }

    public static void WriteAutorespond(AppConfig config, string email, string subject, string body)
    {
        var script = BuildSieveScript(email, subject, body);
        var configPath = ConfigSievePath(config, email);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, script);

        foreach (var path in MaildirSievePaths(config, email))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, script);
            }
            catch
            {
                // Maildir may not exist until first delivery — config copy covers next setup.
            }
        }
    }

    public static void RemoveAutorespond(AppConfig config, string email)
    {
        TryDelete(ConfigSievePath(config, email));
        foreach (var path in MaildirSievePaths(config, email))
            TryDelete(path);
    }

    internal static string EscapeSieve(string value)
    {
        // Sieve quoted strings: backslash and double-quote must be escaped.
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);
    }

    private static string SanitizeEmail(string email) =>
        email.Trim().ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
