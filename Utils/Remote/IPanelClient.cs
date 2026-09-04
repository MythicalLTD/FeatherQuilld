using FeatherQuilld.Utils.Sftp;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Remote;

/// <summary>HTTP client seam for FeatherPanel quilld-remote API routes.</summary>
public interface IPanelClient
{
    Task<AppConfig> FetchRuntimeConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>Raw runtime YAML from the panel (preserves which keys were omitted cuz im just stupid (╯°□°）╯︵ ┻━┻)).</summary>
    Task<string> FetchRuntimeConfigYamlAsync(CancellationToken cancellationToken = default);

    Task<PanelHealthResponse> FetchHealthAsync(CancellationToken cancellationToken = default);

    Task<PanelWebSpaceConfig> FetchWebSpaceAsync(Guid uuid, CancellationToken cancellationToken = default);

    Task<PanelInstallScript> FetchWebSpaceInstallAsync(Guid uuid, CancellationToken cancellationToken = default);

    Task ReportWebSpaceInstallAsync(
        Guid uuid,
        bool successful,
        bool reinstall = false,
        CancellationToken cancellationToken = default);

    Task SyncWebSpaceStateAsync(
        Guid uuid,
        int backendPort,
        string state,
        CancellationToken cancellationToken = default);

    Task ReportTransferAsync(
        Guid uuid,
        bool successful,
        CancellationToken cancellationToken = default);

    Task ReportActivitiesAsync(
        IReadOnlyList<PanelActivityEntry> entries,
        CancellationToken cancellationToken = default);

    Task<SftpAuthResult?> AuthenticateSftpAsync(
        string type,
        string username,
        string password,
        string? publicKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>POST /api/quilld-remote/webspaces/{uuid}/acme-dns set or clear ACME DNS-01 TXT via panel PowerDNS.</summary>
    Task AcmeDnsAsync(
        Guid uuid,
        string action,
        string name,
        string content,
        CancellationToken cancellationToken = default);
}
