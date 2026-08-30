using System.Formats.Tar;
using System.IO.Compression;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class BackupSelectiveRestoreTests : IDisposable
{
    private readonly string _root;

    public BackupSelectiveRestoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-sel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData("public/index.php", "public/index.php", true)]
    [InlineData("public/uploads/a.jpg", "public/uploads", true)]
    [InlineData("public/uploads/a.jpg", "wp-config.php", false)]
    [InlineData("webspace.json", "public", false)]
    public void PathMatches_SelectsFilesAndPrefixes(string entry, string selected, bool expected)
    {
        Assert.Equal(expected, WebSpaceBackupService.PathMatches(entry, [selected]));
    }

    [Fact]
    public void ExtractTarGzSelected_RestoresOnlyChosenPaths()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "public"));
        File.WriteAllText(Path.Combine(src, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(src, "skip.txt"), "skip");
        File.WriteAllText(Path.Combine(src, "public", "index.php"), "<?php");

        var archive = Path.Combine(_root, "b.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            TarFile.CreateFromDirectory(src, gzip, includeBaseDirectory: false);

        var dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "existing.txt"), "ok");

        WebSpaceBackupService.ExtractTarGzSelected(archive, dest, ["keep.txt", "public"]);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(dest, "keep.txt")));
        Assert.Equal("<?php", File.ReadAllText(Path.Combine(dest, "public", "index.php")));
        Assert.False(File.Exists(Path.Combine(dest, "skip.txt")));
        Assert.Equal("ok", File.ReadAllText(Path.Combine(dest, "existing.txt")));
    }
}
