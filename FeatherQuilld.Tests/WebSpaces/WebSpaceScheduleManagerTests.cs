using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Sftp;
using FeatherQuilld.Utils.WebSpaces;
using FeatherQuilld.Utils.WebSpaces.Backups;
using FeatherQuilld.Utils.WebSpaces.Malware;
using FeatherQuilld.Utils.WebSpaces.Schedules;
using Microsoft.Extensions.Logging.Abstractions;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.WebSpaces;

public sealed class WebSpaceScheduleManagerTests
{
    [Fact]
    public async Task Abort_CancelsInFlightSchedule()
    {
        var uuid = Guid.NewGuid().ToString("D");
        var panel = new StubPanel();
        var testRoot = Path.Combine(Path.GetTempPath(), "fq-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var config = new AppConfig
        {
            System =
            {
                RootDirectory = testRoot,
                Data = Path.Combine(testRoot, "volumes"),
                VmountDirectory = Path.Combine(testRoot, "vmounts"),
                TmpDirectory = Path.Combine(testRoot, "tmp"),
                BackupDirectory = Path.Combine(testRoot, "backups"),
                DiskLimiterMode = "none",
            },
        };
        config.System.Proxy.Enabled = false;
        config.Docker.RuntimeReconciliation.Enabled = false;

        var store = new WebSpaceStore(
            config,
            panel,
            new ReverseProxyManager(config),
            new PortAllocator(config.System.Proxy),
            new WebSpaceInstaller(config.Docker),
            new WebSpaceRuntime(config.Docker));

        var backupService = new WebSpaceBackupService(
            config,
            store,
            new LocalBackupStore(config.System));

        var manager = new WebSpaceScheduleManager(
            store,
            backupService,
            new WebSpaceMalwareScanService(config, store),
            panel,
            activityReporter: null,
            NullLogger<WebSpaceScheduleManager>.Instance);

        manager.SyncSchedules(uuid,
        [
            new WebSpaceScheduleDefinition
            {
                Id = 1,
                Name = "delayed",
                Tasks =
                [
                    new WebSpaceScheduleTaskDefinition
                    {
                        Id = 1,
                        SequenceId = 1,
                        Action = "noop",
                        TimeOffset = 60,
                    },
                ],
            },
        ]);

        var triggerTask = manager.TriggerAsync(uuid, 1, CancellationToken.None);

        await Task.Delay(200);
        Assert.True(manager.IsRunning(uuid));

        Assert.True(manager.Abort(uuid));

        await triggerTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(manager.IsRunning(uuid));
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
            Guid uuid,
            bool successful,
            bool reinstall = false,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
