using System.Net;
using System.Text;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpacePullJobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("eeeeeeee-ffff-0000-1111-222222222222");
    private readonly Guid _otherUuid = Guid.Parse("ffffffff-0000-1111-2222-333333333333");

    public WebSpacePullJobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-pull-job-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_root, "public"));
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task StartPull_CompletesWithResultPath()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok"),
        });
        var files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root), new HttpClient(handler));
        var store = new WebSpacePullJobStore(files);

        var id = store.StartPull(_uuid, "/public", "https://example.com/a.txt", "job.txt", 1024 * 1024);
        await WaitForJob(store, _uuid, id, "completed");

        var jobs = store.ListFor(_uuid);
        var job = Assert.Single(jobs);
        Assert.Equal(id, ((dynamic)job).Identifier);
        Assert.Equal(100, (int)((dynamic)job).Progress);
        Assert.Equal("completed", (string)((dynamic)job).Status);
        Assert.Equal("/public/job.txt", (string)((dynamic)job).ResultPath);
        Assert.Null(((dynamic)job).Error);
        Assert.Equal("ok", File.ReadAllText(Path.Combine(_root, "public", "job.txt")));
    }

    [Fact]
    public async Task Cancel_ScopedByWebSpaceUuid()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(_ =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(5));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("slow") };
        });
        var files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root), new HttpClient(handler));
        var store = new WebSpacePullJobStore(files);

        var id = store.StartPull(_uuid, "/public", "https://example.com/slow.txt", "slow.txt", 1024 * 1024);
        Assert.False(store.Cancel(_otherUuid, id));
        Assert.True(store.Cancel(_uuid, id));
        gate.TrySetResult(true);
        await Task.Delay(200);
        Assert.Empty(store.ListFor(_uuid));
    }

    [Fact]
    public async Task StartPull_RecordsFailure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("network down"));
        var files = new WebSpaceFileService(new FakeFsAccess(_uuid, _root), new HttpClient(handler));
        var store = new WebSpacePullJobStore(files);

        var id = store.StartPull(_uuid, "/public", "https://example.com/fail.txt", null, 1024);
        await WaitForJob(store, _uuid, id, "failed");

        var job = Assert.Single(store.ListFor(_uuid));
        Assert.Equal("failed", (string)((dynamic)job).Status);
        Assert.Contains("network down", (string)((dynamic)job).Error);
    }

    private static async Task WaitForJob(WebSpacePullJobStore store, Guid uuid, string id, string status)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = store.ListFor(uuid).Cast<dynamic>().FirstOrDefault(j => (string)j.Identifier == id);
            if (job is not null && (string)job.Status == status)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Job {id} did not reach status {status}");
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

        public string EffectiveFsPath(Guid uuid) => _root;
    }
}
