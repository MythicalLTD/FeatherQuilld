using System.Text.Json;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Per-mailbox spam filter toggle (Rspamd bypass list). Disabled = bypass spam scoring.
/// </summary>
public static class MailSpamHelper
{
    public static bool GetSpamFilterEnabled(AppConfig config, string email)
    {
        email = NormalizeEmail(email);
        var path = StatePath(config, email);
        if (!File.Exists(path))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("enabled", out var enabled))
                return enabled.ValueKind != JsonValueKind.False;
        }
        catch
        {
            // fall through
        }

        return true;
    }

    public static void SetSpamFilterEnabled(AppConfig config, string email, bool enabled)
    {
        email = NormalizeEmail(email);
        Directory.CreateDirectory(SpamFilterDir(config));
        var path = StatePath(config, email);
        if (enabled)
        {
            TryDelete(path);
        }
        else
        {
            var doc = new { email, enabled = false, updated_at = DateTimeOffset.UtcNow.ToString("O") };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }

        SyncBypassMap(config);
    }

    internal static void SyncBypassMap(AppConfig config)
    {
        var mapPath = BypassMapPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        var disabled = Directory.Exists(SpamFilterDir(config))
            ? Directory.GetFiles(SpamFilterDir(config), "*.json")
                .Select(ReadDisabledEmail)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        File.WriteAllLines(mapPath, disabled.Select(e => e!.Trim().ToLowerInvariant()));
    }

    private static string? ReadDisabledEmail(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
                return emailEl.GetString();
        }
        catch
        {
            // ignore
        }

        return Path.GetFileNameWithoutExtension(jsonPath).Replace('_', '@');
    }

    public static string SpamFilterDir(AppConfig config) =>
        Path.Combine(MailPaths.ConfigDir(config), "spam-filter");

    public static string BypassMapPath(AppConfig config) =>
        Path.Combine(MailPaths.ConfigDir(config), "rspamd", "custom", "feather-spam-bypass.map");

    private static string StatePath(AppConfig config, string email) =>
        Path.Combine(SpamFilterDir(config), SanitizeEmail(email) + ".json");

    private static string NormalizeEmail(string email) =>
        (email ?? "").Trim().ToLowerInvariant();

    private static string SanitizeEmail(string email) =>
        string.Concat(NormalizeEmail(email).Select(c => c == '@' ? '_' : c));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }
}
