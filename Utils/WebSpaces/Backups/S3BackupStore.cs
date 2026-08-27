using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Utils.WebSpaces.Backups;

/// <summary>S3-compatible object store with a local sidecar index under BackupDirectory.</summary>
public sealed class S3BackupStore : IBackupObjectStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly IAmazonS3 _s3;
    private readonly BackupS3Config _cfg;
    private readonly string _indexRoot;
    private readonly bool _ownsClient;

    public S3BackupStore(SystemConfig system, IAmazonS3? client = null)
    {
        _cfg = system.Backups.S3 ?? new BackupS3Config();
        _indexRoot = system.BackupDirectory;
        Directory.CreateDirectory(_indexRoot);
        if (client is not null)
        {
            _s3 = client;
            _ownsClient = false;
        }
        else
        {
            _s3 = CreateClient(_cfg);
            _ownsClient = true;
        }
    }

    public static IAmazonS3 CreateClient(BackupS3Config cfg)
    {
        var credentials = new BasicAWSCredentials(cfg.AccessKey, cfg.SecretKey);
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(
                string.IsNullOrWhiteSpace(cfg.Region) ? "us-east-1" : cfg.Region),
            ForcePathStyle = cfg.ForcePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(cfg.Endpoint))
            config.ServiceURL = cfg.Endpoint.Trim();
        return new AmazonS3Client(credentials, config);
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
        var key = ObjectKey(webspaceUuid, backupUuid);
        var info = new FileInfo(localTarGzPath);
        var put = new PutObjectRequest
        {
            BucketName = _cfg.Bucket,
            Key = key,
            FilePath = localTarGzPath,
            ContentType = "application/gzip",
        };
        put.Metadata["checksum"] = checksum;
        put.Metadata["webspace"] = webspaceUuid.ToString();
        await _s3.PutObjectAsync(put, cancellationToken);

        var entry = new BackupObjectInfo(backupUuid, info.Length, DateTimeOffset.UtcNow, checksum);
        var list = ReadIndex(webspaceUuid).Where(x => x.Uuid != backupUuid).ToList();
        list.Add(entry);
        WriteIndex(webspaceUuid, list);
    }

    public async Task<bool> DeleteAsync(Guid webspaceUuid, Guid backupUuid, CancellationToken cancellationToken = default)
    {
        var key = ObjectKey(webspaceUuid, backupUuid);
        try
        {
            await _s3.DeleteObjectAsync(_cfg.Bucket, key, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // still prune index
        }

        var list = ReadIndex(webspaceUuid);
        var had = list.Any(x => x.Uuid == backupUuid);
        WriteIndex(webspaceUuid, list.Where(x => x.Uuid != backupUuid).ToList());
        return had;
    }

    public async Task<Stream?> OpenReadAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        CancellationToken cancellationToken = default)
    {
        var key = ObjectKey(webspaceUuid, backupUuid);
        try
        {
            var response = await _s3.GetObjectAsync(_cfg.Bucket, key, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DownloadToFileAsync(
        Guid webspaceUuid,
        Guid backupUuid,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenReadAsync(webspaceUuid, backupUuid, cancellationToken)
                                 ?? throw new FileNotFoundException("Backup not found in S3.");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var fs = File.Create(destPath);
        await stream.CopyToAsync(fs, cancellationToken);
    }

    public bool Exists(Guid webspaceUuid, Guid backupUuid) =>
        ReadIndex(webspaceUuid).Any(x => x.Uuid == backupUuid);

    public Task<int> ReconcileAsync(Guid webspaceUuid, CancellationToken cancellationToken = default) =>
        Task.FromResult(List(webspaceUuid).Count);

    public void Dispose()
    {
        if (_ownsClient)
            _s3.Dispose();
    }

    private string ObjectKey(Guid webspaceUuid, Guid backupUuid)
    {
        var prefix = (_cfg.Prefix ?? "").Trim().Trim('/');
        var mid = string.IsNullOrEmpty(prefix) ? "" : prefix + "/";
        return $"{mid}{webspaceUuid}/{backupUuid}.tar.gz";
    }

    private string IndexPath(Guid webspaceUuid) =>
        Path.Combine(_indexRoot, webspaceUuid.ToString(), "index.json");

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

    private void WriteIndex(Guid webspaceUuid, List<BackupObjectInfo> entries)
    {
        var path = IndexPath(webspaceUuid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
    }
}
