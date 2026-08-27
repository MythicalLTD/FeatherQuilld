using System.Text.Json;
using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Utils.WebSpaces.Backups;

/// <summary>Stores backups as <c>{BackupDirectory}/{uuid}/{backupUuid}.tar.gz</c> plus optional meta JSON.</summary>
public sealed class LocalBackupStore : IBackupObjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string _root;

    public LocalBackupStore(SystemConfig system)
    {
        _root = system.BackupDirectory;
        Directory.CreateDirectory(_root);
    }

    public LocalBackupStore(string backupDirectory)
    {
        _root = backupDirectory;
        Directory.CreateDirectory(_root);
    }

    public IReadOnlyList<BackupObjectInfo> List(Guid webspaceUuid)
    {
        var dir = Dir(webspaceUuid);
        if (!Directory.Exists(dir))
            return [];

        var results = new List<BackupObjectInfo>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.tar.gz"))
        {
            var idStr = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            if (!Guid.TryParse(idStr, out var id))
                continue;
            var info = new FileInfo(path);
            var checksum = ReadMetaChecksum(dir, id) ?? "";
            results.Add(new BackupObjectInfo(id, info.Length, info.CreationTimeUtc, checksum));
        }

        return results.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public Task PutAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string localTarGzPath,
        string checksum,
        CancellationToken cancellationToken = default)
    {
        var dir = Dir(webspaceUuid);
        Directory.CreateDirectory(dir);
        var dest = ArchivePath(webspaceUuid, backupUuid);
        File.Copy(localTarGzPath, dest, overwrite: true);
        WriteMeta(dir, backupUuid, checksum, new FileInfo(dest).Length);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default)
    {
        var path = ArchivePath(webspaceUuid, backupUuid);
        var meta = MetaPath(Dir(webspaceUuid), backupUuid);
        var ok = false;
        if (File.Exists(path))
        {
            File.Delete(path);
            ok = true;
        }

        if (File.Exists(meta))
            File.Delete(meta);
        return Task.FromResult(ok);
    }

    public Task<Stream?> OpenReadAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default)
    {
        var path = ArchivePath(webspaceUuid, backupUuid);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(File.OpenRead(path));
    }

    public Task DownloadToFileAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        var path = ArchivePath(webspaceUuid, backupUuid);
        if (!File.Exists(path))
            throw new FileNotFoundException("Backup not found.", path);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Copy(path, destPath, overwrite: true);
        return Task.CompletedTask;
    }

    public bool Exists(Guid webspaceUuid, Guid backupUuid) =>
        File.Exists(ArchivePath(webspaceUuid, backupUuid));

    public Task<int> ReconcileAsync(Guid webspaceUuid, CancellationToken cancellationToken = default) =>
        Task.FromResult(List(webspaceUuid).Count);

    private string Dir(Guid uuid) => Path.Combine(_root, uuid.ToString());

    private string ArchivePath(Guid uuid, Guid backupUuid) =>
        Path.Combine(Dir(uuid), $"{backupUuid}.tar.gz");

    private static string MetaPath(string dir, Guid backupUuid) =>
        Path.Combine(dir, $"{backupUuid}.json");

    private static void WriteMeta(string dir, Guid backupUuid, string checksum, long bytes)
    {
        var payload = new { checksum, bytes, created_at = DateTimeOffset.UtcNow };
        File.WriteAllText(MetaPath(dir, backupUuid), JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string? ReadMetaChecksum(string dir, Guid backupUuid)
    {
        var path = MetaPath(dir, backupUuid);
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("checksum", out var c) ? c.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
