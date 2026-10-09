namespace FeatherQuilld.Utils.Mail;

/// <summary>Outcome of a mailcow API call. <see cref="Raw"/> keeps mailcow's own log text for diagnostics.</summary>
public sealed record MailcowResult(bool Ok, string Raw = "")
{
    public static MailcowResult Failure(string message) => new(false, message);

    public static MailcowResult Success(string raw = "") => new(true, raw);
}

/// <summary>
/// The subset of the mailcow REST API (see mailcow's <c>data/web/api/openapi.yaml</c>)
/// the panel needs. Kept as an interface so the backend can be unit tested with a
/// fake instead of a live mailcow.
/// </summary>
public interface IMailcowApi
{
    Task<MailcowResult> AddDomainAsync(string domain, CancellationToken ct = default);

    Task<MailcowResult> DeleteDomainAsync(string domain, CancellationToken ct = default);

    Task<MailcowResult> AddMailboxAsync(string email, string password, bool active = true,
        long quotaMb = 0, CancellationToken ct = default);

    Task<MailcowResult> DeleteMailboxAsync(string email, CancellationToken ct = default);

    Task<MailcowResult> EditMailboxAsync(string email, IReadOnlyDictionary<string, object?> attributes,
        CancellationToken ct = default);

    Task<MailcowResult> AddAliasAsync(string address, string destination, CancellationToken ct = default);

    Task<MailcowResult> DeleteAliasAsync(string address, CancellationToken ct = default);

    Task<MailcowResult> AddDkimAsync(string domain, CancellationToken ct = default);

    Task<MailcowResult> SetSpamScoreAsync(string email, double score, CancellationToken ct = default);

    Task<string?> GetContainersStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// DKIM TXT record mailcow generated for a domain (<c>GET /api/v1/get/dkim/&lt;domain&gt;</c>).
    /// mailcow keeps the keys in redis, not as files, so this is the only reliable
    /// source for the DNS hint. Implementations without DKIM support return (null, null)
    /// so fakes keep compiling.
    /// </summary>
    Task<(string? Selector, string? Txt)> GetDkimAsync(string domain, CancellationToken ct = default) =>
        Task.FromResult<(string?, string?)>((null, null));
}
