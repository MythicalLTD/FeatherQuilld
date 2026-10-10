using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Config.Sftp;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Sftp;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Sftp;

/// <summary>
/// End-to-end tests that drive the REAL OpenSSH client against the embedded
/// SFTP server, using key-first authentication exactly like FileZilla does
/// ("none" probe, publickey probe without signature, then the signed key).
///
/// Regression coverage:
/// 1. A successful key-first login must survive — the panel's password-attempt
///    limit (default 3) used to be passed 1:1 as the library's MaxAuthAttempts,
///    which counts EVERY auth request, so the third request (the signed key)
///    tripped "too many attempts" right after USERAUTH_SUCCESS and the
///    connection was killed. FileZilla clients failed 8/8.
/// 2. The sftp subsystem must be accepted via ChannelRequestHandler installed
///    at ConnectionLayer construction (no post-auth subscriber race).
/// 3. A scripted login must disconnect cleanly and quickly — previously nobody
///    closed the channel on session end and one-shot clients hung for their
///    full disconnect timeout (12s+).
///
/// The tests skip themselves when the OpenSSH client/ssh-keygen are not
/// installed (e.g. a minimal SDK container); CI (ubuntu) runs them for real.
/// </summary>
public sealed class SftpEndToEndClientTests : IDisposable
{
    private readonly string _root;
    private readonly AppConfig _config;
    private readonly Logger _logger;

    public SftpEndToEndClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-sftp-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _logger = new Logger(new LoggerOptions
        {
            Directory = Path.Combine(_root, "logs"),
            Debug = true,
            MaxArchives = 0,
        });
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
                KeyAlgorithm = "ssh-ed25519",
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
        try { _logger.Dispose(); } catch { /* ignore */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string LogTail()
    {
        try
        {
            var path = Path.Combine(_root, "logs", "latest.log");
            if (!File.Exists(path))
                return "(no server log)";
            var lines = File.ReadLines(path).TakeLast(40);
            return "server log tail:\n" + string.Join("\n", lines);
        }
        catch
        {
            return "(server log unreadable)";
        }
    }

    [Fact]
    public async Task KeyFirstLogin_FileZillaStyle_SucceedsAndListsFiles()
    {
        if (!OpenSshAvailable())
            return; // environment without the OpenSSH client (slim container) — CI runs this for real

        var webspaceUuid = Guid.NewGuid();
        var dataPath = Path.Combine(_config.System.Data, webspaceUuid.ToString());
        Directory.CreateDirectory(dataPath);
        await File.WriteAllTextAsync(Path.Combine(dataPath, "hello.txt"), "hi");

        var panel = new E2ePanel(webspaceUuid, acceptKey: true, acceptPassword: false, correctPassword: null);
        var store = CreateStore(panel);
        store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = webspaceUuid,
            SkipScripts = true,
            StartOnCompletion = false,
        });

        var service = new SftpHostedService(_config, store, panel, _logger);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var key = await GenerateClientKeyAsync();
            var batch = Path.Combine(_root, "batch.txt");
            await File.WriteAllTextAsync(batch, "ls\nbye\n");

            var (exitCode, stdout, stderr, timedOut) = await RunSftpAsync(key, batch, TimeSpan.FromSeconds(30));

            Assert.False(timedOut, $"sftp (key-first) hung. stdout: {stdout} stderr: {stderr}\n{LogTail()}");
            Assert.True(exitCode == 0,
                $"sftp (key-first) failed with exit code {exitCode}. stdout: {stdout} stderr: {stderr}\n{LogTail()}");
            Assert.Contains("hello.txt", stdout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task ScriptLogin_DisconnectsCleanlyAndQuickly()
    {
        if (!OpenSshAvailable())
            return; // environment without the OpenSSH client (slim container) — CI runs this for real

        var webspaceUuid = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_config.System.Data, webspaceUuid.ToString()));

        var panel = new E2ePanel(webspaceUuid, acceptKey: true, acceptPassword: false, correctPassword: null);
        var store = CreateStore(panel);
        store.CreateFromPanel(new CreateWebSpaceRequest
        {
            Uuid = webspaceUuid,
            SkipScripts = true,
            StartOnCompletion = false,
        });

        var service = new SftpHostedService(_config, store, panel, _logger);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var key = await GenerateClientKeyAsync();
            var batch = Path.Combine(_root, "batch.txt");
            await File.WriteAllTextAsync(batch, "ls\nbye\n");

            var stopwatch = Stopwatch.StartNew();
            var (exitCode, stdout, stderr, timedOut) = await RunSftpAsync(key, batch, TimeSpan.FromSeconds(30));
            stopwatch.Stop();

            Assert.False(timedOut, $"sftp hung. stdout: {stdout} stderr: {stderr}\n{LogTail()}");
            Assert.True(exitCode == 0, $"sftp failed with exit code {exitCode}. stdout: {stdout} stderr: {stderr}\n{LogTail()}");
            // Before the channel-close fix, the client hung on disconnect for its
            // full timeout (12s+). A clean teardown completes in well under 10s
            // even on a loaded CI runner.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"sftp session took {stopwatch.Elapsed.TotalSeconds:F1}s — disconnect is hanging");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private WebSpaceStore CreateStore(IPanelClient panel) =>
        new(
            _config,
            panel,
            new ReverseProxyManager(_config),
            new PortAllocator(_config.System.Proxy),
            new WebSpaceInstaller(_config.Docker),
            new WebSpaceRuntime(_config.Docker));

    private async Task<string> GenerateClientKeyAsync()
    {
        var keyPath = Path.Combine(_root, "client-key");
        var psi = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            ArgumentList = { "-t", "ed25519", "-N", "", "-f", keyPath, "-q" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh-keygen");
        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("ssh-keygen failed to generate a client key");
        return keyPath;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunSftpAsync(
        string keyPath, string batchPath, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sftp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(keyPath);
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(_config.Sftp.Port.ToString());
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add(batchPath);
        psi.ArgumentList.Add("-oBatchMode=yes");
        psi.ArgumentList.Add("-oStrictHostKeyChecking=no");
        psi.ArgumentList.Add("-oUserKnownHostsFile=/dev/null");
        psi.ArgumentList.Add("-oIdentitiesOnly=yes");
        psi.ArgumentList.Add("-oConnectionAttempts=1");
        psi.ArgumentList.Add("tester@127.0.0.1");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sftp client");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        try
        {
            await proc.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            var partialOut = await stdoutTask;
            var partialErr = await stderrTask;
            return (proc.ExitCode, partialOut, partialErr, TimedOut: true);
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);
    }

    private static bool OpenSshAvailable()
    {
        try
        {
            return IsOnPath("ssh-keygen") && IsOnPath("sftp");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOnPath(string binary)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "which",
            ArgumentList = { binary },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
            return false;
        proc.WaitForExit(5000);
        return proc.ExitCode == 0;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class E2ePanel : IPanelClient
    {
        private readonly Guid _uuid;
        private readonly bool _acceptKey;
        private readonly bool _acceptPassword;
        private readonly string? _correctPassword;

        public E2ePanel(Guid uuid, bool acceptKey, bool acceptPassword, string? correctPassword)
        {
            _uuid = uuid;
            _acceptKey = acceptKey;
            _acceptPassword = acceptPassword;
            _correctPassword = correctPassword;
        }

        public Task<AppConfig> FetchRuntimeConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppConfig());

        public Task<string> FetchRuntimeConfigYamlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("");

        public Task<PanelHealthResponse> FetchHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelHealthResponse { Success = true });

        public Task<PanelWebSpaceConfig> FetchWebSpaceAsync(Guid uuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelWebSpaceConfig
            {
                Uuid = _uuid,
                Name = "e2e-webspace",
                Domains = ["example.test"],
                Ssl = false,
                Webplate = new PanelWebPlateRef { Id = "static", Runtime = "static" },
                Build = new PanelWebSpaceBuild { DiskSpace = 100 },
                Meta = new PanelWebSpaceMeta { DocumentRoot = "public" },
            });

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
            CancellationToken cancellationToken = default)
        {
            var accepted = type switch
            {
                "public_key" => _acceptKey,
                "password" => _acceptPassword && password == _correctPassword,
                _ => false,
            };
            if (!accepted)
                return Task.FromResult<SftpAuthResult?>(null);

            return Task.FromResult<SftpAuthResult?>(new SftpAuthResult
            {
                Server = _uuid.ToString(),
                User = username,
                Permissions = ["*"],
            });
        }

        public Task AcmeDnsAsync(
            Guid uuid,
            string action,
            string name,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
