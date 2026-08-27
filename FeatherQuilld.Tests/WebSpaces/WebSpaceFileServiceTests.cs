using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceFileServiceTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private readonly WebSpaceFileService _files;

    public WebSpaceFileServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-files-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "public"));
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "public", "index.html"), "hello");

        _files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void List_HidesWebspaceJson_AndShowsPublic()
    {
        var entries = _files.List(_uuid, "/");
        var names = entries.Select(e => (string)((dynamic)e).name).ToList();
        Assert.Contains("public", names);
        Assert.DoesNotContain("webspace.json", names);
    }

    [Fact]
    public void WriteRead_RoundTrip()
    {
        _files.WriteText(_uuid, "/public/note.txt", "via-test");
        Assert.Equal("via-test", _files.ReadText(_uuid, "/public/note.txt"));
    }

    [Fact]
    public void CreateDirectory_AndDelete()
    {
        _files.CreateDirectory(_uuid, "/public/subdir");
        Assert.True(Directory.Exists(Path.Combine(_root, "public", "subdir")));
        _files.Delete(_uuid, ["/public/subdir"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "public", "subdir")));
    }

    [Fact]
    public void Write_EscapePath_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            _files.WriteText(_uuid, "../escape.txt", "nope"));
    }

    [Fact]
    public void UnknownUuid_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _files.List(Guid.NewGuid(), "/"));
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

        public string EffectiveFsPath(Guid uuid) =>
            uuid == _uuid ? _root : throw new InvalidOperationException("missing");
    }
}
