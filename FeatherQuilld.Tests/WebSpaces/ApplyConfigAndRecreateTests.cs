using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Sftp;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.WebSpaces;

public sealed class ApplyConfigAndRecreateTests : IDisposable
{
    private readonly string _root;
    private readonly AppConfig _config;

    public ApplyConfigAndRecreateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-sync-" + Guid.NewGuid().ToString("N"));
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
    public void ApplyConfigFromPanel_UpdatesContainerImage_WithoutReinstall()
    {
        var uuid = Guid.NewGuid();
        var panel = new MutablePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "php-site",
                Domains = ["php.example.test"],
                Ssl = false,
                BackendPort = 21080,
                Webplate = new PanelWebPlateRef
                {
                    Id = "php81",
                    Runtime = "php",
                    DockerImage = "featherquilld/php:8.1",
                },
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

        Assert.Equal("featherquilld/php:8.1", space.ContainerImage);
        var installCountBefore = panel.InstallReports.Count;

        panel.Config.Webplate = new PanelWebPlateRef
        {
            Id = "php82",
            Runtime = "php",
            DockerImage = "featherquilld/php:8.2",
        };

        var synced = store.ApplyConfigFromPanel(uuid);

        Assert.Equal("featherquilld/php:8.2", synced.ContainerImage);
        Assert.Equal("php82", synced.WebPlateId);
        Assert.Equal(installCountBefore, panel.InstallReports.Count);
    }

    [Fact]
    public void ApplyConfigFromPanel_UpdatesBackendPort_WhenPanelSendsValue()
    {
        var uuid = Guid.NewGuid();
        var panel = new MutablePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "php-port",
                Domains = ["port.example.test"],
                Ssl = false,
                BackendPort = 21080,
                Webplate = new PanelWebPlateRef
                {
                    Id = "php81",
                    Runtime = "php",
                    DockerImage = "featherquilld/php:8.1",
                },
                Build = new PanelWebSpaceBuild { DiskSpace = 100 },
                Meta = new PanelWebSpaceMeta { DocumentRoot = "public" },
            },
        };

        var store = CreateStore(panel);
        store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = uuid,
            SkipScripts = true,
            StartOnCompletion = false,
        });

        panel.Config.BackendPort = 21999;
        var synced = store.ApplyConfigFromPanel(uuid);

        Assert.Equal(21999, synced.BackendPort);
    }

    [Fact]
    public void ApplyConfigFromPanel_StoresOwnerAcmeEmail()
    {
        var uuid = Guid.NewGuid();
        var panel = new MutablePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "ssl-site",
                Domains = ["ssl.example.test"],
                Ssl = true,
                AcmeEmail = "owner@example.test",
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 100 },
                Meta = new PanelWebSpaceMeta { DocumentRoot = "public" },
            },
        };

        var store = CreateStore(panel);
        var created = store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = uuid,
            SkipScripts = true,
            StartOnCompletion = false,
        });
        Assert.Equal("owner@example.test", created.AcmeEmail);
        Assert.Equal("owner@example.test", created.ResolveAcmeEmail("ops@node.test"));

        panel.Config.AcmeEmail = "new-owner@example.test";
        var synced = store.ApplyConfigFromPanel(uuid);
        Assert.Equal("new-owner@example.test", synced.AcmeEmail);
        Assert.Equal("new-owner@example.test", synced.ResolveAcmeEmail("ops@node.test"));
    }

    [Fact]
    public void ApplyConfigFromPanel_CopiesWafDenyIpsAndRouteDocumentRoot()
    {
        var uuid = Guid.NewGuid();
        var panel = new MutablePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "hosted",
                Domains = ["app.example.test", "blog.example.test"],
                DomainRoutes =
                [
                    new PanelDomainRoute { Domain = "app.example.test", Type = "primary", DocumentRoot = "public" },
                    new PanelDomainRoute { Domain = "blog.example.test", Type = "alias", DocumentRoot = "sites/blog" },
                ],
                Ssl = false,
                WafEnabled = true,
                WafDenyIps = ["203.0.113.9", "not-an-ip", "198.51.100.0/24"],
                WafDenyPaths = ["/xmlrpc.php", "../bad", "/.well-known/x"],
                BackendPort = 21080,
                Webplate = new PanelWebPlateRef
                {
                    Id = "php83",
                    Runtime = "php",
                    DockerImage = "php:8.3-apache",
                },
                Build = new PanelWebSpaceBuild { DiskSpace = 100 },
            },
        };

        var store = CreateStore(panel);
        var space = store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = uuid,
            SkipScripts = true,
            StartOnCompletion = false,
        });

        Assert.True(space.WafEnabled);
        Assert.Equal(["203.0.113.9", "198.51.100.0/24"], space.WafDenyIps);
        Assert.Equal(["/xmlrpc.php"], space.WafDenyPaths);
        Assert.Equal("public", space.DomainRoutes.Single(r => r.Domain == "app.example.test").DocumentRoot);
        Assert.Equal("sites/blog", space.DomainRoutes.Single(r => r.Domain == "blog.example.test").DocumentRoot);

        panel.Config.WafDenyIps = ["192.0.2.1"];
        panel.Config.DomainRoutes =
        [
            new PanelDomainRoute { Domain = "app.example.test", Type = "primary", DocumentRoot = "web" },
        ];
        var synced = store.ApplyConfigFromPanel(uuid);
        Assert.Equal(["192.0.2.1"], synced.WafDenyIps);
        Assert.Equal("web", synced.DomainRoutes.Single().DocumentRoot);
    }

    [Fact]
    public void RecreateRuntime_PreservesDataDirectory()
    {
        var uuid = Guid.NewGuid();
        var panel = new MutablePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "php-data",
                Domains = ["data.example.test"],
                Ssl = false,
                BackendPort = 21081,
                Webplate = new PanelWebPlateRef
                {
                    Id = "php81",
                    Runtime = "php",
                    DockerImage = "featherquilld/php:8.1",
                },
                Build = new PanelWebSpaceBuild { DiskSpace = 100 },
                Meta = new PanelWebSpaceMeta { DocumentRoot = "public" },
            },
        };

        var store = CreateStore(panel);
        store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = uuid,
            SkipScripts = true,
            StartOnCompletion = false,
        });

        var dataPath = Path.Combine(_config.System.Data, uuid.ToString());
        var marker = Path.Combine(dataPath, "keeper.txt");
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(marker, "keep-me");

        try
        {
            store.RecreateRuntime(uuid);
        }
        catch
        {
            // Docker may be unavailable in CI; data preservation is still asserted below.
        }

        Assert.True(File.Exists(marker));
        Assert.Equal("keep-me", File.ReadAllText(marker));
    }

    private WebSpaceStore CreateStore(IPanelClient panel) =>
        new(
            _config,
            panel,
            new ReverseProxyManager(_config),
            new PortAllocator(_config.System.Proxy),
            new WebSpaceInstaller(_config.Docker),
            new WebSpaceRuntime(_config.Docker));

    private sealed class MutablePanel : IPanelClient
    {
        public PanelWebSpaceConfig Config { get; set; } = new();
        public List<(Guid Uuid, bool Successful, bool Reinstall)> InstallReports { get; } = [];

        public Task<AppConfig> FetchRuntimeConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppConfig());

        public Task<string> FetchRuntimeConfigYamlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<PanelHealthResponse> FetchHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelHealthResponse { Success = true });

        public Task<PanelWebSpaceConfig> FetchWebSpaceAsync(Guid uuid, CancellationToken cancellationToken = default)
        {
            Config.Uuid = uuid;
            return Task.FromResult(Config);
        }

        public Task<PanelInstallScript> FetchWebSpaceInstallAsync(Guid uuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelInstallScript { Script = "" });

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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
