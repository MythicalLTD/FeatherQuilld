using System.Text.Json.Serialization;
using FeatherQuilld.Utils.Ftp;
using FeatherQuilld.Utils.Mail;
using FeatherQuilld.Utils.Proxy;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.SystemInfo;

public static class HostingServicesDiagnostics
{
    public static HostingServicesSnapshot Capture(AppConfig config)
    {
        var ftp = config.Ftp;
        return new HostingServicesSnapshot(
            FtpEnabled: ftp.Enabled,
            FtpListening: ftp.Enabled && FtpProbe.IsListening(ftp),
            FtpPort: ftp.Port,
            FtpPasvMin: ftp.PassivePortMin,
            FtpPasvMax: ftp.PassivePortMax,
            FtpPublicIp: ftp.PassiveHost ?? "",
            ProxyProvider: (config.System.Proxy.Provider ?? "caddy").Trim().ToLowerInvariant(),
            ProxyEnabled: config.System.Proxy.Enabled,
            ModsecurityAvailable: ModSecurityProbe.IsAvailable(),
            WebmailAvailable: WebmailProbe.ContainerRunning(config) || WebmailProbe.HttpReachable(config),
            WebmailPort: WebmailPaths.DefaultPort);
    }
}

public sealed record HostingServicesSnapshot(
    [property: JsonPropertyName("ftp_enabled")] bool FtpEnabled,
    [property: JsonPropertyName("ftp_listening")] bool FtpListening,
    [property: JsonPropertyName("ftp_port")] int FtpPort,
    [property: JsonPropertyName("ftp_pasv_min")] int FtpPasvMin,
    [property: JsonPropertyName("ftp_pasv_max")] int FtpPasvMax,
    [property: JsonPropertyName("ftp_public_ip")] string FtpPublicIp,
    [property: JsonPropertyName("proxy_provider")] string ProxyProvider,
    [property: JsonPropertyName("proxy_enabled")] bool ProxyEnabled,
    [property: JsonPropertyName("modsecurity_available")] bool ModsecurityAvailable,
    [property: JsonPropertyName("webmail_available")] bool WebmailAvailable,
    [property: JsonPropertyName("webmail_port")] int WebmailPort);
