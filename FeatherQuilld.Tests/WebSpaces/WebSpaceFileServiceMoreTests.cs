using System.Text;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceFileServiceMoreTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly WebSpaceFileService _files;

    public WebSpaceFileServiceMoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-files2-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_root, "public"));
        _files = new WebSpaceFileService(new Fake(_uuid, _root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Rename_File()
    {
        _files.WriteText(_uuid, "/public/a.txt", "a");
        _files.Rename(_uuid, "/public/a.txt", "/public/b.txt");
        Assert.Equal("a", _files.ReadText(_uuid, "/public/b.txt"));
        Assert.Throws<FileNotFoundException>(() => _files.ReadText(_uuid, "/public/a.txt"));
    }

    [Fact]
    public void Copy_File_AutoNameAndExplicit()
    {
        _files.WriteText(_uuid, "/public/note.txt", "hello");
        var auto = _files.Copy(_uuid, "/public/note.txt");
        Assert.Equal("/public/note copy.txt", auto);
        Assert.Equal("hello", _files.ReadText(_uuid, auto));
        Assert.Equal("hello", _files.ReadText(_uuid, "/public/note.txt"));

        var dest = _files.Copy(_uuid, "/public/note.txt", "/public/note-2.txt");
        Assert.Equal("/public/note-2.txt", dest);
        Assert.Equal("hello", _files.ReadText(_uuid, dest));
    }

    [Fact]
    public void CopyMany_IntoDestination()
    {
        _files.WriteText(_uuid, "/public/a.txt", "a");
        _files.WriteText(_uuid, "/public/b.txt", "b");
        _files.CreateDirectory(_uuid, "/public/out");
        var results = _files.CopyMany(_uuid, ["/public/a.txt", "/public/b.txt"], "/public/out");
        Assert.Equal(2, results.Count);
        Assert.Equal("a", _files.ReadText(_uuid, "/public/out/a.txt"));
        Assert.Equal("b", _files.ReadText(_uuid, "/public/out/b.txt"));
    }

    [Fact]
    public void CreateSymlink_And_Fingerprints()
    {
        _files.WriteText(_uuid, "/public/target.txt", "hash-me");
        _files.CreateSymlink(_uuid, "/public/link.txt", "/public/target.txt");
        Assert.True(File.Exists(Path.Combine(_root, "public", "link.txt")));

        var sha256 = _files.Fingerprints(_uuid, ["/public/target.txt"], "sha256");
        Assert.Single(sha256);
        var entry = Assert.IsAssignableFrom<object>(sha256[0]);
        var hashProp = entry.GetType().GetProperty("hash")!.GetValue(entry) as string;
        Assert.False(string.IsNullOrWhiteSpace(hashProp));
        Assert.Equal(64, hashProp!.Length);

        Assert.Throws<ArgumentException>(() => _files.Fingerprints(_uuid, ["/public/target.txt"], "md5"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _files.CreateSymlink(_uuid, "/public/evil", "/../../outside"));
    }

    [Fact]
    public async Task UploadAsync_WritesStream()
    {
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("upload-bytes"));
        await _files.UploadAsync(_uuid, "/public", "up.txt", ms);
        Assert.Equal("upload-bytes", _files.ReadText(_uuid, "/public/up.txt"));
    }

    [Fact]
    public void OpenRead_StreamsContent()
    {
        _files.WriteText(_uuid, "/public/r.txt", "stream-me");
        using var s = _files.OpenRead(_uuid, "/public/r.txt");
        using var reader = new StreamReader(s);
        Assert.Equal("stream-me", reader.ReadToEnd());
    }

    [Fact]
    public void Delete_SkipsWebspaceJson()
    {
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");
        _files.Delete(_uuid, ["/webspace.json"]);
        Assert.True(File.Exists(Path.Combine(_root, "webspace.json")));
    }

    private sealed class Fake(Guid uuid, string root) : IWebSpaceFsAccess
    {
        public WebSpace? Get(Guid id) =>
            id == uuid ? new WebSpace { Uuid = uuid, Name = "t" } : null;

        public string EffectiveFsPath(Guid id) =>
            id == uuid ? root : throw new InvalidOperationException();
    }
}
