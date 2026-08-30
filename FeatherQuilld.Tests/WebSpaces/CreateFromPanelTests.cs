using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Sftp;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.WebSpaces;

public sealed class CreateFromPanelTests : IDisposable
{
    private readonly string _root;
    private readonly AppConfig _config;

    public CreateFromPanelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-create-" + Guid.NewGuid().ToString("N"));
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
        };
        _config.System.Quotas.Enabled = false;
        _config.System.Proxy.Enabled = false;
        _config.Docker.RuntimeReconciliation.Enabled = false;
        Directory.CreateDirectory(_config.System.Data);
        Directory.CreateDirectory(_config.System.VmountDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void CreateFromPanel_SkipScripts_ReportsSuccessfulInstall()
    {
        var uuid = Guid.NewGuid();
        var panel = new FakePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "soak-static",
                Domains = ["example.test"],
                Ssl = false,
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 100 },
                Meta = new PanelWebSpaceMeta { DocumentRoot = "public" },
            },
        };

        var store = CreateStore(panel);
        var space = store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = uuid,
            SkipScripts = true,
            StartOnCompletion = false,
        });

        Assert.Equal(WebSpaceStatus.Installed, space.Status);
        Assert.Contains(panel.InstallReports, r => r.Uuid == uuid && r.Successful && !r.Reinstall);
        Assert.Contains(panel.StateSyncs, s => s.Uuid == uuid);
    }

    [Fact]
    public void CreateFromPanel_InstallFetchFails_ReportsFailed()
    {
        var uuid = Guid.NewGuid();
        var panel = new FakePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "fail-install",
                Domains = ["fail.example.test"],
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 50 },
            },
            ThrowOnInstallFetch = true,
        };

        var store = CreateStore(panel);
        var space = store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = uuid,
            SkipScripts = false,
        });

        Assert.Equal(WebSpaceStatus.Installing, space.Status);

        WebSpace? final = null;
        for (var i = 0; i < 100; i++)
        {
            final = store.Get(uuid);
            if (final?.Status is WebSpaceStatus.Failed or WebSpaceStatus.Installed)
                break;
            Thread.Sleep(50);
        }

        Assert.NotNull(final);
        Assert.Equal(WebSpaceStatus.Failed, final.Status);
        Assert.Contains(panel.InstallReports, r => r.Uuid == uuid && !r.Successful && !r.Reinstall);
    }

    private WebSpaceStore CreateStore(IPanelClient panel) =>
        new(
            _config,
            panel,
            new ReverseProxyManager(_config),
            new PortAllocator(_config.System.Proxy),
            new WebSpaceInstaller(_config.Docker),
            new WebSpaceRuntime(_config.Docker));

    private sealed class FakePanel : IPanelClient
    {
        public PanelWebSpaceConfig Config { get; set; } = new();
        public PanelInstallScript Install { get; set; } = new() { Script = "" };
        public bool ThrowOnInstallFetch { get; set; }
        public List<(Guid Uuid, bool Successful, bool Reinstall)> InstallReports { get; } = [];
        public List<(Guid Uuid, int Port, string State)> StateSyncs { get; } = [];

        public Task<AppConfig> FetchRuntimeConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppConfig());

        public Task<PanelHealthResponse> FetchHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelHealthResponse { Success = true });

        public Task<PanelWebSpaceConfig> FetchWebSpaceAsync(Guid uuid, CancellationToken cancellationToken = default)
        {
            Config.Uuid = uuid;
            return Task.FromResult(Config);
        }

        public Task<PanelInstallScript> FetchWebSpaceInstallAsync(Guid uuid, CancellationToken cancellationToken = default)
        {
            if (ThrowOnInstallFetch)
                throw new InvalidOperationException("install fetch failed");
            return Task.FromResult(Install);
        }

        public Task ReportWebSpaceInstallAsync(
            Guid uuid,
            bool successful,
            bool reinstall = false,
            CancellationToken cancellationToken = default)
        {
            InstallReports.Add((uuid, successful, reinstall));
            return Task.CompletedTask;
        }

        public Task SyncWebSpaceStateAsync(
            Guid uuid,
            int backendPort,
            string state,
            CancellationToken cancellationToken = default)
        {
            StateSyncs.Add((uuid, backendPort, state));
            return Task.CompletedTask;
        }

        public Task ReportTransferAsync(Guid uuid, bool successful, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReportActivitiesAsync(
            IReadOnlyList<PanelActivityEntry> entries,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SftpAuthResult?> AuthenticateSftpAsync(
            string type,
            string username,
            string password,
            string? publicKey = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SftpAuthResult?>(null);

        public Task AcmeDnsAsync(
            Guid uuid,
            string action,
            string name,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
