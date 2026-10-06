using System.Net;
using System.Text.RegularExpressions;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>Validates redirect targets and backend hosts before proxy config render.</summary>
public static partial class ProxyRouteSanitizer
{
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostnameRegex();

    /// <summary>
    /// Accepts an absolute http(s) URL, or a path starting with <c>/</c>.
    /// Rejects newlines, control chars, and other schemes.
    /// </summary>
    public static string? NormalizeRedirectTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (value.Any(c => char.IsControl(c) || c is '\n' or '\r' or '\0' or ';' or '{' or '}'))
            throw new ArgumentException("Redirect target contains forbidden characters.");

        if (value.StartsWith('/'))
        {
            if (value.Contains("://", StringComparison.Ordinal) || value.Contains('\\'))
                throw new ArgumentException("Redirect path is invalid.");
            return value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Redirect target must be an http(s) URL or a path starting with '/'.");

        // Keep the validated operator/panel string (no forced trailing slash rewrite).
        return value;
    }

    /// <summary>Hostname or IP only — no ports, paths, or proxy metacharacters.</summary>
    public static string NormalizeBackendHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "";

        var value = host.Trim();
        if (value.Any(c => char.IsControl(c) || c is '\n' or '\r' or '\0' or ';' or '{' or '}' or ' ' or '/' or '\\' or ':' or '@'))
            throw new ArgumentException("Backend host contains forbidden characters.");

        if (IPAddress.TryParse(value, out _))
            return value;

        if (!HostnameRegex().IsMatch(value))
            throw new ArgumentException("Backend host must be a hostname or IP address.");

        return value.ToLowerInvariant();
    }
}
