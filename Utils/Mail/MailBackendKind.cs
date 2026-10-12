namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Supported mail stack backends. Default stays <see cref="DockerMailserver"/>
/// so existing installations keep the exact behaviour they had before the
/// backend switch existed.
/// </summary>
public static class MailBackendKind
{
    public const string DockerMailserver = "docker-mailserver";
    public const string Mailcow = "mailcow";

    /// <summary>
    /// Lenient resolver used by probes/diagnostics: unknown or empty values fall
    /// back to docker-mailserver. <see cref="MailBackendFactory"/> rejects
    /// unknown values so a typo never silently manages the wrong stack.
    /// </summary>
    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return v switch
        {
            "mailcow" or "mailcow-dockerized" or "mailcowdockerized" => Mailcow,
            _ => DockerMailserver,
        };
    }

    public static bool IsMailcow(string? value) =>
        Normalize(value) == Mailcow;

    public static bool IsKnown(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (v.Length == 0)
            return true;
        return v is "docker-mailserver" or "docker-mail-server" or "dms" or "docker"
            or "mailcow" or "mailcow-dockerized" or "mailcowdockerized" or "default";
    }
}
