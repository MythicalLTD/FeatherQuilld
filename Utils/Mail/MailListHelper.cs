using System.Text.Json;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>Mailing list aliases synced to docker-mailserver postfix-virtual via setup alias.</summary>
public static class MailListHelper
{
    public static IReadOnlyList<object> ListLists(AppConfig config, string? domain = null)
    {
        var store = LoadStore(config);
        var lists = new List<object>();
        foreach (var kv in store.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var at = kv.Key.LastIndexOf('@');
            if (at <= 0)
                continue;
            var listDomain = kv.Key[(at + 1)..];
            if (!string.IsNullOrWhiteSpace(domain)
                && !string.Equals(listDomain, domain.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                continue;

            lists.Add(new
            {
                address = kv.Key,
                domain = listDomain,
                local_part = kv.Key[..at],
                members = kv.Value.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
            });
        }

        return lists;
    }

    public static object CreateList(AppConfig config, string address, IEnumerable<string> members, Action<string, string> runSetupAliasAdd)
    {
        address = NormalizeAddress(address);
        var memberList = NormalizeMembers(members);
        if (memberList.Count == 0)
            throw new InvalidOperationException("At least one member is required.");

        var store = LoadStore(config);
        store[address] = memberList;
        SaveStore(config, store);
        SyncAliases(address, memberList, runSetupAliasAdd, runSetupAliasDel: null);

        return new { ok = true, address, members = memberList };
    }

    public static object DeleteList(AppConfig config, string address, Action<string, string> runSetupAliasDel)
    {
        address = NormalizeAddress(address);
        var store = LoadStore(config);
        if (!store.TryGetValue(address, out var members))
            return new { ok = true, address, deleted = false };

        store.Remove(address);
        SaveStore(config, store);
        SyncAliases(address, members, runSetupAliasAdd: null, runSetupAliasDel);

        return new { ok = true, address, deleted = true };
    }

    public static object SetListMember(
        AppConfig config,
        string address,
        string member,
        bool add,
        Action<string, string> runSetupAliasAdd,
        Action<string, string> runSetupAliasDel)
    {
        address = NormalizeAddress(address);
        member = NormalizeMember(member);
        var store = LoadStore(config);
        if (!store.TryGetValue(address, out var members))
            throw new InvalidOperationException("Mailing list not found.");

        if (add)
        {
            if (members.Contains(member, StringComparer.OrdinalIgnoreCase))
                return new { ok = true, address, member, added = false };

            members.Add(member);
            runSetupAliasAdd(address, member);
        }
        else
        {
            var removed = members.RemoveAll(m => string.Equals(m, member, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
                return new { ok = true, address, member, removed = false };

            runSetupAliasDel(address, member);
        }

        store[address] = members.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        SaveStore(config, store);

        return new { ok = true, address, member, added = add, removed = !add };
    }

    internal static void SyncAliases(
        string address,
        IReadOnlyList<string> members,
        Action<string, string>? runSetupAliasAdd,
        Action<string, string>? runSetupAliasDel)
    {
        if (runSetupAliasDel is not null)
        {
            foreach (var member in members)
                runSetupAliasDel(address, member);
        }

        if (runSetupAliasAdd is not null)
        {
            foreach (var member in members)
                runSetupAliasAdd(address, member);
        }
    }

    private static Dictionary<string, List<string>> LoadStore(AppConfig config)
    {
        var path = StorePath(config);
        if (!File.Exists(path))
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                ?? new Dictionary<string, List<string>>();
            return raw.ToDictionary(
                kv => NormalizeAddress(kv.Key),
                kv => kv.Value.Select(NormalizeMember).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveStore(AppConfig config, Dictionary<string, List<string>> store)
    {
        var path = StorePath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ordered = store
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(path, JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string StorePath(AppConfig config) =>
        Path.Combine(MailPaths.ConfigDir(config), "feather-mailing-lists.json");

    private static string NormalizeAddress(string address)
    {
        address = (address ?? "").Trim().ToLowerInvariant();
        if (!address.Contains('@'))
            throw new InvalidOperationException("Invalid mailing list address.");
        return address;
    }

    private static string NormalizeMember(string member) =>
        (member ?? "").Trim().ToLowerInvariant();

    private static List<string> NormalizeMembers(IEnumerable<string> members)
    {
        return members
            .Select(NormalizeMember)
            .Where(m => m.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
