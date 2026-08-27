using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Utils.WebSpaces.Backups;

/// <summary>
/// Stores each backup via <c>proxmox-backup-client backup</c> and tracks metadata in a local index.
/// </summary>
public sealed class PbsBackupStore : IBackupObjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly BackupPbsConfig _cfg;
    private readonly SystemConfig _system;
    private readonly string _indexRoot;
    private readonly Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<int>> _run;
    private readonly Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<(int Code, string Output)>>? _runWithOutput;

    public PbsBackupStore(SystemConfig system)
        : this(system, RunProcessAsync, ResticBackupStore.RunProcessCaptureAsync)
    {
    }

    internal PbsBackupStore(
        SystemConfig system,
        Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<int>> runner,
        Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<(int Code, string Output)>>? runnerWithOutput = null)
    {
        _system = system;
        _cfg = system.Backups.Pbs ?? new BackupPbsConfig();
        _indexRoot = system.BackupDirectory;
        Directory.CreateDirectory(_indexRoot);
        Directory.CreateDirectory(BackupTempPaths.Root(system));
        _run = runner;
        _runWithOutput = runnerWithOutput;
        if (string.IsNullOrWhiteSpace(_cfg.Repository))
            throw new InvalidOperationException("system.backups.pbs.repository is required for pbs provider.");
    }

    public IReadOnlyList<BackupObjectInfo> List(Guid webspaceUuid) =>
        ReadIndex(webspaceUuid).OrderByDescending(x => x.CreatedAt).ToList();

    public async Task PutAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string localTarGzPath,
        string checksum,
        CancellationToken cancellationToken = default)
    {
        var archiveName = $"{backupUuid:D}.tar.gz";
        var args = new List<string>
        {
            "backup",
            $"{archiveName}:{localTarGzPath}",
            "--backup-type", "host",
            "--backup-id", webspaceUuid.ToString("D"),
            "--repository", _cfg.Repository,
        };
        if (!string.IsNullOrWhiteSpace(_cfg.Fingerprint))
        {
            args.Add("--fingerprint");
            args.Add(_cfg.Fingerprint);
        }

        var createdAt = DateTimeOffset.UtcNow;
        args.Add("--backup-time");
        args.Add(createdAt.ToUnixTimeSeconds().ToString());

        var code = await _run(Binary(), args, Env(), cancellationToken);
        if (code != 0)
            throw new InvalidOperationException($"proxmox-backup-client backup failed with exit code {code}.");

        var snapshotRef = SnapshotRef(webspaceUuid, createdAt);
        var bytes = new FileInfo(localTarGzPath).Length;
        var list = ReadIndex(webspaceUuid).Where(x => x.Uuid != backupUuid).ToList();
        list.Add(new BackupObjectInfo(backupUuid, bytes, createdAt, checksum, snapshotRef));
        WriteIndex(webspaceUuid, list);
    }

    public async Task<bool> DeleteAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default)
    {
        var list = ReadIndex(webspaceUuid).ToList();
        var entry = list.FirstOrDefault(x => x.Uuid == backupUuid);
        if (entry is null)
            return false;

        var snapshot = entry.RemoteRef ?? SnapshotRef(webspaceUuid, entry.CreatedAt);
        var args = new List<string>
        {
            "snapshot",
            "forget",
            snapshot,
            "--repository", _cfg.Repository,
        };
        if (!string.IsNullOrWhiteSpace(_cfg.Fingerprint))
        {
            args.Add("--fingerprint");
            args.Add(_cfg.Fingerprint);
        }

        var code = await _run(Binary(), args, Env(), cancellationToken);
        if (code != 0)
            throw new InvalidOperationException($"proxmox-backup-client snapshot forget failed with exit code {code}.");

        WriteIndex(webspaceUuid, list.Where(x => x.Uuid != backupUuid).ToList());
        return true;
    }

    public async Task<Stream?> OpenReadAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default)
    {
        if (!Exists(webspaceUuid, backupUuid))
            return null;
        var tmp = BackupTempPaths.File(_system, "fq-pbs", backupUuid);
        await DownloadToFileAsync(webspaceUuid, backupUuid, tmp, cancellationToken);
        return new TempFileStream(tmp);
    }

    public async Task DownloadToFileAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        var entry = ReadIndex(webspaceUuid).FirstOrDefault(x => x.Uuid == backupUuid)
            ?? throw new FileNotFoundException("Backup not found in PBS index.", backupUuid.ToString());

        var targetDir = BackupTempPaths.Directory(_system, "fq-pbs-restore", backupUuid);
        Directory.CreateDirectory(targetDir);
        try
        {
            var archiveName = $"{backupUuid:D}.tar.gz";
            var snapshot = entry.RemoteRef ?? SnapshotRef(webspaceUuid, entry.CreatedAt);
            var args = new List<string>
            {
                "restore",
                $"{snapshot}/{archiveName}",
                targetDir,
                "--repository", _cfg.Repository,
            };
            if (!string.IsNullOrWhiteSpace(_cfg.Fingerprint))
            {
                args.Add("--fingerprint");
                args.Add(_cfg.Fingerprint);
            }

            var code = await _run(Binary(), args, Env(), cancellationToken);
            if (code != 0)
                throw new InvalidOperationException($"proxmox-backup-client restore failed with exit code {code}.");

            var found = Directory.EnumerateFiles(targetDir, "*.tar.gz", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (found is null)
                throw new FileNotFoundException("PBS restore produced no files.");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(found, destPath, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(targetDir, recursive: true); } catch { /* ignore */ }
        }
    }

    public bool Exists(Guid webspaceUuid, Guid backupUuid) =>
        ReadIndex(webspaceUuid).Any(x => x.Uuid == backupUuid);

    public async Task<int> ReconcileAsync(Guid webspaceUuid, CancellationToken cancellationToken = default)
    {
        var existing = List(webspaceUuid);
        if (_runWithOutput is null)
            return existing.Count;

        var args = new List<string>
        {
            "snapshot",
            "list",
            $"host/{webspaceUuid:D}",
            "--repository",
            _cfg.Repository,
            "--output-format",
            "json",
        };
        if (!string.IsNullOrWhiteSpace(_cfg.Fingerprint))
        {
            args.Add("--fingerprint");
            args.Add(_cfg.Fingerprint);
        }

        int code;
        string output;
        try
        {
            (code, output) = await _runWithOutput(Binary(), args, Env(), cancellationToken);
        }
        catch
        {
            return existing.Count;
        }

        if (code != 0 || string.IsNullOrWhiteSpace(output))
            return existing.Count;

        var rebuilt = new List<BackupObjectInfo>();
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return existing.Count;

            foreach (var snap in doc.RootElement.EnumerateArray())
            {
                if (!TryParsePbsSnapshot(webspaceUuid, snap, out var entry))
                    continue;
                rebuilt.Add(entry);
            }
        }
        catch
        {
            return existing.Count;
        }

        var deduped = rebuilt
            .GroupBy(x => x.Uuid)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .ToList();
        WriteIndex(webspaceUuid, deduped);
        return deduped.Count;
    }

    private static bool TryParsePbsSnapshot(Guid webspaceUuid, JsonElement snap, out BackupObjectInfo entry)
    {
        entry = default!;
        var createdAt = DateTimeOffset.UtcNow;
        if (snap.TryGetProperty("backup-time", out var bt))
        {
            if (bt.ValueKind == JsonValueKind.Number && bt.TryGetInt64(out var unix))
                createdAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            else if (bt.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(bt.GetString(), out var parsed))
                createdAt = parsed.ToUniversalTime();
        }
        else if (snap.TryGetProperty("backup_time", out var btSnake))
        {
            if (btSnake.ValueKind == JsonValueKind.Number && btSnake.TryGetInt64(out var unix))
                createdAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            else if (btSnake.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(btSnake.GetString(), out var parsed))
                createdAt = parsed.ToUniversalTime();
        }

        var snapshotRef = SnapshotRef(webspaceUuid, createdAt);
        if (snap.TryGetProperty("snapshot", out var snapRefEl))
        {
            var s = snapRefEl.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                snapshotRef = s!;
        }

        Guid backupUuid = Guid.Empty;
        long bytes = 0;
        var checksum = "";

        if (snap.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                var name = file.TryGetProperty("filename", out var fn) ? fn.GetString()
                    : file.TryGetProperty("file-name", out var fn2) ? fn2.GetString()
                    : file.ValueKind == JsonValueKind.String ? file.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    continue;
                var idStr = name[..^".tar.gz".Length];
                if (!Guid.TryParse(idStr, out backupUuid))
                    continue;
                if (file.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var sz))
                    bytes = sz;
                if (file.TryGetProperty("checksum", out var cs))
                    checksum = cs.GetString() ?? "";
                break;
            }
        }

        if (backupUuid == Guid.Empty)
        {
            foreach (var propName in new[] { "filename", "file-name", "archive", "name" })
            {
                if (!snap.TryGetProperty(propName, out var prop))
                    continue;
                var name = prop.GetString();
                if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    continue;
                var idStr = name[..^".tar.gz".Length];
                if (Guid.TryParse(idStr, out backupUuid))
                    break;
            }
        }

        if (backupUuid == Guid.Empty)
            return false;

        entry = new BackupObjectInfo(backupUuid, bytes, createdAt, checksum, snapshotRef);
        return true;
    }

    internal static string SnapshotRef(Guid webspaceUuid, DateTimeOffset createdAt) =>
        $"host/{webspaceUuid:D}/{FormatSnapshotTime(createdAt)}";

    internal static string FormatSnapshotTime(DateTimeOffset createdAt) =>
        createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private string Binary() =>
        string.IsNullOrWhiteSpace(_cfg.Binary) ? "proxmox-backup-client" : _cfg.Binary.Trim();

    private Dictionary<string, string> Env()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(_cfg.Password))
            env["PBS_PASSWORD"] = _cfg.Password;
        if (!string.IsNullOrWhiteSpace(_cfg.Fingerprint))
            env["PBS_FINGERPRINT"] = _cfg.Fingerprint;
        return env;
    }

    private string IndexPath(Guid webspaceUuid) =>
        Path.Combine(_indexRoot, webspaceUuid.ToString("D"), "pbs-index.json");

    private List<BackupObjectInfo> ReadIndex(Guid webspaceUuid)
    {
        var path = IndexPath(webspaceUuid);
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<BackupObjectInfo>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteIndex(Guid webspaceUuid, IReadOnlyList<BackupObjectInfo> items)
    {
        var path = IndexPath(webspaceUuid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(items, JsonOptions), Encoding.UTF8);
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        IDictionary<string, string>? env,
        CancellationToken cancellationToken)
    {
        var (code, _) = await ResticBackupStore.RunProcessCaptureAsync(fileName, args, env, cancellationToken);
        return code;
    }
}
