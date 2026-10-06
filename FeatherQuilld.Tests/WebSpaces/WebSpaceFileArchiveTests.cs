using System.IO.Compression;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceFileArchiveTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
    private readonly WebSpaceFileService _files;

    public WebSpaceFileArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-arch-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "public"));
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "public", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_root, "public", "b.txt"), "beta");
        _files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Compress_TarGz_And_Decompress_RoundTrip()
    {
        var archive = _files.Compress(
            _uuid,
            "/public",
            ["/public/a.txt", "/public/b.txt"],
            archiveName: "bundle",
            extension: "tar.gz");

        Assert.Equal("/public/bundle.tar.gz", archive);
        Assert.True(File.Exists(Path.Combine(_root, "public", "bundle.tar.gz")));

        File.Delete(Path.Combine(_root, "public", "a.txt"));
        File.Delete(Path.Combine(_root, "public", "b.txt"));

        _files.Decompress(_uuid, archive);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_root, "public", "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_root, "public", "b.txt")));
    }

    [Fact]
    public void Compress_Zip_RoundTrip()
    {
        var archive = _files.Compress(
            _uuid,
            "/public",
            ["a.txt"],
            archiveName: "z",
            extension: "zip");

        Assert.EndsWith(".zip", archive);
        File.Delete(Path.Combine(_root, "public", "a.txt"));
        _files.Decompress(_uuid, archive);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_root, "public", "a.txt")));
    }

    [Fact]
    public void Decompress_ZipSlip_Throws()
    {
        var evil = Path.Combine(_root, "public", "evil.zip");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("nope");
        }

        Assert.Throws<UnauthorizedAccessException>(() =>
            _files.Decompress(_uuid, "/public/evil.zip"));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public void Compress_7z_RoundTrip_And_ListArchive()
    {
        var archive = _files.Compress(
            _uuid,
            "/public",
            ["/public/a.txt", "/public/b.txt"],
            archiveName: "bundle",
            extension: "7z");

        Assert.Equal("/public/bundle.7z", archive);
        Assert.True(File.Exists(Path.Combine(_root, "public", "bundle.7z")));

        var listing = _files.ListArchiveDirectory(_uuid, "/public", "bundle.7z", "");
        var names = listing.Contents.Select(e => (string)((dynamic)e).name).ToList();
        Assert.Contains("a.txt", names);
        Assert.Contains("b.txt", names);

        File.Delete(Path.Combine(_root, "public", "a.txt"));
        File.Delete(Path.Combine(_root, "public", "b.txt"));

        _files.Decompress(_uuid, archive);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_root, "public", "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_root, "public", "b.txt")));
    }

    [Fact]
    public void Chmod_AppliesMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        _files.Chmod(_uuid, [("/public/a.txt", "0755")]);
        var mode = File.GetUnixFileMode(Path.Combine(_root, "public", "a.txt"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, mode);
    }

    [Fact]
    public void ParseOctalMode_AcceptsCommonForms()
    {
        Assert.Equal((UnixFileMode)0b110_100_100, WebSpaceFileService.ParseOctalMode("0644"));
        Assert.Equal((UnixFileMode)0b111_101_101, WebSpaceFileService.ParseOctalMode("755"));
        Assert.Throws<ArgumentException>(() => WebSpaceFileService.ParseOctalMode("xyz"));
    }

    [Fact]
    public void ParseOctalMode_StripsSetuidSetgidSticky()
    {
        Assert.Equal((UnixFileMode)0b111_101_101, WebSpaceFileService.ParseOctalMode("4755"));
        Assert.Equal((UnixFileMode)0b110_100_100, WebSpaceFileService.ParseOctalMode("4644"));
    }

    [Fact]
    public void Compress_SkipsEscapingSymlinkContent()
    {
        if (OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(Path.GetTempPath(), "fq-pack-out-" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "HOSTCONFIDENTIAL");
        try
        {
            File.CreateSymbolicLink(
                Path.Combine(_root, "public", "leak.txt"),
                Path.Combine(outside, "secret.txt"));
            File.WriteAllText(Path.Combine(_root, "public", "ok.txt"), "safe");

            var archiveVirtual = _files.Compress(_uuid, "/public", ["ok.txt", "leak.txt"], "pack", "zip");
            var archivePath = Path.Combine(_root, archiveVirtual.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains(names, n => n.Contains("ok.txt", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("leak.txt", StringComparison.Ordinal));
            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0) continue;
                using var s = entry.Open();
                using var reader = new StreamReader(s);
                Assert.DoesNotContain("HOSTCONFIDENTIAL", reader.ReadToEnd());
            }
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Compress_EscapePath_SkipsAndFailsWhenEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            _files.Compress(_uuid, "/", ["../etc/passwd"], "x", "zip"));
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
