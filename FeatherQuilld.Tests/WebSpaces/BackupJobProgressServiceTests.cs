using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class BackupJobProgressServiceTests : IDisposable
{
    private readonly string _dir;

    public BackupJobProgressServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fq-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Start_And_Complete_Tracks_Backup_Metadata()
    {
        var svc = new BackupJobProgressService(_dir);
        var space = Guid.NewGuid();
        var job = svc.Start(space, "create");

        Assert.Equal(BackupJobPhase.Running, job.Phase);
        Assert.Equal("create", job.Operation);
        Assert.Equal(space, job.WebSpaceUuid);

        var backupUuid = Guid.NewGuid();
        svc.MarkCompleted(job.JobId, backupUuid, 1234, "sha256:deadbeef");

        var done = svc.Get(job.JobId);
        Assert.NotNull(done);
        Assert.Equal(BackupJobPhase.Completed, done!.Phase);
        Assert.Equal(backupUuid, done.BackupUuid);
        Assert.Equal(1234, done.Bytes);
        Assert.Equal("sha256:deadbeef", done.Checksum);
        Assert.True(File.Exists(Path.Combine(_dir, $"{job.JobId:D}.json")));
    }

    [Fact]
    public void MarkFailed_Sets_Message()
    {
        var svc = new BackupJobProgressService(_dir);
        var job = svc.Start(Guid.NewGuid(), "restore");
        svc.MarkFailed(job.JobId, "disk full");

        var failed = svc.Get(job.JobId);
        Assert.NotNull(failed);
        Assert.Equal(BackupJobPhase.Failed, failed!.Phase);
        Assert.Equal("disk full", failed.Message);
    }

    [Fact]
    public void Get_Unknown_Returns_Null()
    {
        var svc = new BackupJobProgressService(_dir);
        Assert.Null(svc.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Recover_Marks_Running_Jobs_Failed()
    {
        var svc1 = new BackupJobProgressService(_dir);
        var job = svc1.Start(Guid.NewGuid(), "create");
        Assert.Equal(BackupJobPhase.Running, job.Phase);

        var svc2 = new BackupJobProgressService(_dir);
        var recovered = svc2.Get(job.JobId);
        Assert.NotNull(recovered);
        Assert.Equal(BackupJobPhase.Failed, recovered!.Phase);
        Assert.Equal("daemon restarted", recovered.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
