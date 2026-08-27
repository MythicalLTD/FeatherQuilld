using System.Net;
using System.Net.Sockets;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Config.Sftp;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Sftp;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Sftp;

public sealed class SftpListenSmokeTests : IDisposable
{
    private readonly string _root;
    private readonly AppConfig _config;

    public SftpListenSmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-sftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _config = new AppConfig
        {
            System =
            {
                RootDirectory = _root,
                Data = Path.Combine(_root, "volumes"),
                VmountDirectory = Path.Combine(_root, "vmounts"),
                TmpDirectory = Path.Combine(_root, "tmp"),
                DiskLimiterMode = "none",
            },
            Sftp = new SftpConfig
            {
                Enabled = true,
                Port = GetFreePort(),
            },
        };
        _config.System.Quotas.Enabled = false;
        _config.System.Proxy.Enabled = false;
        _config.Docker.RuntimeReconciliation.Enabled = false;
        Directory.CreateDirectory(_config.System.Data);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SftpHostedService_StartsAndAcceptsTcpConnection()
    {
        var panel = new StubPanel();
        var store = new WebSpaceStore(
            _config,
            panel,
            new ReverseProxyManager(_config),
            new PortAllocator(_config.System.Proxy),
            new WebSpaceInstaller(_config.Docker),
            new WebSpaceRuntime(_config.Docker));

        var service = new SftpHostedService(_config, store, panel);
        await service.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        var connectTask = client.ConnectAsync(IPAddress.Loopback, _config.Sftp.Port);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.Connected);

        await service.StopAsync(CancellationToken.None);
        service.Dispose();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubPanel : IPanelClient
    {
        public Task<AppConfig> FetchRuntimeConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppConfig());

        public Task<PanelHealthResponse> FetchHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelHealthResponse { Success = true });

        public Task<PanelWebSpaceConfig> FetchWebSpaceAsync(Guid uuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelWebSpaceConfig { Uuid = uuid });

        public Task<PanelInstallScript> FetchWebSpaceInstallAsync(Guid uuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelInstallScript { Script = "" });

        public Task ReportWebSpaceInstallAsync(
            Guid uuid, bool successful, bool reinstall = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SyncWebSpaceStateAsync(
            Guid uuid, int backendPort, string state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReportTransferAsync(Guid uuid, bool successful, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReportActivitiesAsync(
            IReadOnlyList<PanelActivityEntry> entries,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SftpAuthResult?> AuthenticateSftpAsync(
            string type, string username, string password, string? publicKey = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SftpAuthResult?>(null);
    }
}
