using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Tests.Logging;

public class SystemLogReaderTests
{
    [Fact]
    public void ListFiles_IncludesLatestAndArchives()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "latest.log"), "line1\nline2\n");
            File.WriteAllBytes(Path.Combine(dir, "2026-08-30-1.log.gz"), [0x1f, 0x8b]);

            var files = SystemLogReader.ListFiles(dir);

            Assert.Contains(files, f => f.Name == "latest.log" && !f.Compressed);
            Assert.Contains(files, f => f.Name == "2026-08-30-1.log.gz" && f.Compressed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadTail_ReturnsLastLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "latest.log");
            File.WriteAllText(path, "one\ntwo\nthree\n");

            var tail = SystemLogReader.ReadTail(dir, "latest.log", 2);

            Assert.Equal("two\nthree", tail);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadTail_RejectsUnsafeFileName()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SystemLogReader.ReadTail("/tmp", "../secrets", 10));
    }
}
