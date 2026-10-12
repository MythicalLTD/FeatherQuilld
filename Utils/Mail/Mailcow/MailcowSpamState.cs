using System.Text.Json;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Local bookkeeping for the per-mailbox spam filter switch on mailcow. mailcow
/// has no read endpoint for a mailbox' rspamd score, so the panel remembers the
/// state it set (see <see cref="MailcowBackend.SetSpamFilterEnabled"/>) and keeps
/// the effective value in mailcow as the rspamd score.
/// </summary>
internal static class MailcowSpamState
{
    public static string StatePath(AppConfig config) =>
        Path.Combine(MailcowPaths.Root(config), "feather-spam-state.json");

    public static bool GetEnabled(AppConfig config, string email)
    {
        var state = Load(config);
        return !state.TryGetValue(email, out var enabled) || enabled;
    }

    public static void SetEnabled(AppConfig config, string email, bool enabled)
    {
        var state = Load(config);
        state[email] = enabled;
        Save(config, state);
    }

    private static Dictionary<string, bool> Load(AppConfig config)
    {
        var path = StatePath(config);
        if (!File.Exists(path))
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path));
            return raw is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(AppConfig config, Dictionary<string, bool> state)
    {
        var path = StatePath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ordered = state
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(path, JsonSerializer.Serialize(ordered,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
