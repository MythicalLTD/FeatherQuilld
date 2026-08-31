using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpacePullJobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly WebSpacePullJobStore _store;

    public WebSpacePullJobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-pull-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".featherquilld"));
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");

        var fs = new FakeFsAccess(_uuid, _root);
        var files = new WebSpaceFileService(fs);
        _store = new WebSpacePullJobStore(files, fs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void PersistedJobs_SurviveStoreReload()
    {
        var id = _store.StartPull(_uuid, "/", "https://example.com/missing.zip", "test.zip", 1024);
        Assert.False(string.IsNullOrWhiteSpace(id));

        Thread.Sleep(1500);

        var fs = new FakeFsAccess(_uuid, _root);
        var files = new WebSpaceFileService(fs);
        var reloaded = new WebSpacePullJobStore(files, fs);
        var jobs = reloaded.ListFor(_uuid);
        Assert.Contains(jobs, j => (string)((dynamic)j).Identifier == id);
    }

    private sealed class FakeFsAccess : IWebSpaceFsAccess
    {
        private readonly Guid _uuid;
        private readonly string _root;

        public FakeFsAccess(Guid uuid, string root)
        {
            _uuid = uuid;
            _root = root;
        }

        public WebSpace? Get(Guid uuid) =>
            uuid == _uuid
                ? new WebSpace { Uuid = uuid, Name = "test", Status = WebSpaceStatus.Installed }
                : null;

        public string DataPath(Guid uuid) => uuid == _uuid ? _root : throw new InvalidOperationException();
        public string EffectiveFsPath(Guid uuid) => DataPath(uuid);
    }
}
