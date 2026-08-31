using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

public sealed class MailManager
{
    private readonly AppConfig _config;

    public MailManager(AppConfig config)
    {
        _config = config;
        if (!MailProbe.ContainerRunning(config))
            throw new InvalidOperationException("Mail server container is not running.");
    }

    public object ProbeStatus() => new
    {
        available = MailProbe.IsAvailable(_config),
        container = MailPaths.ContainerName,
        hostname = _config.System.Mail.Hostname,
        smtp_port = _config.System.Mail.SmtpPort,
        imap_port = _config.System.Mail.ImapPort,
        port_25_open = MailProbe.PortOpen(25),
        submission_open = MailProbe.SmtpReachable(_config),
        imap_open = MailProbe.ImapReachable(_config),
        deliverability_hint = MailProbe.PortOpen(25)
            ? null
            : "SMTP port 25 is not listening — inbound MX and many providers require it; also set PTR/rDNS for outbound.",
    };

    public IReadOnlyList<string> ListDomains()
    {
        var path = MailPaths.DomainsFile(_config);
        if (!File.Exists(path))
            return Array.Empty<string>();

        return File.ReadAllLines(path)
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct()
            .OrderBy(l => l)
            .ToList();
    }

    public void AddDomain(string domain)
    {
        domain = NormalizeDomain(domain);
        RunSetup("domain", "add", domain);
        PersistDomain(domain, add: true);
        EnsureDkim(domain);
    }

    public void RemoveDomain(string domain)
    {
        domain = NormalizeDomain(domain);
        RunSetup("domain", "del", domain);
        PersistDomain(domain, add: false);
    }

    public object Provision(IReadOnlyDictionary<string, object?> payload)
    {
        var action = GetString(payload, "action")?.ToLowerInvariant() ?? "";
        return action switch
        {
            "create" => CreateMailbox(payload),
            "delete" => DeleteMailbox(payload),
            "reset_password" => ResetPassword(payload),
            "set_enabled" => SetEnabled(payload),
            "set_forward" => SetForward(payload, delete: false),
            "delete_forward" => SetForward(payload, delete: true),
            "set_autorespond" => SetAutorespond(payload),
            "set_spam_filter" => SetSpamFilter(payload),
            "create_list" => CreateList(payload),
            "delete_list" => DeleteList(payload),
            "set_list_member" => SetListMember(payload),
            _ => throw new InvalidOperationException("Unsupported mail provision action: " + action),
        };
    }

    private object CreateMailbox(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var password = GetString(payload, "password") ?? throw new InvalidOperationException("password is required.");
        var domain = EmailDomain(email);
        AddDomain(domain);

        var args = new List<string> { "email", "add", email, password };
        RunSetup(args.ToArray());

        if (GetBool(payload, "enabled") == false)
            SetEnabledInternal(email, enabled: false);

        return new { ok = true, email };
    }

    private object DeleteMailbox(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        RunSetup("email", "del", email);
        return new { ok = true, email };
    }

    private object ResetPassword(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var password = GetString(payload, "password") ?? throw new InvalidOperationException("password is required.");
        RunSetup("email", "update", email, password);
        return new { ok = true, email };
    }

    private object SetEnabled(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var enabled = GetBool(payload, "enabled") ?? true;
        SetEnabledInternal(email, enabled);
        return new { ok = true, email, enabled };
    }

    private void SetEnabledInternal(string email, bool enabled)
    {
        if (enabled)
            RunSetup("email", "restrict", "del", email);
        else
            RunSetup("email", "restrict", "add", email, "send");
    }

    private object SetForward(IReadOnlyDictionary<string, object?> payload, bool delete)
    {
        var source = GetString(payload, "source") ?? RequireEmail(payload);
        var destination = GetString(payload, "destination") ?? "";
        if (!delete && destination.Length == 0)
            throw new InvalidOperationException("destination is required.");

        if (delete)
            RunSetup("alias", "del", source);
        else
            RunSetup("alias", "add", source, destination);

        return new { ok = true, source, destination };
    }

    private object SetAutorespond(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var enabled = GetBool(payload, "enabled") ?? false;
        var subject = GetString(payload, "subject") ?? "Out of office";
        var body = GetString(payload, "body") ?? "";

        Directory.CreateDirectory(MailPaths.AutorespondDir(_config));
        var path = Path.Combine(MailPaths.AutorespondDir(_config), SanitizeFileName(email) + ".json");
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            MailVacationHelper.RemoveAutorespond(_config, email);
            return new { ok = true, email, enabled = false };
        }

        var doc = new
        {
            email,
            enabled = true,
            subject,
            body,
            updated_at = DateTimeOffset.UtcNow.ToString("O"),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        MailVacationHelper.WriteAutorespond(_config, email, subject, body);

        return new { ok = true, email, enabled = true, sieve = true };
    }

    public bool GetSpamFilterEnabled(string email) =>
        MailSpamHelper.GetSpamFilterEnabled(_config, email);

    public object SetSpamFilter(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var enabled = GetBool(payload, "enabled") ?? true;
        MailSpamHelper.SetSpamFilterEnabled(_config, email, enabled);
        return new { ok = true, email, enabled };
    }

    public IReadOnlyList<object> ListMailingLists(string? domain = null) =>
        MailListHelper.ListLists(_config, domain);

    private object CreateList(IReadOnlyDictionary<string, object?> payload)
    {
        var address = GetString(payload, "address") ?? RequireEmail(payload);
        var members = GetStringList(payload, "members");
        if (members.Count == 0)
            throw new InvalidOperationException("members is required.");

        return MailListHelper.CreateList(_config, address, members, (source, dest) => RunSetup("alias", "add", source, dest));
    }

    private object DeleteList(IReadOnlyDictionary<string, object?> payload)
    {
        var address = GetString(payload, "address") ?? RequireEmail(payload);
        return MailListHelper.DeleteList(_config, address, (source, dest) => RunSetup("alias", "del", source, dest));
    }

    private object SetListMember(IReadOnlyDictionary<string, object?> payload)
    {
        var address = GetString(payload, "address") ?? throw new InvalidOperationException("address is required.");
        var member = GetString(payload, "member") ?? throw new InvalidOperationException("member is required.");
        var add = GetBool(payload, "add") ?? true;
        return MailListHelper.SetListMember(
            _config,
            address,
            member,
            add,
            (source, dest) => RunSetup("alias", "add", source, dest),
            (source, dest) => RunSetup("alias", "del", source, dest));
    }

    private static List<string> GetStringList(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return [];

        if (value is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToList();
        }

        if (value is IEnumerable<object> list)
        {
            return list
                .Select(item => item?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToList();
        }

        return [];
    }

    /// <summary>Generate DKIM keys with short retries until the TXT file is readable.</summary>
    public bool EnsureDkim(string domain, int maxAttempts = 6, int delayMs = 1000)
    {
        domain = NormalizeDomain(domain);
        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            try
            {
                RunSetup("config", "dkim", "domain", domain);
            }
            catch
            {
                // Container may still be starting — retry until file appears.
            }

            if (MailDnsHelper.IsDkimReady(_config, domain))
                return true;

            if (attempt < maxAttempts - 1 && delayMs > 0)
                Thread.Sleep(delayMs);
        }

        return MailDnsHelper.IsDkimReady(_config, domain);
    }

    private void PersistDomain(string domain, bool add)
    {
        var path = MailPaths.DomainsFile(_config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var domains = File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (add)
            domains.Add(domain);
        else
            domains.Remove(domain);

        File.WriteAllLines(path, domains.OrderBy(d => d));
    }

    private void RunSetup(params string[] setupArgs)
    {
        var args = new List<string> { "exec", MailPaths.ContainerName, "setup" };
        args.AddRange(setupArgs);
        RunDocker(args);
    }

    internal void RunDocker(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker.");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException("docker command timed out.");
        }

        if (proc.ExitCode != 0)
        {
            var combined = (stdout + "\n" + stderr).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(combined)
                ? $"docker exited with code {proc.ExitCode}"
                : combined);
        }
    }

    private static string RequireEmail(IReadOnlyDictionary<string, object?> payload)
    {
        var email = GetString(payload, "email");
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("email is required.");
        return email.Trim().ToLowerInvariant();
    }

    private static string EmailDomain(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at >= email.Length - 1)
            throw new InvalidOperationException("Invalid email address.");
        return email[(at + 1)..];
    }

    private static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();

    private static string? GetString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => value.ToString(),
        };
    }

    private static bool? GetBool(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            JsonElement el when el.ValueKind == JsonValueKind.True => true,
            JsonElement el when el.ValueKind == JsonValueKind.False => false,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
