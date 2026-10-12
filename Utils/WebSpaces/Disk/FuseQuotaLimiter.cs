using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Native;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.WebSpaces.Disk;

/// <summary>
/// Wings-style FuseQuota limiter: mounts a FUSE passthrough with a hard disk quota
/// and talks to the daemon over a Unix socket.
/// </summary>
public sealed class FuseQuotaLimiter
{
    private readonly AppConfig _config;
    private readonly AppLogger? _logger;
    private readonly Guid _webSpaceUuid;
    private readonly string _sourcePath;
    private readonly string _mountPath;
    private readonly string _socketPath;
    private readonly long _diskLimitBytes;
    private readonly int? _ownerUid;
    private readonly int? _ownerGid;

    public FuseQuotaLimiter(
        AppConfig config,
        Guid webSpaceUuid,
        string sourcePath,
        long diskLimitBytes,
        AppLogger? logger = null,
        int? ownerUid = null,
        int? ownerGid = null)
    {
        _config = config;
        _logger = logger;
        _webSpaceUuid = webSpaceUuid;
        _sourcePath = sourcePath;
        _diskLimitBytes = diskLimitBytes;
        _ownerUid = ownerUid;
        _ownerGid = ownerGid;
        _mountPath = GetMountPath(config.System, webSpaceUuid);
        _socketPath = _mountPath + ".fqsock";
    }

    public string MountPath => _mountPath;
    public string SourcePath => _sourcePath;
    public string SocketPath => _socketPath;

    public static string GetMountPath(SystemConfig system, Guid uuid) =>
        Path.Combine(system.VmountDirectory, uuid.ToString(), "fs");

    public static string ResolveBinaryPath(SystemConfig system) =>
        TryResolveBinaryPath(system, out var path) ? path : system.FusequotaPath;

    /// <summary>Resolves the fusequota binary to a concrete path when possible.</summary>
    public static bool TryResolveBinaryPath(SystemConfig system, out string path)
    {
        var configured = system.FusequotaPath?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            path = Path.GetFullPath(configured);
            return true;
        }

        var env = Environment.GetEnvironmentVariable("FUSEQUOTA_BIN");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            path = Path.GetFullPath(env);
            return true;
        }

        var nextToApp = FuseQuotaBinaryProvisioner.CachePath;
        if (File.Exists(nextToApp))
        {
            path = nextToApp;
            return true;
        }

        var bins = Path.Combine(Directory.GetCurrentDirectory(), "bins", "fusequota");
        if (File.Exists(bins))
        {
            path = Path.GetFullPath(bins);
            return true;
        }

        var vendored = Path.Combine(Directory.GetCurrentDirectory(), "fusequota", "build", "fusequota");
        if (File.Exists(vendored))
        {
            path = Path.GetFullPath(vendored);
            return true;
        }

        var name = string.IsNullOrWhiteSpace(configured) || configured.Contains(Path.DirectorySeparatorChar)
            ? "fusequota"
            : configured;

        var onPath = FindOnPath(name);
        if (!string.IsNullOrEmpty(onPath))
        {
            path = onPath;
            return true;
        }

        path = configured;
        return false;
    }

    public static bool IsBinaryAvailable(SystemConfig system) =>
        TryResolveBinaryPath(system, out _);

    public void Setup()
    {
        _logger?.Debug(LoggerTypes.Disk, $"fusequota setup uuid={_webSpaceUuid} source={_sourcePath} mount={_mountPath}");
        Directory.CreateDirectory(_sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_mountPath)!);

        // A stale FUSE mount makes this throw ("The file ... already exists", ENOTCONN), and
        // that used to abort the whole attach before StartupAsync could repair the mount.
        try
        {
            Directory.CreateDirectory(_mountPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Debug(LoggerTypes.Disk,
                $"mount path {_mountPath} could not be created ({ex.Message}); Startup will repair it");
        }
    }

    public async Task<bool> StartupAsync(CancellationToken cancellationToken = default)
    {
        var bin = await FuseQuotaBinaryProvisioner.EnsureAsync(_config.System, _logger, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bin) || !File.Exists(bin))
            throw new InvalidOperationException(
                "fusequota binary is not available. FeatherQuilld downloads it automatically on Linux when disk_limiter_mode is fuse_quota; set system.fusequota_path or FUSEQUOTA_BIN to use a custom binary.");

        _logger?.Debug(LoggerTypes.Disk, $"fusequota startup uuid={_webSpaceUuid} bin={bin} limit={_diskLimitBytes}");

        if (!await EnsureHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger?.Debug(LoggerTypes.Disk, $"fusequota already running for {_webSpaceUuid} (socket ok)");
            return false;
        }

        _logger?.Info(LoggerTypes.Disk, $"fusequota ready for {_webSpaceUuid}");
        return true;
    }

    /// <summary>
    /// Makes sure this WebSpace has a live quota mount and repairs it when it does not.
    /// A mount can survive as a dead entry (every access fails with ENOTCONN, e.g. after the
    /// daemon was restarted or killed) which makes the site answer 404/502 even though the
    /// files are present - and a plain "already running? spawn" check never noticed it,
    /// because mounting on top of the stale entry fails with "already exists".
    /// </summary>
    /// <returns>true when the mount had to be (re)created, false when it was healthy.</returns>
    public async Task<bool> EnsureHealthyAsync(CancellationToken cancellationToken = default)
    {
        var mounted = IsMounted(_mountPath);
        var accessible = IsMountAccessible(_mountPath);
        var socketOk = await IsSocketFunctionalAsync(cancellationToken).ConfigureAwait(false);

        if (mounted && accessible && socketOk)
            return false;

        if (mounted)
        {
            _logger?.Warning(LoggerTypes.Disk,
                $"fusequota mount {_mountPath} for {_webSpaceUuid} is stale (accessible={accessible}, socket={socketOk}); remounting");
            await UnmountStaleAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(_socketPath))
        {
            // Leftover socket from a previous run; a fresh daemon cannot bind it.
            try { File.Delete(_socketPath); } catch { /* best-effort */ }
        }

        await SpawnDaemonAsync(cancellationToken).ConfigureAwait(false);
        await WaitForSocketAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>True when the path is currently a mount point (from /proc/self/mounts).</summary>
    public static bool IsMounted(string mountPath)
    {
        try
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountPath));
            foreach (var line in File.ReadLines("/proc/self/mounts"))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length >= 2
                    && string.Equals(WebSpaceFsOwnership.UnescapeMountField(parts[1]), target, StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
            // treat as not mounted
        }

        return false;
    }

    /// <summary>
    /// False when the path cannot be stat()ed. Dead FUSE mounts surface here as an
    /// IOException (ENOTCONN / "Transport endpoint is not connected").
    /// </summary>
    public static bool IsMountAccessible(string mountPath)
    {
        try
        {
            _ = new DirectoryInfo(mountPath).Attributes;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dead FUSE mounts surface as ENOTCONN ("Transport endpoint is not connected")
            // or, via Directory.CreateDirectory, as "The file ... already exists".
            return false;
        }
    }

    private async Task UnmountStaleAsync(CancellationToken cancellationToken)
    {
        var result = await FqCtl.UmountLazyAsync(_mountPath, _config.System, _logger, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = string.IsNullOrEmpty(result.Stderr) ? result.Code.ToString() : result.Stderr;
            _logger?.Warning(LoggerTypes.Disk,
                $"umount-lazy {_mountPath} failed ({result.Code}): {detail}");
        }

        try
        {
            if (File.Exists(_socketPath))
                File.Delete(_socketPath);
        }
        catch
        {
            // best-effort
        }

        // Give the kernel a moment to release the mount point before mounting again.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger?.Debug(LoggerTypes.Disk, $"fusequota shutdown uuid={_webSpaceUuid} cmd=do end");
            var response = await TalkToSocketAsync("do end", cancellationToken).ConfigureAwait(false);
            if (response.Contains("OK", StringComparison.Ordinal))
                _logger?.Info(LoggerTypes.Disk, $"fusequota stopped for {_webSpaceUuid}");
            else
                _logger?.Debug(LoggerTypes.Disk, $"fusequota shutdown response: {response.Trim()}");
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Disk, $"fusequota shutdown for {_webSpaceUuid}: {ex.Message}");
        }
    }

    public async Task DestroyAsync(CancellationToken cancellationToken = default)
    {
        await ShutdownAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Directory.Exists(_mountPath))
                Directory.Delete(_mountPath, recursive: true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Disk, $"Failed to remove mount {_mountPath}: {ex.Message}");
        }

        var vmountSite = Path.GetDirectoryName(_mountPath);
        try
        {
            if (vmountSite is not null && Directory.Exists(vmountSite)
                && !Directory.EnumerateFileSystemEntries(vmountSite).Any())
                Directory.Delete(vmountSite);
        }
        catch
        {
            // best-effort
        }

        try
        {
            if (File.Exists(_socketPath))
                File.Delete(_socketPath);
        }
        catch
        {
            // best-effort
        }
    }

    public async Task<ulong> DiskUsageAsync(CancellationToken cancellationToken = default)
    {
        var response = await TalkToSocketAsync("get quota_used", cancellationToken).ConfigureAwait(false);
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("quota_used", StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;

            if (ulong.TryParse(line[(eq + 1)..].Trim(), out var usage))
                return usage;
        }

        throw new IOException("fusequota socket failed to return usage data");
    }

    public async Task UpdateDiskLimitAsync(ulong limit, CancellationToken cancellationToken = default)
    {
        var response = await TalkToSocketAsync($"set quota = {limit}", cancellationToken).ConfigureAwait(false);
        if (!response.Contains("OK", StringComparison.Ordinal))
            throw new IOException($"fusequota rejected limit update: {response}");
    }

    public async Task<bool> IsSocketFunctionalAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_socketPath))
            return false;

        try
        {
            var response = await TalkToSocketAsync("get quota_used", cancellationToken).ConfigureAwait(false);
            return response.Contains("OK", StringComparison.Ordinal)
                   || response.Contains("quota_used", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task SpawnDaemonAsync(CancellationToken cancellationToken)
    {
        var bin = ResolveBinaryPath(_config.System);
        var uid = _ownerUid ?? _config.System.User.Uid;
        var gid = _ownerGid ?? _config.System.User.Gid;

        var psi = new ProcessStartInfo
        {
            FileName = bin,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--quota");
        psi.ArgumentList.Add(_diskLimitBytes.ToString());
        psi.ArgumentList.Add("--quota-rescan-interval");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--clone-fd");
        psi.ArgumentList.Add("--communication-socket-path");
        psi.ArgumentList.Add(_socketPath);
        psi.ArgumentList.Add("--uid");
        psi.ArgumentList.Add(uid.ToString());
        psi.ArgumentList.Add("--gid");
        psi.ArgumentList.Add(gid.ToString());
        psi.ArgumentList.Add("--nocache");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("allow_other");
        psi.ArgumentList.Add(_sourcePath);
        psi.ArgumentList.Add(_mountPath);

        _logger?.Info(LoggerTypes.Disk, $"Starting fusequota for {_webSpaceUuid} → {_mountPath}");
        _logger?.Debug(LoggerTypes.Disk,
            $"fusequota args: --quota {_diskLimitBytes} socket={_socketPath} uid={uid} gid={gid} {bin} {_sourcePath} {_mountPath}");

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start fusequota ({bin})");

        // Detach: do not wait; fusequota daemonizes / stays as FUSE process.
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stderr))
                    _logger?.Warning(LoggerTypes.Disk, $"fusequota {_webSpaceUuid}: {stderr.Trim()}");
            }
            catch
            {
                // ignored
            }
        }, cancellationToken);
    }

    private async Task WaitForSocketAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsSocketFunctionalAsync(cancellationToken).ConfigureAwait(false))
                return;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"fusequota socket not ready for {_webSpaceUuid} within {timeout.TotalSeconds}s");
    }

    private async Task<string> TalkToSocketAsync(string command, CancellationToken cancellationToken)
    {
        _logger?.Debug(LoggerTypes.Disk, $"fusequota socket → {_webSpaceUuid}: {command}");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(_socketPath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);

        var payload = Encoding.UTF8.GetBytes(command.TrimEnd() + "\n");
        await socket.SendAsync(payload, SocketFlags.None, cts.Token).ConfigureAwait(false);
        socket.Shutdown(SocketShutdown.Send);

        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
            if (read == 0)
                break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        var response = sb.ToString();
        _logger?.Debug(LoggerTypes.Disk, $"fusequota socket ← {_webSpaceUuid}: {response.Trim().Replace('\n', ' ')}");
        return response;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
