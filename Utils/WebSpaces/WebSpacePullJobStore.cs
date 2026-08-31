using System.Collections.Concurrent;

namespace FeatherQuilld.Utils.WebSpaces;

public sealed class WebSpacePullJob
{
    public string Identifier { get; init; } = "";
    public Guid WebSpaceUuid { get; init; }
    public int Progress { get; set; }
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public string? ResultPath { get; set; }
    public CancellationTokenSource? Cts { get; init; }
}

public sealed class WebSpacePullJobStore
{
    private readonly ConcurrentDictionary<string, WebSpacePullJob> _jobs = new();
    private readonly WebSpaceFileService _files;

    public WebSpacePullJobStore(WebSpaceFileService files) => _files = files;

    public string StartPull(
        Guid uuid,
        string? directory,
        string url,
        string? fileName,
        long maxBytes)
    {
        var id = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        var job = new WebSpacePullJob
        {
            Identifier = id,
            WebSpaceUuid = uuid,
            Progress = 0,
            Status = "running",
            Cts = cts,
        };
        _jobs[id] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                job.Progress = 10;
                var path = await _files.PullAsync(uuid, directory, url, fileName, maxBytes, cts.Token);
                job.ResultPath = path;
                job.Progress = 100;
                job.Status = "completed";
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
                job.Error = "Cancelled";
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.Error = ex.Message;
            }
        }, cts.Token);

        return id;
    }

    public IReadOnlyList<object> ListFor(Guid uuid) =>
        _jobs.Values
            .Where(j => j.WebSpaceUuid == uuid)
            .Select(j => new
            {
                Identifier = j.Identifier,
                Progress = j.Progress,
                Status = j.Status,
                Error = j.Error,
                ResultPath = j.ResultPath,
            })
            .ToList();

    public bool Cancel(Guid uuid, string identifier)
    {
        if (!_jobs.TryGetValue(identifier, out var job) || job.WebSpaceUuid != uuid)
            return false;
        job.Cts?.Cancel();
        _jobs.TryRemove(identifier, out _);
        return true;
    }
}
