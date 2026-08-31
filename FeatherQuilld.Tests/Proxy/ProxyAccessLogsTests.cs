using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.Proxy;

public class ProxyAccessLogsTests
{
    [Fact]
    public void SearchFile_FiltersCaseInsensitive()
    {
        var path = Path.Combine(Path.GetTempPath(), "fq-search-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllLines(path,
            [
                "GET /ok HTTP/1.1 200",
                "GET /error HTTP/1.1 500",
                "POST /api HTTP/1.1 201",
            ]);

            var result = ProxyAccessLogs.SearchFile(path, "error", scanLines: 100, resultLimit: 50, regex: false);
            Assert.Contains("500", result.Text);
            Assert.DoesNotContain("200", result.Text);
            Assert.False(result.Truncated);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void RotateSpace_TruncatesLiveLogsAfterSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-rotate-" + Guid.NewGuid().ToString("N"));
        var uuid = Guid.NewGuid();
        Directory.CreateDirectory(ProxyAccessLogs.DirectoryFor(root, uuid));
        try
        {
            var accessPath = ProxyAccessLogs.AccessLogPath(root, uuid, "app.example.com");
            File.WriteAllText(
                accessPath,
                """
                1.2.3.4 - - [30/Aug/2026:00:00:00 +0000] "GET / HTTP/1.1" 200 100
                """);

            var space = new WebSpace { Uuid = uuid, Domains = ["app.example.com"] };
            ProxyAccessLogs.RotateSpace(root, space);

            Assert.True(File.Exists(ProxyAccessLogs.SummaryPath(root, uuid, "app.example.com", new DateOnly(2026, 8, 30))));
            Assert.Equal(string.Empty, File.ReadAllText(accessPath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
