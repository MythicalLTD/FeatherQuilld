using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using FeatherQuilld.Plugins.Events;
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
    private readonly IEventBus _events;

    public WebSpaceBackupService(
        AppConfig config,
        WebSpaceStore spaces,
        IBackupObjectStore store,
        AppLogger? logger = null,
        BackupJobProgressService? jobs = null,
        IEventBus? events = null)
    {
        _config = config;
        _spaces = spaces;
        _store = store;
        _logger = logger;
        _jobs = jobs;
        _events = events.OrNoOp();
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

    public BackupJobState StartRestoreAsync(Guid uuid, Guid backupUuid, IReadOnlyList<string>? paths = null)
    {
        var jobs = _jobs ?? throw new InvalidOperationException("Backup job service not configured.");
        var selected = NormalizeRestorePaths(paths);
        var job = jobs.Start(uuid, selected.Count > 0 ? "restore_selected" : "restore");
        _ = Task.Run(() =>
        {
            try
            {
                Restore(uuid, backupUuid, selected);
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

    private object CreateInternal(Guid uuid, bool stopDuringBackup = false) =>
        _events.WithHooks(
            new BackupCreateBeforeEvent { WebSpaceUuid = uuid, StopDuringBackup = stopDuringBackup },
            (result, err) => new BackupCreateAfterEvent
            {
                WebSpaceUuid = uuid,
                Result = result,
                Error = err,
            },
            () => CreateInternalCore(uuid, stopDuringBackup));

    private object CreateInternalCore(Guid uuid, bool stopDuringBackup)
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

    public bool Delete(Guid uuid, Guid backupUuid) =>
        _events.WithHooks(
            new BackupDeleteBeforeEvent { WebSpaceUuid = uuid, BackupUuid = backupUuid },
            (deleted, err) => new BackupDeleteAfterEvent
            {
                WebSpaceUuid = uuid,
                BackupUuid = backupUuid,
                Deleted = deleted,
                Error = err,
            },
            () => DeleteCore(uuid, backupUuid));

    private bool DeleteCore(Guid uuid, Guid backupUuid)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        return _store.DeleteAsync(uuid, backupUuid).GetAwaiter().GetResult();
    
    }

    public Task<int> ReconcileAsync(Guid uuid, CancellationToken ct = default) =>
        _events.WithHooksAsync(
            new BackupReconcileBeforeEvent { WebSpaceUuid = uuid },
            (count, err) => new BackupReconcileAfterEvent
            {
                WebSpaceUuid = uuid,
                Count = count,
                Error = err,
            },
            token => ReconcileCoreAsync(uuid, token),
            ct);

    private async Task<int> ReconcileCoreAsync(Guid uuid, CancellationToken ct)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        return await _store.ReconcileAsync(uuid, ct).ConfigureAwait(false);
    
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

    public void Restore(Guid uuid, Guid backupUuid, IReadOnlyList<string>? paths = null) =>
        _events.WithHooks(
            new BackupRestoreBeforeEvent { WebSpaceUuid = uuid, BackupUuid = backupUuid },
            err => new BackupRestoreAfterEvent
            {
                WebSpaceUuid = uuid,
                BackupUuid = backupUuid,
                Error = err,
            },
            () => RestoreCore(uuid, backupUuid, paths));

    /// <summary>Immediate children of <paramref name="directory"/> inside a backup archive.</summary>
    public object ListBackupFiles(Guid uuid, Guid backupUuid, string directory = "/")
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!_store.Exists(uuid, backupUuid))
            throw new InvalidOperationException("Backup not found.");

        var prefix = NormalizeEntryName(directory);
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";

        var tmp = Path.Combine(_config.System.TmpDirectory, $"browse-{backupUuid}.tar.gz");
        try
        {
            _store.DownloadToFileAsync(uuid, backupUuid, tmp).GetAwaiter().GetResult();
            var children = new Dictionary<string, (bool Dir, long Size)>(StringComparer.Ordinal);
            using (var file = File.OpenRead(tmp))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new TarReader(gzip))
            {
                while (reader.GetNextEntry() is { } entry)
                {
                    var name = NormalizeEntryName(entry.Name);
                    if (name.Length == 0)
                        continue;
                    string rest;
                    if (prefix.Length == 0)
                        rest = name;
                    else if (name.StartsWith(prefix, StringComparison.Ordinal))
                        rest = name[prefix.Length..];
                    else
                        continue;
                    if (rest.Length == 0)
                        continue;
                    var slash = rest.IndexOf('/');
                    var child = slash < 0 ? rest : rest[..slash];
                    var isDir = slash >= 0 || entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList;
                    var size = slash < 0 && !isDir ? entry.Length : 0;
                    if (children.TryGetValue(child, out var existing))
                    {
                        children[child] = (existing.Dir || isDir, existing.Size + size);
                    }
                    else
                    {
                        children[child] = (isDir, size);
                    }
                }
            }

            var listing = children
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (object)new
                {
                    name = kv.Key,
                    directory = kv.Value.Dir,
                    file = !kv.Value.Dir,
                    size = kv.Value.Size,
                })
                .ToList();

            return new
            {
                directory = "/" + prefix.TrimEnd('/'),
                files = listing,
            };
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private void RestoreCore(Guid uuid, Guid backupUuid, IReadOnlyList<string>? paths)
    {
        var space = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!_store.Exists(uuid, backupUuid))
            throw new InvalidOperationException("Backup not found.");

        var selected = NormalizeRestorePaths(paths);
        var selective = selected.Count > 0;
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

            if (wasRunning && !selective)
                _spaces.Power(uuid, "stop");

            var fsPath = _spaces.EffectiveFsPath(uuid);
            if (!selective)
            {
                WipeContents(fsPath);
                ExtractTarGz(tmp, fsPath);
            }
            else
            {
                ExtractTarGzSelected(tmp, fsPath, selected);
            }

            if (wasRunning && !selective)
                _spaces.Power(uuid, "start");

            _logger?.Info(LoggerTypes.WebSpaces,
                selective
                    ? $"Restored {selected.Count} path(s) from backup {backupUuid} into {uuid}"
                    : $"Restored backup {backupUuid} into {uuid}");
        }
        catch
        {
            if (wasRunning && !selective)
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

    public object Import(Guid uuid, Stream archiveStream) =>
        _events.WithHooks(
            new BackupImportBeforeEvent { WebSpaceUuid = uuid },
            (result, err) => new BackupImportAfterEvent
            {
                WebSpaceUuid = uuid,
                Result = result,
                Error = err,
            },
            () => ImportCore(uuid, archiveStream));

    private object ImportCore(Guid uuid, Stream archiveStream)
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

    internal static void ExtractTarGzSelected(string archivePath, string destDir, IReadOnlyList<string> paths)
    {
        Directory.CreateDirectory(destDir);
        var destFull = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = NormalizeEntryName(entry.Name);
            if (name.Length == 0 || !PathMatches(name, paths))
                continue;
            if (name is "webspace.json" or "site.json")
                continue;

            var target = Path.GetFullPath(Path.Combine(destDir, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destFull, StringComparison.Ordinal) &&
                !string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), destFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Archive entry escapes WebSpace root.");
            }

            if (entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    internal static bool PathMatches(string entryName, IReadOnlyList<string> selected)
    {
        var name = NormalizeEntryName(entryName);
        foreach (var raw in selected)
        {
            var sel = NormalizeEntryName(raw);
            if (sel.Length == 0)
                continue;
            if (string.Equals(name, sel, StringComparison.Ordinal))
                return true;
            if (name.StartsWith(sel + "/", StringComparison.Ordinal))
                return true;
            if (sel.EndsWith('/') && name.StartsWith(sel, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static List<string> NormalizeRestorePaths(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var raw in paths)
        {
            var name = NormalizeEntryName(raw);
            if (name.Length == 0 || !seen.Add(name))
                continue;
            list.Add(name);
        }

        return list;
    }

    internal static string NormalizeEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var n = name.Replace('\\', '/').Trim();
        while (n.StartsWith("./", StringComparison.Ordinal))
            n = n[2..];
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is ".." or "."))
            return "";
        return string.Join('/', parts);
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
