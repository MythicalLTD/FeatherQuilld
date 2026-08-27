namespace FeatherQuilld.Utils.WebSpaces.Backups;

public sealed record BackupObjectInfo(
    Guid Uuid,
    long Bytes,
    DateTimeOffset CreatedAt,
    string Checksum,
    string? RemoteRef = null);

/// <summary>Persists WebSpace backup archives (local disk or S3-compatible).</summary>
public interface IBackupObjectStore
{
    IReadOnlyList<BackupObjectInfo> List(Guid webspaceUuid);

    Task PutAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string localTarGzPath,
        string checksum,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default);

    Task DownloadToFileAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string destPath,
        CancellationToken cancellationToken = default);

    bool Exists(Guid webspaceUuid, Guid backupUuid);

    /// <summary>Rebuild local sidecar index from remote provider. Returns number of entries written. No-op stores return current list count.</summary>
    Task<int> ReconcileAsync(Guid webspaceUuid, CancellationToken cancellationToken = default);
}
