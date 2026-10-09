using FeatherQuilld.Plugins.Events;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// mailcow: dockerized backend. Domains, mailboxes, aliases, DKIM and the
/// per-mailbox spam threshold are driven through mailcow's REST API; the
/// autoresponder uses <c>doveadm sieve</c> inside the dovecot container.
/// </summary>
public sealed class MailcowBackend : IMailBackend
{
    /// <summary>rspamd score that effectively disables spam scoring for a mailbox.</summary>
    internal const double SpamBypassScore = 9999;

    private readonly AppConfig _config;
    private readonly IEventBus _events;
    private readonly IMailcowApi _api;

    public MailcowBackend(AppConfig config, IEventBus? events = null, IMailcowApi? api = null)
    {
        _config = config;
        _events = events.OrNoOp();
        _api = api ?? new MailcowApiClient(config);
    }

    public string Kind => MailBackendKind.Mailcow;

    public string DisplayName => "mailcow: dockerized";

    public bool IsRunning() => MailcowDocker.StackReachable(_config);

    public object ProbeStatus()
    {
        var mailHost = string.IsNullOrWhiteSpace(_config.System.Mail.Mailcow.MailHost)
            ? _config.System.Mail.Hostname
            : _config.System.Mail.Mailcow.MailHost;
        var api = _api as MailcowApiClient;
        var running = IsRunning();
        // No containers here + the API answers => mailcow runs on its own host.
        var remoteHost = running && !MailcowDocker.StackRunning(_config, 1500)
            ? MailcowDocker.RemoteHost(_config)
            : null;
        var portsOpen = MailProbe.SmtpReachable(_config) && MailProbe.ImapReachable(_config);

        return new
        {
            available = running && (remoteHost is not null || portsOpen),
            backend = Kind,
            mode = remoteHost is not null ? "remote" : "local",
            project = MailcowPaths.ProjectName,
            container = remoteHost is not null ? $"{remoteHost} (remote)" : MailcowPaths.ApiContainerName,
            hostname = mailHost,
            api_url = api?.BaseUrl.ToString() ?? MailcowApiClient.ResolveBaseUrl(_config).ToString(),
            api_key_configured = api?.HasApiKey ?? MailcowApiClient.ResolveApiKey(_config).Length > 0,
            smtp_port = _config.System.Mail.SmtpPort,
            imap_port = _config.System.Mail.ImapPort,
            port_25_open = MailProbe.PortOpen(25),
            submission_open = MailProbe.SmtpReachable(_config),
            imap_open = MailProbe.ImapReachable(_config),
            mailcow_ui_port = _config.System.Mail.Mailcow.HttpsPort,
            dkim_directory = Path.Combine(MailcowPaths.DataDir(_config), "dkim"),
            deliverability_hint = MailProbe.PortOpen(25)
                ? null
                : "SMTP port 25 is not listening inbound MX and many providers require it; also set PTR/rDNS for outbound.",
        };
    }

    public IReadOnlyList<string> ListDomains() =>
        MailDomainStore.List(MailDomainStore.MailcowPath(_config));

    public void AddDomain(string domain)
    {
        domain = Normalize(domain);
        try
        {
            Run(_api.AddDomainAsync(domain), $"add domain {domain}");
        }
        catch (InvalidOperationException e) when (IsAlreadyExists(e.Message))
        {
            // mailcow answers a duplicate /add/domain with type "danger"; creating
            // the second mailbox on an existing domain must not fail.
        }

        MailDomainStore.Persist(MailDomainStore.MailcowPath(_config), domain, add: true);
        EnsureDkim(domain);
    }

    private static bool IsAlreadyExists(string message) =>
        message.Contains("exist", StringComparison.OrdinalIgnoreCase)
        || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    public void RemoveDomain(string domain)
    {
        domain = Normalize(domain);
        Run(_api.DeleteDomainAsync(domain), $"delete domain {domain}");
        MailDomainStore.Persist(MailDomainStore.MailcowPath(_config), domain, add: false);
    }

    public void CreateMailbox(string email, string password)
    {
        email = Normalize(email);
        DomainCheckGuard(email);
        Run(_api.AddMailboxAsync(email, password), $"add mailbox {email}");
    }

    public void DeleteMailbox(string email)
    {
        email = Normalize(email);
        Run(_api.DeleteMailboxAsync(email), $"delete mailbox {email}");
    }

    public void UpdateMailboxPassword(string email, string password)
    {
        email = Normalize(email);
        Run(_api.EditMailboxAsync(email, new Dictionary<string, object?>
        {
            ["password"] = password,
            ["password2"] = password,
        }), $"update mailbox password {email}");
    }

    public void SetMailboxEnabled(string email, bool enabled)
    {
        email = Normalize(email);
        Run(_api.EditMailboxAsync(email, new Dictionary<string, object?>
        {
            ["active"] = enabled ? "1" : "0",
        }), $"set mailbox {(enabled ? "active" : "inactive")} {email}");
    }

    public void AddAlias(string source, string destination)
    {
        source = Normalize(source);
        Run(_api.AddAliasAsync(source, Normalize(destination)), $"add alias {source}");
    }

    public void DeleteAlias(string source, string? destination)
    {
        source = Normalize(source);
        // mailcow deletes aliases by address, the destination is only relevant
        // for docker-mailserver's setup alias del.
        Run(_api.DeleteAliasAsync(source), $"delete alias {source}");
    }

    public bool GetSpamFilterEnabled(string email) =>
        MailcowSpamState.GetEnabled(_config, Normalize(email));

    public void SetSpamFilterEnabled(string email, bool enabled)
    {
        email = Normalize(email);
        // mailcow has no "bypass" switch: the per-mailbox rspamd score is the
        // effective knob, so a very high score means "do not filter".
        Run(_api.SetSpamScoreAsync(email, enabled ? 0 : SpamBypassScore),
            $"set spam filter {(enabled ? "on" : "off")} for {email}");
        MailcowSpamState.SetEnabled(_config, email, enabled);
    }

    public void SetAutorespond(string email, bool enabled, string subject, string body)
    {
        email = Normalize(email);
        if (enabled)
            MailcowSieve.Set(_config, email, subject, body);
        else
            MailcowSieve.Remove(_config, email);
    }

    public bool EnsureDkim(string domain, int maxAttempts = 6, int delayMs = 1000)
    {
        domain = Normalize(domain);
        var selector = string.IsNullOrWhiteSpace(_config.System.Mail.DkimSelector)
            ? "mail"
            : _config.System.Mail.DkimSelector.Trim();

        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            try
            {
                Run(_api.AddDkimAsync(domain), $"add dkim for {domain}");
            }
            catch
            {
                // mailcow refuses duplicate keys; the key file check below decides.
            }

            if (MailDnsHelper.IsDkimReady(_config, domain))
                return true;

            if (attempt < maxAttempts - 1 && delayMs > 0)
                Thread.Sleep(delayMs);
        }

        return MailDnsHelper.IsDkimReady(_config, domain);
    }

    /// <summary>mailcow requires the domain to exist before a mailbox can be created.</summary>
    private void DomainCheckGuard(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at >= email.Length - 1)
            throw new InvalidOperationException($"Invalid email address '{email}'.");

        var domain = email[(at + 1)..];
        if (!ListDomains().Contains(domain, StringComparer.OrdinalIgnoreCase))
            AddDomain(domain);
    }

    private static void Run(Task<MailcowResult> call, string what)
    {
        MailcowResult result;
        try
        {
            result = call.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"mailcow API call failed ({what}): {e.Message}", e);
        }

        if (!result.Ok)
            throw new InvalidOperationException($"mailcow API call failed ({what}): {result.Raw}");
    }

    private static string Normalize(string value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}
