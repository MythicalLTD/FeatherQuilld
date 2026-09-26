namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Primitives every mail stack has to provide. <see cref="MailManager"/> keeps the
/// payload parsing, event hooks and bookkeeping (domains file, autoresponder
/// state, mailing lists) and delegates the stack specific work to a backend, so
/// the public <c>/api/mail</c> surface stays identical regardless of the stack.
/// </summary>
public interface IMailBackend
{
    /// <summary>Canonical backend name, see <see cref="MailBackendKind"/>.</summary>
    string Kind { get; }

    /// <summary>Human readable name for diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>
    /// True when the stack is installed and its containers are running. Used by
    /// <see cref="MailManager"/> as a guard before any mutating operation.
    /// </summary>
    bool IsRunning();

    /// <summary>Payload for <c>GET /api/mail/probe</c>.</summary>
    object ProbeStatus();

    IReadOnlyList<string> ListDomains();

    void AddDomain(string domain);

    void RemoveDomain(string domain);

    /// <summary>Generates/refreshes DKIM keys with short retries; true when the TXT file is readable.</summary>
    bool EnsureDkim(string domain, int maxAttempts = 6, int delayMs = 1000);

    void CreateMailbox(string email, string password);

    void DeleteMailbox(string email);

    void UpdateMailboxPassword(string email, string password);

    void SetMailboxEnabled(string email, bool enabled);

    void AddAlias(string source, string destination);

    void DeleteAlias(string source, string? destination);

    bool GetSpamFilterEnabled(string email);

    void SetSpamFilterEnabled(string email, bool enabled);

    void SetAutorespond(string email, bool enabled, string subject, string body);
}
