using System.Text.Json;
using FeatherQuilld.Plugins.Events;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Front door for the mail API. Parsing, event hooks and panel side bookkeeping
/// (domain list, autoresponder state, mailing lists) live here; everything that
/// depends on the installed mail stack is delegated to the configured
/// <see cref="IMailBackend"/> (docker-mailserver by default, mailcow optional).
/// </summary>
public sealed class MailManager
{
    private readonly AppConfig _config;
    private readonly IEventBus _events;
    private readonly IMailBackend _backend;

    public MailManager(AppConfig config, IEventBus? events = null)
        : this(config, events, backend: null)
    {
    }

    /// <summary>
    /// Test/DI seam: <paramref name="backend"/> overrides the backend that
    /// <c>system.mail.backend</c> would select.
    /// </summary>
    public MailManager(AppConfig config, IEventBus? events, IMailBackend? backend)
    {
        _config = config;
        _events = events.OrNoOp();
        _backend = backend ?? MailBackendFactory.Create(config, _events);
        if (!_backend.IsRunning())
            throw new InvalidOperationException($"{_backend.DisplayName} mail server is not running.");
    }

    /// <summary>Backend actually in use (also surfaced through the probe payload).</summary>
    public string BackendKind => _backend.Kind;

    public object ProbeStatus() => _backend.ProbeStatus();

    public IReadOnlyList<string> ListDomains() => _backend.ListDomains();

    public void AddDomain(string domain)
    {
        domain = NormalizeDomain(domain);
        _events.WithHooks(
            new MailDomainAddBeforeEvent { Domain = domain },
            err => new MailDomainAddAfterEvent { Domain = domain, Error = err },
            () =>
            {
                // Both backends keep their own view of domains: docker-mailserver
                // v13+ (ACCOUNT_PROVISIONER=FILE) has no "setup domain add" and
                // creates domains implicitly from mailbox addresses, mailcow needs
                // an explicit /add/domain before the first mailbox. Our own
                // tracking file serves GET /api/mail/domains and DKIM generation
                // in both cases.
                _backend.AddDomain(domain);
            });
    }

    public void RemoveDomain(string domain)
    {
        domain = NormalizeDomain(domain);
        _events.WithHooks(
            new MailDomainRemoveBeforeEvent { Domain = domain },
            err => new MailDomainRemoveAfterEvent { Domain = domain, Error = err },
            () => _backend.RemoveDomain(domain));
    }

    public object Provision(IReadOnlyDictionary<string, object?> payload)
    {
        var email = ResolveProvisionEmail(payload);
        return _events.WithHooks(
            new MailProvisionBeforeEvent { Email = email },
            (_, err) => new MailProvisionAfterEvent { Email = email, Error = err },
            () => ProvisionCore(payload));
    }

    private object ProvisionCore(IReadOnlyDictionary<string, object?> payload)
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

        _backend.CreateMailbox(email, password);

        if (GetBool(payload, "enabled") == false)
            _backend.SetMailboxEnabled(email, enabled: false);

        return new { ok = true, email };
    }

    private object DeleteMailbox(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        _backend.DeleteMailbox(email);
        return new { ok = true, email };
    }

    private object ResetPassword(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var password = GetString(payload, "password") ?? throw new InvalidOperationException("password is required.");
        _backend.UpdateMailboxPassword(email, password);
        return new { ok = true, email };
    }

    private object SetEnabled(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var enabled = GetBool(payload, "enabled") ?? true;
        _backend.SetMailboxEnabled(email, enabled);
        return new { ok = true, email, enabled };
    }

    private object SetForward(IReadOnlyDictionary<string, object?> payload, bool delete)
    {
        var source = GetString(payload, "source") ?? RequireEmail(payload);
        var destination = GetString(payload, "destination") ?? "";
        if (!delete && destination.Length == 0)
            throw new InvalidOperationException("destination is required.");

        if (delete)
            _backend.DeleteAlias(source, destination);
        else
            _backend.AddAlias(source, destination);

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
            _backend.SetAutorespond(email, enabled: false, subject, body);
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
        _backend.SetAutorespond(email, enabled: true, subject, body);

        return new { ok = true, email, enabled = true, sieve = true };
    }

    public bool GetSpamFilterEnabled(string email) =>
        _backend.GetSpamFilterEnabled(email);

    public object SetSpamFilter(IReadOnlyDictionary<string, object?> payload)
    {
        var email = RequireEmail(payload);
        var enabled = GetBool(payload, "enabled") ?? true;
        return _events.WithHooks(
            new MailSpamFilterBeforeEvent { Email = email, Enabled = enabled },
            (_, err) => new MailSpamFilterAfterEvent { Email = email, Error = err },
            () =>
            {
                _backend.SetSpamFilterEnabled(email, enabled);
                return new { ok = true, email, enabled };
            });
    }

    public IReadOnlyList<object> ListMailingLists(string? domain = null) =>
        MailListHelper.ListLists(_config, domain);

    private object CreateList(IReadOnlyDictionary<string, object?> payload)
    {
        var address = GetString(payload, "address") ?? RequireEmail(payload);
        var members = GetStringList(payload, "members");
        if (members.Count == 0)
            throw new InvalidOperationException("members is required.");

        // Mailing lists are emulated with per-member aliases because neither
        // docker-mailserver's setup CLI nor mailcow's API exposes list objects.
        return MailListHelper.CreateList(_config, address, members, _backend.AddAlias);
    }

    private object DeleteList(IReadOnlyDictionary<string, object?> payload)
    {
        var address = GetString(payload, "address") ?? RequireEmail(payload);
        return MailListHelper.DeleteList(_config, address, (source, dest) => _backend.DeleteAlias(source, dest));
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
            _backend.AddAlias,
            (source, dest) => _backend.DeleteAlias(source, dest));
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
    public bool EnsureDkim(string domain, int maxAttempts = 6, int delayMs = 1000) =>
        _backend.EnsureDkim(NormalizeDomain(domain), maxAttempts, delayMs);

    private static string ResolveProvisionEmail(IReadOnlyDictionary<string, object?> payload) =>
        (GetString(payload, "email")
         ?? GetString(payload, "source")
         ?? GetString(payload, "address")
         ?? "").Trim().ToLowerInvariant();

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
