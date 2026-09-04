using System.Net;
using System.Net.Sockets;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

public static class MailDeliverabilityHelper
{
    public sealed record PtrCheck(string Status, string? ExpectedHost, string? PublicIp, string? PtrHost, string? Detail);

    public static PtrCheck CheckPtr(string expectedHost, string? publicIp)
    {
        expectedHost = NormalizeHost(expectedHost);
        publicIp = (publicIp ?? "").Trim();
        if (expectedHost.Length == 0)
        {
            return new PtrCheck("warn", null, publicIp, null, "Mail hostname is not configured.");
        }

        if (publicIp.Length == 0 || !IPAddress.TryParse(publicIp, out _))
        {
            return new PtrCheck("warn", expectedHost, publicIp, null, "Public IP is unknown PTR cannot be verified.");
        }

        try
        {
            var entry = global::System.Net.Dns.GetHostEntry(publicIp);
            var ptr = NormalizeHost(entry.HostName);
            if (ptr.Length == 0)
            {
                return new PtrCheck("warn", expectedHost, publicIp, null, "No PTR record found for the node public IP.");
            }

            var matches = HostnamesMatch(ptr, expectedHost);
            return matches
                ? new PtrCheck("pass", expectedHost, publicIp, ptr, null)
                : new PtrCheck(
                    "warn",
                    expectedHost,
                    publicIp,
                    ptr,
                    $"PTR ({ptr}) does not match mail hostname ({expectedHost}).");
        }
        catch (SocketException ex)
        {
            return new PtrCheck("warn", expectedHost, publicIp, null, $"PTR lookup failed: {ex.Message}");
        }
    }

    public static object BuildPayload(AppConfig config, string domain, string? publicIp = null)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var mxHost = MailDnsHelper.ResolveMailHostname(config, domain).TrimEnd('.');
        var ptr = CheckPtr(mxHost, publicIp);
        return new
        {
            domain,
            mx_host = mxHost,
            public_ip = publicIp,
            ptr = new
            {
                status = ptr.Status,
                expected_host = ptr.ExpectedHost,
                public_ip = ptr.PublicIp,
                ptr_host = ptr.PtrHost,
                detail = ptr.Detail,
            },
            ports = new
            {
                smtp_25 = MailProbe.PortOpen(25),
                submission = MailProbe.SmtpReachable(config),
                imap = MailProbe.ImapReachable(config),
            },
            mail_container = MailProbe.ContainerRunning(config),
        };
    }

    private static bool HostnamesMatch(string left, string right)
    {
        left = NormalizeHost(left);
        right = NormalizeHost(right);
        if (left == right)
            return true;

        return left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase)
            || right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimEnd('.').ToLowerInvariant();
}
