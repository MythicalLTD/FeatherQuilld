using AppConfig = FeatherQuilld.Utils.Config.Config;
using FeatherQuilld.Plugins.Events;

namespace FeatherQuilld.Utils.Mail;

/// <summary>Creates the backend selected by <c>system.mail.backend</c>.</summary>
public static class MailBackendFactory
{
    public static IMailBackend Create(AppConfig config, IEventBus? events = null)
    {
        var configured = config.System.Mail.Backend;

        // A typo must not silently manage the other stack, so unknown values are
        // rejected here; the lenient MailBackendKind.Normalize is only used by probes.
        if (!MailBackendKind.IsKnown(configured))
        {
            throw new InvalidOperationException(
                $"Unsupported mail backend '{configured}'. Supported: " +
                $"'{MailBackendKind.DockerMailserver}' (default) and '{MailBackendKind.Mailcow}'.");
        }

        return MailBackendKind.IsMailcow(configured)
            ? new MailcowBackend(config, events)
            : new DockerMailserverBackend(config, events);
    }
}
