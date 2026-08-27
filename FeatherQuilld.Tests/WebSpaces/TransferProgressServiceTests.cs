using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public sealed class TransferProgressServiceTests : IDisposable
{
    private readonly string _dir;

    public TransferProgressServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fq-xfer-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void MarkRunning_Persists_And_Get_Returns()
    {
        var svc = new TransferProgressService(_dir);
        var uuid = Guid.NewGuid();
        svc.MarkRunning(uuid, "outgoing", "Packing…");

        var state = svc.Get(uuid);
        Assert.NotNull(state);
        Assert.Equal(TransferPhase.Running, state!.Phase);
        Assert.Equal("outgoing", state.Direction);
        Assert.Equal("Packing…", state.Message);
        Assert.True(File.Exists(Path.Combine(_dir, $"{uuid:D}.json")));
    }

    [Fact]
    public void Recover_Marks_Running_Failed()
    {
        var svc1 = new TransferProgressService(_dir);
        var uuid = Guid.NewGuid();
        svc1.MarkRunning(uuid, "incoming");

        var svc2 = new TransferProgressService(_dir);
        var recovered = svc2.Get(uuid);
        Assert.NotNull(recovered);
        Assert.Equal(TransferPhase.Failed, recovered!.Phase);
        Assert.Equal("daemon restarted", recovered.Message);
    }

    [Fact]
    public void MarkCompleted_Then_Get()
    {
        var svc = new TransferProgressService(_dir);
        var uuid = Guid.NewGuid();
        svc.MarkRunning(uuid, "outgoing");
        svc.MarkCompleted(uuid, "outgoing");
        var done = svc.Get(uuid);
        Assert.Equal(TransferPhase.Completed, done!.Phase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
