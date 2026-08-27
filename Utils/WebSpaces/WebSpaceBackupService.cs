using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.WebSpaces.Backups;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>WebSpace tar.gz backups via <see cref="IBackupObjectStore"/> (local or S3).</summary>
public sealed class WebSpaceBackupService
{
    private readonly AppConfig _config;
    private readonly WebSpaceStore _spaces;
    private readonly IBackupObjectStore _store;
    private readonly BackupJobProgressService? _jobs;
    private readonly AppLogger? _logger;

    public WebSpaceBackupService(
        AppConfig config,
        WebSpaceStore spaces,
        IBackupObjectStore store,
        AppLogger? logger = null,
        BackupJobProgressService? jobs = null)
    {
        _config = config;
        _spaces = spaces;
        _store = store;
        _logger = logger;
        _jobs = jobs;
        Directory.CreateDirectory(_config.System.BackupDirectory);
        Directory.CreateDirectory(_config.System.TmpDirectory);
    }

    public IReadOnlyList<object> List(Guid uuid)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        return _store.List(uuid)
            .Select(x => (object)new
            {
                uuid = x.Uuid,
                bytes = x.Bytes,
                checksum = x.Checksum,
                created_at = x.CreatedAt,
            })
            .ToList();
    }

    public object Create(Guid uuid, bool stopDuringBackup = false) =>
        CreateInternal(uuid, stopDuringBackup);

    public BackupJobState StartCreateAsync(Guid uuid, bool stopDuringBackup = false)
    {
        var jobs = _jobs ?? throw new InvalidOperationException("Backup job service not configured.");
        var job = jobs.Start(uuid, "create");
        _ = Task.Run(() =>
        {
            try
            {
                var result = CreateInternal(uuid, stopDuringBackup);
                var backupUuid = result.GetType().GetProperty("uuid")?.GetValue(result) is Guid g ? g : Guid.Empty;
                var bytes = result.GetType().GetProperty("bytes")?.GetValue(result) as long?;
                var checksum = result.GetType().GetProperty("checksum")?.GetValue(result) as string;
                jobs.MarkCompleted(job.JobId, backupUuid == Guid.Empty ? null : backupUuid, bytes, checksum);
            }
            catch (Exception ex)
            {
                jobs.MarkFailed(job.JobId, ex.Message);
            }
        });
        return job;
    }

    public BackupJobState StartRestoreAsync(Guid uuid, Guid backupUuid)
    {
        var jobs = _jobs ?? throw new InvalidOperationException("Backup job service not configured.");
        var job = jobs.Start(uuid, "restore");
        _ = Task.Run(() =>
        {
            try
            {
                Restore(uuid, backupUuid);
                jobs.MarkCompleted(job.JobId, backupUuid);
            }
            catch (Exception ex)
            {
                jobs.MarkFailed(job.JobId, ex.Message);
            }
        });
        return job;
    }

    public BackupJobState? GetJob(Guid jobId) => _jobs?.Get(jobId);

    private object CreateInternal(Guid uuid, bool stopDuringBackup = false)
    {
        var space = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var backupUuid = Guid.NewGuid();
        var fsPath = _spaces.EffectiveFsPath(uuid);
        var tmp = Path.Combine(_config.System.TmpDirectory, $"backup-{backupUuid}.tar.gz");

        var wasRunning = space.State == WebSpaceState.Running && WebSpaceRuntimeNeeds(space);
        if (stopDuringBackup && wasRunning)
            _spaces.Power(uuid, "stop");

        try
        {
            try
            {
                CreateTarGz(fsPath, tmp);
                var checksum = ComputeSha256(tmp);
                _store.PutAsync(uuid, backupUuid, tmp, checksum).GetAwaiter().GetResult();
                var info = new FileInfo(tmp);
                _logger?.Info(LoggerTypes.WebSpaces, $"Backup {backupUuid} for {uuid} ({info.Length} bytes)");
                return new
                {
                    uuid = backupUuid,
                    bytes = info.Length,
                    checksum,
                    created_at = DateTimeOffset.UtcNow,
                };
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            }
        }
        finally
        {
            if (stopDuringBackup && wasRunning)
            {
                try { _spaces.Power(uuid, "start"); }
                catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"backup restart: {ex.Message}"); }
            }
        }
    }

    public bool Delete(Guid uuid, Guid backupUuid)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        return _store.DeleteAsync(uuid, backupUuid).GetAwaiter().GetResult();
    }

    public Task<int> ReconcileAsync(Guid uuid, CancellationToken ct = default)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        return _store.ReconcileAsync(uuid, ct);
    }

    /// <summary>Open a readable stream for download (caller disposes).</summary>
    public Stream? OpenDownload(Guid uuid, Guid backupUuid)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        return _store.OpenReadAsync(uuid, backupUuid).GetAwaiter().GetResult();
    }

    [Obsolete("Use OpenDownload for S3-compatible stores.")]
    public string? ResolveDownloadPath(Guid uuid, Guid backupUuid)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (_store is LocalBackupStore local)
        {
            var path = Path.Combine(_config.System.BackupDirectory, uuid.ToString(), $"{backupUuid}.tar.gz");
            return local.Exists(uuid, backupUuid) ? path : null;
        }

        return null;
    }

    public void Restore(Guid uuid, Guid backupUuid)
    {
        var space = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!_store.Exists(uuid, backupUuid))
            throw new InvalidOperationException("Backup not found.");

        var entry = _store.List(uuid).FirstOrDefault(x => x.Uuid == backupUuid);
        var tmp = Path.Combine(_config.System.TmpDirectory, $"restore-{backupUuid}.tar.gz");
        var wasRunning = space.State == WebSpaceState.Running && WebSpaceRuntimeNeeds(space);

        try
        {
            _store.DownloadToFileAsync(uuid, backupUuid, tmp).GetAwaiter().GetResult();

            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Checksum))
            {
                var actual = ComputeSha256(tmp);
                if (!string.Equals(actual, entry.Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Backup checksum mismatch; restore aborted.");
            }

            if (wasRunning)
                _spaces.Power(uuid, "stop");

            var fsPath = _spaces.EffectiveFsPath(uuid);
            WipeContents(fsPath);
            ExtractTarGz(tmp, fsPath);

            if (wasRunning)
                _spaces.Power(uuid, "start");

            _logger?.Info(LoggerTypes.WebSpaces, $"Restored backup {backupUuid} into {uuid}");
        }
        catch
        {
            if (wasRunning)
            {
                try { _spaces.Power(uuid, "start"); }
                catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"restore restart: {ex.Message}"); }
            }

            throw;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public object Import(Guid uuid, Stream archiveStream)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var backupUuid = Guid.NewGuid();
        var tmp = Path.Combine(_config.System.TmpDirectory, $"import-{backupUuid}.tar.gz");

        try
        {
            Directory.CreateDirectory(_config.System.TmpDirectory);
            using (var file = File.Create(tmp))
            {
                archiveStream.CopyTo(file);
            }

            var checksum = ComputeSha256(tmp);
            _store.PutAsync(uuid, backupUuid, tmp, checksum).GetAwaiter().GetResult();
            var info = new FileInfo(tmp);
            _logger?.Info(LoggerTypes.WebSpaces, $"Imported backup {backupUuid} for {uuid} ({info.Length} bytes)");
            return new
            {
                uuid = backupUuid,
                bytes = info.Length,
                checksum,
                created_at = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static bool WebSpaceRuntimeNeeds(WebSpace space) =>
        Docker.WebSpaceRuntime.NeedsContainer(space.Runtime);

    private static void CreateTarGz(string sourceDir, string archivePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using var file = File.Create(archivePath);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        TarFile.CreateFromDirectory(sourceDir, gzip, includeBaseDirectory: false);
    }

    private static void ExtractTarGz(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destDir, overwriteFiles: true);
    }

    private static void WipeContents(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (name is "webspace.json" or "site.json")
                continue;
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, true);
                else
                    File.Delete(entry);
            }
            catch { /* best-effort */ }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
