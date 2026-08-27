using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces.Backups;
using Moq;

namespace FeatherQuilld.Tests.WebSpaces;

public class BackupStoreTests : IDisposable
{
    private readonly string _root;

    public BackupStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-bak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task LocalStore_PutListDelete_RoundTrip()
    {
        var store = new LocalBackupStore(_root);
        var ws = Guid.NewGuid();
        var bak = Guid.NewGuid();
        var tar = Path.Combine(_root, "src.tar.gz");
        await File.WriteAllBytesAsync(tar, [1, 2, 3, 4]);

        await store.PutAsync(ws, bak, tar, "abc");
        Assert.True(store.Exists(ws, bak));
        var list = store.List(ws);
        Assert.Single(list);
        Assert.Equal(bak, list[0].Uuid);
        Assert.Equal(4, list[0].Bytes);

        await using var stream = await store.OpenReadAsync(ws, bak);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ms.ToArray());

        Assert.True(await store.DeleteAsync(ws, bak));
        Assert.False(store.Exists(ws, bak));
    }

    [Fact]
    public async Task S3Store_PutListDelete_UsesClientAndIndex()
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var mock = new Mock<IAmazonS3>(MockBehavior.Strict);
        mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PutObjectRequest req, CancellationToken _) =>
            {
                objects[req.Key!] = File.ReadAllBytes(req.FilePath);
                return new PutObjectResponse();
            });
        mock.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string key, CancellationToken __) =>
            {
                if (!objects.TryGetValue(key, out var bytes))
                    throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
                return new GetObjectResponse
                {
                    ResponseStream = new MemoryStream(bytes),
                    HttpStatusCode = HttpStatusCode.OK,
                };
            });
        mock.Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string key, CancellationToken __) =>
            {
                objects.Remove(key);
                return new DeleteObjectResponse();
            });
        mock.Setup(s => s.Dispose());

        var system = new SystemConfig
        {
            BackupDirectory = _root,
            Backups = new BackupsConfig
            {
                Provider = "s3",
                S3 = new BackupS3Config
                {
                    Bucket = "test-bucket",
                    Prefix = "webspaces/",
                    AccessKey = "ak",
                    SecretKey = "sk",
                },
            },
        };

        using var store = new S3BackupStore(system, mock.Object);
        var ws = Guid.NewGuid();
        var bak = Guid.NewGuid();
        var tar = Path.Combine(_root, "s3-src.tar.gz");
        await File.WriteAllBytesAsync(tar, [9, 8, 7]);

        await store.PutAsync(ws, bak, tar, "chk");
        Assert.True(store.Exists(ws, bak));
        Assert.Single(store.List(ws));
        Assert.Single(objects);

        await using var stream = await store.OpenReadAsync(ws, bak);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(new byte[] { 9, 8, 7 }, ms.ToArray());

        Assert.True(await store.DeleteAsync(ws, bak));
        Assert.False(store.Exists(ws, bak));
        Assert.Empty(objects);
    }

    [Fact]
    public async Task ResticStore_PutDelete_InvokesCliWithTags()
    {
        var calls = new List<IReadOnlyList<string>>();
        var bak = Guid.NewGuid();
        Task<int> Runner(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___)
        {
            calls.Add(args.ToList());
            return Task.FromResult(0);
        }

        Task<(int Code, string Output)> RunnerWithOutput(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___)
        {
            calls.Add(args.ToList());
            if (args.Count > 0 && args[0] == "snapshots")
            {
                return Task.FromResult((0, $$"""[{"id":"snap-abc","tags":["backup={{bak:D}}"]}]"""));
            }

            return Task.FromResult((0, string.Empty));
        }

        var system = new SystemConfig
        {
            BackupDirectory = _root,
            TmpDirectory = Path.Combine(_root, "tmp"),
            Backups = new BackupsConfig
            {
                Provider = "restic",
                Restic = new BackupResticConfig
                {
                    Repository = "/tmp/restic-repo",
                    Password = "secret",
                    Binary = "restic",
                },
            },
        };
        Directory.CreateDirectory(system.TmpDirectory);

        var store = new ResticBackupStore(system, Runner, RunnerWithOutput);
        var ws = Guid.NewGuid();
        var tar = Path.Combine(_root, "restic-src.tar.gz");
        await File.WriteAllBytesAsync(tar, [1, 2]);

        await store.PutAsync(ws, bak, tar, "sum");
        Assert.True(store.Exists(ws, bak));
        Assert.Contains(calls, c => c.Count > 0 && c[0] == "backup" && c.Contains("--tag"));
        Assert.Contains(calls, c => c.Any(a => a == $"backup={bak:D}"));

        var listed = store.List(ws);
        Assert.Single(listed);
        Assert.Equal("snap-abc", listed[0].RemoteRef);

        Assert.True(await store.DeleteAsync(ws, bak));
        Assert.False(store.Exists(ws, bak));
        Assert.Contains(calls, c => c.Count > 0 && c[0] == "forget" && c.Contains("--prune"));
    }

    [Fact]
    public async Task PbsStore_PutDelete_InvokesSnapshotForget()
    {
        var calls = new List<IReadOnlyList<string>>();
        Task<int> Runner(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___)
        {
            calls.Add(args.ToList());
            return Task.FromResult(0);
        }

        var system = new SystemConfig
        {
            BackupDirectory = _root,
            Backups = new BackupsConfig
            {
                Provider = "pbs",
                Pbs = new BackupPbsConfig
                {
                    Repository = "user@pbs@host:store",
                    Password = "tok",
                    Fingerprint = "aa:bb",
                    Binary = "proxmox-backup-client",
                },
            },
        };

        var store = new PbsBackupStore(system, Runner);
        var ws = Guid.NewGuid();
        var bak = Guid.NewGuid();
        var tar = Path.Combine(_root, "pbs-src.tar.gz");
        await File.WriteAllBytesAsync(tar, [3, 4, 5]);

        await store.PutAsync(ws, bak, tar, "pbs-sum");
        Assert.True(store.Exists(ws, bak));
        Assert.Contains(calls, c => c.Count > 0 && c[0] == "backup" && c.Contains("--backup-time"));

        var listed = store.List(ws);
        Assert.Single(listed);
        Assert.StartsWith($"host/{ws:D}/", listed[0].RemoteRef);

        Assert.True(await store.DeleteAsync(ws, bak));
        Assert.False(store.Exists(ws, bak));
        Assert.Contains(calls, c =>
            c.Count >= 3
            && c[0] == "snapshot"
            && c[1] == "forget"
            && c[2].StartsWith($"host/{ws:D}/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResticStore_ReconcileAsync_RebuildsIndexFromSnapshotsJson()
    {
        var ws = Guid.NewGuid();
        var bak1 = Guid.NewGuid();
        var bak2 = Guid.NewGuid();
        var calls = new List<IReadOnlyList<string>>();

        Task<int> Runner(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___)
        {
            calls.Add(args.ToList());
            return Task.FromResult(0);
        }

        Task<(int Code, string Output)> RunnerWithOutput(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___)
        {
            calls.Add(args.ToList());
            if (args.Count > 0 && args[0] == "snapshots")
            {
                var json =
                    "[{\"id\":\"snap-1\",\"time\":\"2026-01-15T10:00:00Z\",\"tags\":[\"webspace=" + ws.ToString("D") +
                    "\",\"backup=" + bak1.ToString("D") +
                    "\"]},{\"id\":\"snap-2\",\"time\":\"2026-02-01T12:30:00Z\",\"tags\":[\"webspace=" + ws.ToString("D") +
                    "\",\"backup=" + bak2.ToString("D") +
                    "\"],\"summary\":{\"total_bytes_processed\":42}}]";
                return Task.FromResult((0, json));
            }

            return Task.FromResult((0, string.Empty));
        }

        var system = new SystemConfig
        {
            BackupDirectory = _root,
            TmpDirectory = Path.Combine(_root, "tmp"),
            Backups = new BackupsConfig
            {
                Provider = "restic",
                Restic = new BackupResticConfig
                {
                    Repository = "/tmp/restic-repo",
                    Password = "secret",
                    Binary = "restic",
                },
            },
        };
        Directory.CreateDirectory(system.TmpDirectory);

        var store = new ResticBackupStore(system, Runner, RunnerWithOutput);
        Assert.Empty(store.List(ws));

        var count = await store.ReconcileAsync(ws);
        Assert.Equal(2, count);
        Assert.Contains(calls, c =>
            c.Count >= 4
            && c[0] == "snapshots"
            && c.Contains("--json")
            && c.Contains($"webspace={ws:D}"));

        var listed = store.List(ws);
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, x => x.Uuid == bak1 && x.RemoteRef == "snap-1" && x.Bytes == 0 && x.Checksum == "");
        Assert.Contains(listed, x => x.Uuid == bak2 && x.RemoteRef == "snap-2" && x.Bytes == 42);
    }

    [Fact]
    public async Task PbsStore_ReconcileAsync_RebuildsIndexFromSnapshotList()
    {
        var ws = Guid.NewGuid();
        var bak = Guid.NewGuid();
        var unix = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var calls = new List<IReadOnlyList<string>>();

        Task<int> Runner(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___)
        {
            calls.Add(args.ToList());
            return Task.FromResult(0);
        }

        Task<(int Code, string Output)> RunnerWithOutput(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___)
        {
            calls.Add(args.ToList());
            if (args.Count >= 2 && args[0] == "snapshot" && args[1] == "list")
            {
                var json =
                    "[{\"backup-time\":" + unix +
                    ",\"files\":[{\"filename\":\"" + bak.ToString("D") + ".tar.gz\",\"size\":99}]}]";
                return Task.FromResult((0, json));
            }

            return Task.FromResult((0, string.Empty));
        }

        var system = new SystemConfig
        {
            BackupDirectory = _root,
            Backups = new BackupsConfig
            {
                Provider = "pbs",
                Pbs = new BackupPbsConfig
                {
                    Repository = "user@pbs@host:store",
                    Password = "tok",
                    Fingerprint = "aa:bb",
                    Binary = "proxmox-backup-client",
                },
            },
        };

        var store = new PbsBackupStore(system, Runner, RunnerWithOutput);
        var count = await store.ReconcileAsync(ws);
        Assert.Equal(1, count);
        Assert.Contains(calls, c =>
            c.Count >= 3
            && c[0] == "snapshot"
            && c[1] == "list"
            && c[2] == $"host/{ws:D}"
            && c.Contains("--output-format")
            && c.Contains("json")
            && c.Contains("--fingerprint"));

        var listed = store.List(ws);
        Assert.Single(listed);
        Assert.Equal(bak, listed[0].Uuid);
        Assert.Equal(99, listed[0].Bytes);
        Assert.Equal(PbsBackupStore.SnapshotRef(ws, DateTimeOffset.FromUnixTimeSeconds(unix)), listed[0].RemoteRef);
    }

    [Fact]
    public async Task PbsStore_ReconcileAsync_KeepsIndexWhenListFails()
    {
        var ws = Guid.NewGuid();
        var bak = Guid.NewGuid();
        Task<int> Runner(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___) =>
            Task.FromResult(0);

        Task<(int Code, string Output)> RunnerWithOutput(string _, IReadOnlyList<string> args, IDictionary<string, string>? __, CancellationToken ___) =>
            Task.FromResult((1, ""));

        var system = new SystemConfig
        {
            BackupDirectory = _root,
            Backups = new BackupsConfig
            {
                Provider = "pbs",
                Pbs = new BackupPbsConfig
                {
                    Repository = "user@pbs@host:store",
                    Password = "tok",
                    Binary = "proxmox-backup-client",
                },
            },
        };

        var store = new PbsBackupStore(system, Runner, RunnerWithOutput);
        var tar = Path.Combine(_root, "pbs-keep.tar.gz");
        await File.WriteAllBytesAsync(tar, [1]);
        // Seed index via Put (uses Runner, not WithOutput)
        await store.PutAsync(ws, bak, tar, "keep");
        Assert.Single(store.List(ws));

        var count = await store.ReconcileAsync(ws);
        Assert.Equal(1, count);
        Assert.True(store.Exists(ws, bak));
    }

    [Fact]
    public async Task LocalStore_ReconcileAsync_ReturnsListCount()
    {
        var store = new LocalBackupStore(_root);
        var ws = Guid.NewGuid();
        var bak = Guid.NewGuid();
        var tar = Path.Combine(_root, "local-rec.tar.gz");
        await File.WriteAllBytesAsync(tar, [1, 2]);
        await store.PutAsync(ws, bak, tar, "x");
        Assert.Equal(1, await store.ReconcileAsync(ws));
    }

    [Fact]
    public void Pbs_FormatSnapshotTime_IsUtcIso()
    {
        var t = new DateTimeOffset(2026, 8, 26, 12, 34, 56, TimeSpan.Zero);
        Assert.Equal("2026-08-26T12:34:56Z", PbsBackupStore.FormatSnapshotTime(t));
    }

    [Fact]
    public void NginxAcme_CertPaths_And_FreshFalseWhenMissing()
    {
        Assert.EndsWith("example.com.crt", NginxAcmeService.CertPath("example.com"));
        Assert.EndsWith("example.com.key", NginxAcmeService.KeyPath("example.com"));
        Assert.False(NginxAcmeService.IsCertFresh("definitely-missing-" + Guid.NewGuid().ToString("N") + ".test"));
    }
}
