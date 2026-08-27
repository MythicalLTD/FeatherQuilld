using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Utils.WebSpaces.Backups;

/// <summary>
/// Stores each backup as a restic snapshot tagged with webspace/backup UUIDs.
/// A local sidecar index under BackupDirectory tracks list/download metadata.
/// </summary>
public sealed class ResticBackupStore : IBackupObjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly BackupResticConfig _cfg;
    private readonly SystemConfig _system;
    private readonly string _indexRoot;
    private readonly Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<int>> _run;
    private readonly Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<(int Code, string Output)>>? _runWithOutput;

    public ResticBackupStore(SystemConfig system)
        : this(system, RunProcessAsync, RunProcessCaptureAsync)
    {
    }

    internal ResticBackupStore(
        SystemConfig system,
        Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<int>> runner,
        Func<string, IReadOnlyList<string>, IDictionary<string, string>?, CancellationToken, Task<(int Code, string Output)>>? runnerWithOutput)
    {
        _system = system;
        _cfg = system.Backups.Restic ?? new BackupResticConfig();
        _indexRoot = system.BackupDirectory;
        Directory.CreateDirectory(_indexRoot);
        Directory.CreateDirectory(BackupTempPaths.Root(system));
        _run = runner;
        _runWithOutput = runnerWithOutput;
        if (string.IsNullOrWhiteSpace(_cfg.Repository))
            throw new InvalidOperationException("system.backups.restic.repository is required for restic provider.");
    }

    public IReadOnlyList<BackupObjectInfo> List(Guid webspaceUuid)
    {
        var index = ReadIndex(webspaceUuid);
        return index.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task PutAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string localTarGzPath,
        string checksum,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "backup",
            localTarGzPath,
            "--tag", $"webspace={webspaceUuid:D}",
            "--tag", $"backup={backupUuid:D}",
            "--host", "featherquilld",
        };
        await EnsureRepoAsync(cancellationToken);
        var code = await _run(Binary(), args, Env(), cancellationToken);
        if (code != 0)
            throw new InvalidOperationException($"restic backup failed with exit code {code}.");

        var snapshotId = await ResolveSnapshotIdAsync(backupUuid, cancellationToken);
        var bytes = new FileInfo(localTarGzPath).Length;
        var list = ReadIndex(webspaceUuid).Where(x => x.Uuid != backupUuid).ToList();
        list.Add(new BackupObjectInfo(backupUuid, bytes, DateTimeOffset.UtcNow, checksum, snapshotId));
        WriteIndex(webspaceUuid, list);
    }

    public async Task<bool> DeleteAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default)
    {
        var list = ReadIndex(webspaceUuid).ToList();
        if (list.All(x => x.Uuid != backupUuid))
            return false;

        var args = new List<string>
        {
            "forget",
            "--tag", $"backup={backupUuid:D}",
            "--prune",
        };
        var code = await _run(Binary(), args, Env(), cancellationToken);
        if (code != 0)
            throw new InvalidOperationException($"restic forget failed with exit code {code}.");

        WriteIndex(webspaceUuid, list.Where(x => x.Uuid != backupUuid).ToList());
        return true;
    }

    public async Task<Stream?> OpenReadAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default)
    {
        if (!Exists(webspaceUuid, backupUuid))
            return null;
        var tmp = BackupTempPaths.File(_system, "fq-restic", backupUuid);
        await DownloadToFileAsync(webspaceUuid, backupUuid, tmp, cancellationToken);
        return new TempFileStream(tmp);
    }

    public async Task DownloadToFileAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        var entry = ReadIndex(webspaceUuid).FirstOrDefault(x => x.Uuid == backupUuid);
        if (entry is null)
            throw new FileNotFoundException("Backup not found in restic index.", backupUuid.ToString());

        var targetDir = BackupTempPaths.Directory(_system, "fq-restic-restore", backupUuid);
        Directory.CreateDirectory(targetDir);
        try
        {
            var args = new List<string> { "restore" };
            if (!string.IsNullOrWhiteSpace(entry.RemoteRef))
                args.Add(entry.RemoteRef);
            else
            {
                args.Add("latest");
                args.Add("--tag");
                args.Add($"backup={backupUuid:D}");
            }

            args.Add("--target");
            args.Add(targetDir);

            var code = await _run(Binary(), args, Env(), cancellationToken);
            if (code != 0)
                throw new InvalidOperationException($"restic restore failed with exit code {code}.");

            var found = Directory.EnumerateFiles(targetDir, "*.tar.gz", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (found is null)
                throw new FileNotFoundException("restic restore produced no files.");

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
        if (_runWithOutput is null)
            return List(webspaceUuid).Count;

        var args = new List<string>
        {
            "snapshots",
            "--json",
            "--tag",
            $"webspace={webspaceUuid:D}",
        };
        var (code, output) = await _runWithOutput(Binary(), args, Env(), cancellationToken);
        if (code != 0 || string.IsNullOrWhiteSpace(output))
            return List(webspaceUuid).Count;

        var rebuilt = new List<BackupObjectInfo>();
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return List(webspaceUuid).Count;

            foreach (var snap in doc.RootElement.EnumerateArray())
            {
                if (!TryParseResticSnapshot(snap, out var backupUuid, out var remoteId, out var createdAt, out var bytes, out var checksum))
                    continue;
                rebuilt.Add(new BackupObjectInfo(backupUuid, bytes, createdAt, checksum, remoteId));
            }
        }
        catch
        {
            return List(webspaceUuid).Count;
        }

        // Prefer latest snapshot per backup UUID if duplicates appear.
        var deduped = rebuilt
            .GroupBy(x => x.Uuid)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .ToList();
        WriteIndex(webspaceUuid, deduped);
        return deduped.Count;
    }

    private static bool TryParseResticSnapshot(
        JsonElement snap,
        out Guid backupUuid,
        out string? remoteId,
        out DateTimeOffset createdAt,
        out long bytes,
        out string checksum)
    {
        backupUuid = Guid.Empty;
        remoteId = null;
        createdAt = DateTimeOffset.UtcNow;
        bytes = 0;
        checksum = "";

        if (snap.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var s = tag.GetString();
                if (s is null || !s.StartsWith("backup=", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = s["backup=".Length..];
                if (Guid.TryParse(value, out var parsed))
                {
                    backupUuid = parsed;
                    break;
                }
            }
        }

        if (backupUuid == Guid.Empty)
            return false;

        if (snap.TryGetProperty("id", out var id))
            remoteId = id.GetString();
        else if (snap.TryGetProperty("short_id", out var shortId))
            remoteId = shortId.GetString();

        if (snap.TryGetProperty("time", out var timeEl))
        {
            var timeStr = timeEl.GetString();
            if (!string.IsNullOrWhiteSpace(timeStr) && DateTimeOffset.TryParse(timeStr, out var parsedTime))
                createdAt = parsedTime.ToUniversalTime();
        }

        if (snap.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            if (summary.TryGetProperty("total_bytes_processed", out var totalBytes) && totalBytes.TryGetInt64(out var tb))
                bytes = tb;
            else if (summary.TryGetProperty("data_added", out var dataAdded) && dataAdded.TryGetInt64(out var da))
                bytes = da;
        }
        else if (snap.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var size))
        {
            bytes = size;
        }

        if (snap.TryGetProperty("checksum", out var checksumEl))
            checksum = checksumEl.GetString() ?? "";

        return true;
    }

    private async Task<string?> ResolveSnapshotIdAsync(Guid backupUuid, CancellationToken ct)
    {
        if (_runWithOutput is null)
            return null;

        var args = new List<string> { "snapshots", "--json", "--tag", $"backup={backupUuid:D}" };
        var (code, output) = await _runWithOutput(Binary(), args, Env(), ct);
        if (code != 0 || string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var first = doc.RootElement[0];
            if (first.TryGetProperty("id", out var id))
                return id.GetString();
            if (first.TryGetProperty("short_id", out var shortId))
                return shortId.GetString();
        }
        catch
        {
            return null;
        }

        return null;
    }

    private async Task EnsureRepoAsync(CancellationToken ct)
    {
        var code = await _run(Binary(), ["snapshots", "--json"], Env(), ct);
        if (code == 0)
            return;
        code = await _run(Binary(), ["init"], Env(), ct);
        if (code != 0)
            throw new InvalidOperationException($"restic init failed with exit code {code}.");
    }

    private string Binary() =>
        string.IsNullOrWhiteSpace(_cfg.Binary) ? "restic" : _cfg.Binary.Trim();

    private Dictionary<string, string> Env()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RESTIC_REPOSITORY"] = _cfg.Repository,
            ["RESTIC_PASSWORD"] = _cfg.Password ?? "",
        };
        return env;
    }

    private string IndexPath(Guid webspaceUuid) =>
        Path.Combine(_indexRoot, webspaceUuid.ToString("D"), "restic-index.json");

    private List<BackupObjectInfo> ReadIndex(Guid webspaceUuid)
    {
        var path = IndexPath(webspaceUuid);
        if (!File.Exists(path))
            return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<BackupObjectInfo>>(json, JsonOptions) ?? [];
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
        var (code, _) = await RunProcessCaptureAsync(fileName, args, env, cancellationToken);
        return code;
    }

    internal static async Task<(int Code, string Output)> RunProcessCaptureAsync(
        string fileName,
        IReadOnlyList<string> args,
        IDictionary<string, string>? env,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (env is not null)
        {
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
        await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        return (proc.ExitCode, stdout);
    }
}
