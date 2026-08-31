using System.IO.Compression;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceFileArchiveBrowseTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("dddddddd-eeee-ffff-0000-111111111111");
    private readonly WebSpaceFileService _files;

    public WebSpaceFileArchiveBrowseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-arch-browse-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_root, "public", "folder", "sub"));
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "public", "root.txt"), "top");
        File.WriteAllText(Path.Combine(_root, "public", "folder", "file.txt"), "inner");
        File.WriteAllText(Path.Combine(_root, "public", "folder", "sub", "deep.txt"), "deep");
        CreateNestedZip(Path.Combine(_root, "public", "nested.zip"));
        _files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root));
        _files.Compress(_uuid, "/public", ["folder", "root.txt"], archiveName: "nested", extension: "tar.gz");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void ListArchiveDirectory_Zip_ReturnsImmediateChildrenAtRoot()
    {
        var result = _files.ListArchiveDirectory(_uuid, "/public", "nested.zip", null);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Contents.Count);

        dynamic folder = result.Contents.First(c => ((dynamic)c).name == "folder");
        Assert.True((bool)folder.directory);
        Assert.Equal("folder", (string)folder.path);

        dynamic rootTxt = result.Contents.First(c => ((dynamic)c).name == "root.txt");
        Assert.False((bool)rootTxt.directory);
    }

    [Fact]
    public void ListArchiveDirectory_Zip_ReturnsImmediateChildrenUnderPrefix()
    {
        var result = _files.ListArchiveDirectory(_uuid, "/public", "nested.zip", "folder");
        Assert.Equal(2, result.Contents.Count);
        dynamic sub = result.Contents.First(c => ((dynamic)c).name == "sub");
        Assert.True((bool)sub.directory);
        Assert.Equal("folder/sub", (string)sub.path);
    }

    [Fact]
    public void ListArchiveDirectory_TarGz_ReturnsImmediateChildrenAtRoot()
    {
        var result = _files.ListArchiveDirectory(_uuid, "/public", "nested.tar.gz", "/");
        Assert.Equal(2, result.Contents.Count);
        Assert.Contains(result.Contents, c => ((dynamic)c).name == "folder");
        Assert.Contains(result.Contents, c => ((dynamic)c).name == "root.txt");
    }

    private static void CreateNestedZip(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("folder/sub/deep.txt").Open().Dispose();
        using (var w = new StreamWriter(zip.CreateEntry("folder/file.txt").Open()))
            w.Write("inner");
        using (var w = new StreamWriter(zip.CreateEntry("root.txt").Open()))
            w.Write("top");
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

        public string EffectiveFsPath(Guid uuid) => _root;
    }
}
