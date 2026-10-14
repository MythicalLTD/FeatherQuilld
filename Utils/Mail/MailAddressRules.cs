using System.Text.RegularExpressions;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Whitelist validation for mailbox addresses that end up as file-name or
/// path components (Sieve scripts, spam-filter state). Only plain
/// <c>local-part@host</c> addresses pass, so a crafted address cannot contain
/// path separators or <c>..</c> segments and escape the mail config directory.
/// </summary>
internal static partial class MailAddressRules
{
    [GeneratedRegex(
        @"^(?!\.)(?!.*\.\.)(?=.{1,254}$)[a-z0-9.!#$%&'*+=?^_`{|}~-]+@(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();

    public static bool IsValidAddress(string? email) =>
        !string.IsNullOrWhiteSpace(email) && AddressRegex().IsMatch(email.Trim());
}
