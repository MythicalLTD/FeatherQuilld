using System.Net;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Sftp;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.WebSpaces;

public sealed class TransferServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppConfig _config;

    public TransferServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-xfer-" + Guid.NewGuid().ToString("N"));
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
        Directory.CreateDirectory(_config.System.TmpDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Outgoing_MissingSpace_Throws()
    {
        var panel = new FakePanel();
        var store = CreateStore(panel);
        var transfers = new WebSpaceTransferService(_config, store, panel);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transfers.OutgoingAsync(Guid.NewGuid(), "http://127.0.0.1:9/upload", "tok"));
    }

    [Fact]
    public async Task Outgoing_UnreachableUrl_ReportsFailed()
    {
        var uuid = Guid.NewGuid();
        var panel = new FakePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "xfer",
                Domains = ["xfer.example.test"],
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 10 },
            },
        };
        var store = CreateStore(panel);
        store.CreateFromPanel(new CreateWebSpaceRequest { Uuid = uuid, SkipScripts = true });

        var transfers = new WebSpaceTransferService(_config, store, panel);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transfers.OutgoingAsync(uuid, "http://127.0.0.1:1/nope", "token_id.token"));

        Assert.Contains("Outgoing transfer failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(panel.TransferReports, r => r.Uuid == uuid && !r.Successful);
        // Space should still exist (delete only on success).
        Assert.NotNull(store.Get(uuid));
    }

    [Fact]
    public async Task Incoming_AlreadyExists_Throws()
    {
        var uuid = Guid.NewGuid();
        var panel = new FakePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "dup",
                Domains = ["dup.example.test"],
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 10 },
            },
        };
        var store = CreateStore(panel);
        store.CreateFromPanel(new CreateWebSpaceRequest { Uuid = uuid, SkipScripts = true });
        var transfers = new WebSpaceTransferService(_config, store, panel);

        await using var empty = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transfers.IncomingAsync(uuid, empty, startOnCompletion: false));
    }

    [Fact]
    public async Task Outgoing_Success_DeletesSpaceAndReportsOk()
    {
        var uuid = Guid.NewGuid();
        var panel = new FakePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "ok-xfer",
                Domains = ["ok-xfer.example.test"],
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 10 },
            },
        };
        var store = CreateStore(panel);
        store.CreateFromPanel(new CreateWebSpaceRequest { Uuid = uuid, SkipScripts = true });

        var progress = new TransferProgressService();
        var handler = new SuccessUploadHandler();
        var http = new HttpClient(handler);
        var transfers = new WebSpaceTransferService(_config, store, panel, progress, http);

        await transfers.OutgoingAsync(uuid, "http://127.0.0.1/upload", "dest-token");

        Assert.Null(store.Get(uuid));
        Assert.Contains(panel.TransferReports, r => r.Uuid == uuid && r.Successful);
        var state = progress.Get(uuid);
        Assert.NotNull(state);
        Assert.Equal(TransferPhase.Completed, state!.Phase);
        Assert.Equal("outgoing", state.Direction);
    }

    [Fact]
    public async Task Incoming_TarGz_RegistersSpaceWithMarkerFile()
    {
        var uuid = Guid.NewGuid();
        var panel = new FakePanel
        {
            Config = new PanelWebSpaceConfig
            {
                Uuid = uuid,
                Name = "incoming",
                Domains = ["incoming.example.test"],
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 10 },
            },
        };
        var store = CreateStore(panel);
        var progress = new TransferProgressService();
        var transfers = new WebSpaceTransferService(_config, store, panel, progress);

        var fsPath = Path.Combine(_config.System.Data, uuid.ToString(), "public");
        Directory.CreateDirectory(fsPath);
        await File.WriteAllTextAsync(Path.Combine(fsPath, "marker.txt"), "xfer-ok");
        var archivePath = Path.Combine(_config.System.TmpDirectory, "in.tar.gz");
        CreateTarGz(Path.GetDirectoryName(fsPath)!, archivePath);
        await using var archive = File.OpenRead(archivePath);

        await transfers.IncomingAsync(uuid, archive, startOnCompletion: false);

        Assert.NotNull(store.Get(uuid));
        Assert.True(File.Exists(Path.Combine(store.EffectiveFsPath(uuid), "public", "marker.txt")));
        var state = progress.Get(uuid);
        Assert.NotNull(state);
        Assert.Equal(TransferPhase.Completed, state!.Phase);
    }

    private static void CreateTarGz(string sourceDir, string archivePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using var file = File.Create(archivePath);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal);
        System.Formats.Tar.TarFile.CreateFromDirectory(sourceDir, gzip, includeBaseDirectory: false);
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
        public List<(Guid Uuid, bool Successful)> TransferReports { get; } = [];

        public Task<AppConfig> FetchRuntimeConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppConfig());

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
            Guid uuid, bool successful, bool reinstall = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SyncWebSpaceStateAsync(
            Guid uuid, int backendPort, string state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReportTransferAsync(Guid uuid, bool successful, CancellationToken cancellationToken = default)
        {
            TransferReports.Add((uuid, successful));
            return Task.CompletedTask;
        }

        public Task ReportActivitiesAsync(
            IReadOnlyList<PanelActivityEntry> entries,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SftpAuthResult?> AuthenticateSftpAsync(
            string type, string username, string password, string? publicKey = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SftpAuthResult?>(null);
    }

    private sealed class SuccessUploadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}"),
            });
        }
    }
}
