using System.Diagnostics;
using FeatherQuilld.Plugins.Events;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// docker-mailserver backend (the historic behaviour of MailManager). Mailbox,
/// alias, domain-track and spam operations are executed through the image's
/// <c>setup</c> CLI inside the container; DKIM/autoresponder state lives in the
/// files the image itself uses.
/// </summary>
public sealed class DockerMailserverBackend : IMailBackend
{
    private readonly AppConfig _config;
    private readonly IEventBus _events;

    public DockerMailserverBackend(AppConfig config, IEventBus? events = null)
    {
        _config = config;
        _events = events.OrNoOp();
    }

    public string Kind => MailBackendKind.DockerMailserver;

    public string DisplayName => "docker-mailserver";

    public bool IsRunning() => MailProbe.ContainerRunning(_config);

    public string NotRunningHint() =>
        "Install the mailserver package on this node (POST /api/system/packages/mailserver/install); " +
        "the container is started by that package. Docs: docs/mail-backends.md";

    public object ProbeStatus() => new
    {
        available = MailProbe.IsAvailable(_config),
        backend = Kind,
        container = MailPaths.ContainerName,
        hostname = _config.System.Mail.Hostname,
        smtp_port = _config.System.Mail.SmtpPort,
        imap_port = _config.System.Mail.ImapPort,
        port_25_open = MailProbe.PortOpen(25),
        submission_open = MailProbe.SmtpReachable(_config),
        imap_open = MailProbe.ImapReachable(_config),
        deliverability_hint = MailProbe.PortOpen(25)
            ? null
            : "SMTP port 25 is not listening inbound MX and many providers require it; also set PTR/rDNS for outbound.",
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
        // docker-mailserver has no top-level "domain" management command as
        // of v13+ (ACCOUNT_PROVISIONER=FILE, the default here): domains
        // exist implicitly from mailbox addresses in postfix-accounts.cf,
        // there is nothing to "add" separately. Calling
        // "setup domain add <domain>" against a current image fails with
        // "invalid command" and previously aborted mailbox creation before
        // "setup email add" ever ran. Domain tracking for our own
        // /api/mail/domains listing and DKIM key generation is handled
        // locally/via "config dkim domain" (EnsureDkim) below.
        MailDomainStore.Persist(MailDomainStore.DockerMailserverPath(_config), domain, add: true);
        EnsureDkim(domain);
    }

    public void RemoveDomain(string domain)
    {
        domain = NormalizeDomain(domain);
        // See AddDomain: no "setup domain del" command exists on current
        // docker-mailserver images. Removing the last mailbox on a domain
        // is what actually removes it from the container's own view; this
        // only drops it from our local tracking file.
        MailDomainStore.Persist(MailDomainStore.DockerMailserverPath(_config), domain, add: false);
    }

    public void CreateMailbox(string email, string password)
    {
        RunSetup("email", "add", email, password);
    }

    public void DeleteMailbox(string email)
    {
        RunSetup("email", "del", email);
    }

    public void UpdateMailboxPassword(string email, string password)
    {
        RunSetup("email", "update", email, password);
    }

    public void SetMailboxEnabled(string email, bool enabled)
    {
        if (enabled)
            RunSetup("email", "restrict", "del", email);
        else
            RunSetup("email", "restrict", "add", email, "send");
    }

    public void AddAlias(string source, string destination)
    {
        RunSetup("alias", "add", source, destination);
    }

    public void DeleteAlias(string source, string? destination)
    {
        RunSetup("alias", "del", source);
    }

    public bool GetSpamFilterEnabled(string email) =>
        MailSpamHelper.GetSpamFilterEnabled(_config, email);

    public void SetSpamFilterEnabled(string email, bool enabled) =>
        MailSpamHelper.SetSpamFilterEnabled(_config, email, enabled);

    public void SetAutorespond(string email, bool enabled, string subject, string body)
    {
        if (enabled)
            MailVacationHelper.WriteAutorespond(_config, email, subject, body);
        else
            MailVacationHelper.RemoveAutorespond(_config, email);
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
                // Container may still be starting retry until file appears.
            }

            if (MailDnsHelper.IsDkimReady(_config, domain))
                return true;

            if (attempt < maxAttempts - 1 && delayMs > 0)
                Thread.Sleep(delayMs);
        }

        return MailDnsHelper.IsDkimReady(_config, domain);
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

    internal static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}
