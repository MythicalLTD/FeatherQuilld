using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceFileSearchPullTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("cccccccc-dddd-eeee-ffff-000000000001");

    public WebSpaceFileSearchPullTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-search-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_root, "public", "nested"));
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "public", "index.html"), "home");
        File.WriteAllText(Path.Combine(_root, "public", "nested", "readme.md"), "docs");
        File.WriteAllText(Path.Combine(_root, "public", "logo.png"), "img");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Search_FindsByNameSubstring()
    {
        var files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root));
        var hits = files.Search(_uuid, "/", "read");
        var paths = hits.Select(h => (string)((dynamic)h).path).ToList();
        Assert.Contains("/public/nested/readme.md", paths);
        Assert.DoesNotContain(paths, p => p.Contains("webspace.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root));
        var hits = files.Search(_uuid, "/public", "e", limit: 1);
        Assert.Single(hits);
    }

    [Fact]
    public void ValidatePullUrl_BlocksPrivateAndNonHttp()
    {
        Assert.Throws<ArgumentException>(() => WebSpaceFileService.ValidatePullUrl("ftp://example.com/a"));
        Assert.Throws<ArgumentException>(() => WebSpaceFileService.ValidatePullUrl("http://127.0.0.1/a"));
        Assert.Throws<ArgumentException>(() => WebSpaceFileService.ValidatePullUrl("http://10.0.0.5/a"));
        Assert.Throws<ArgumentException>(() => WebSpaceFileService.ValidatePullUrl("http://localhost/a"));
        var ok = WebSpaceFileService.ValidatePullUrl("https://example.com/file.zip");
        Assert.Equal("example.com", ok.Host);
    }

    [Fact]
    public async Task PullAsync_WritesFileFromHttp()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pulled-bytes")),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return response;
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root), http);

        var path = await files.PullAsync(_uuid, "/public", "https://example.com/remote.txt", fileName: "got.txt");
        Assert.Equal("/public/got.txt", path);
        Assert.Equal("pulled-bytes", File.ReadAllText(Path.Combine(_root, "public", "got.txt")));
    }

    [Fact]
    public async Task PullAsync_EnforcesMaxBytes()
    {
        var handler = new StubHandler(_ =>
        {
            var big = new string('x', 1000);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(big),
            };
        });
        var files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root), new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            files.PullAsync(_uuid, "/public", "https://example.com/big.bin", maxBytes: 100));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
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
