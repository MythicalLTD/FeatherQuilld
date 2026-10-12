using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Local domain tracking shared by all backends. Neither docker-mailserver
/// (v13+, ACCOUNT_PROVISIONER=FILE) nor mailcow can list "the domains the panel
/// manages" reliably through their own tooling, so the panel remembers them
/// here and uses the file for <c>GET /api/mail/domains</c> and DNS hints.
/// </summary>
internal static class MailDomainStore
{
    public static string DockerMailserverPath(AppConfig config) => MailPaths.DomainsFile(config);

    public static string MailcowPath(AppConfig config) =>
        Path.Combine(MailcowPaths.Root(config), "feather-domains.txt");

    public static IReadOnlyList<string> List(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<string>();

        return File.ReadAllLines(path)
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct()
            .OrderBy(l => l)
            .ToList();
    }

    public static void Persist(string path, string domain, bool add)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var domains = File.Exists(path)
            ? File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (add)
            domains.Add(domain);
        else
            domains.Remove(domain);

        File.WriteAllLines(path, domains.OrderBy(d => d));
    }
}
