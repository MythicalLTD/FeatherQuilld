using System.Collections.Concurrent;
using System.Text.Json;

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
    private const int MaxPersistedJobs = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<string, WebSpacePullJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, byte> _loaded = new();
    private readonly WebSpaceFileService _files;
    private readonly IWebSpaceFsAccess _spaces;

    public WebSpacePullJobStore(WebSpaceFileService files, IWebSpaceFsAccess spaces)
    {
        _files = files;
        _spaces = spaces;
    }

    public string StartPull(
        Guid uuid,
        string? directory,
        string url,
        string? fileName,
        long maxBytes)
    {
        EnsureLoaded(uuid);
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
        Persist(uuid);

        _ = Task.Run(async () =>
        {
            try
            {
                job.Progress = 10;
                Persist(uuid);
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
            finally
            {
                Persist(uuid);
            }
        }, cts.Token);

        return id;
    }

    public IReadOnlyList<object> ListFor(Guid uuid)
    {
        EnsureLoaded(uuid);
        return _jobs.Values
            .Where(j => j.WebSpaceUuid == uuid)
            .OrderByDescending(j => j.Identifier)
            .Select(j => new
            {
                Identifier = j.Identifier,
                Progress = j.Progress,
                Status = j.Status,
                Error = j.Error,
                ResultPath = j.ResultPath,
            })
            .ToList();
    }

    public bool Cancel(Guid uuid, string identifier)
    {
        EnsureLoaded(uuid);
        if (!_jobs.TryGetValue(identifier, out var job) || job.WebSpaceUuid != uuid)
            return false;
        job.Cts?.Cancel();
        job.Status = "cancelled";
        job.Error = "Cancelled";
        Persist(uuid);
        return true;
    }

    private void EnsureLoaded(Guid uuid)
    {
        if (!_loaded.ContainsKey(uuid))
        {
            lock (_loaded)
            {
                if (_loaded.ContainsKey(uuid))
                    return;

                var path = IndexPath(uuid);
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        var records = JsonSerializer.Deserialize<List<PullJobRecord>>(json, JsonOptions) ?? [];
                        foreach (var record in records)
                        {
                            if (record.WebSpaceUuid != uuid)
                                continue;
                            if (_jobs.ContainsKey(record.Identifier))
                                continue;

                            var status = record.Status;
                            var error = record.Error;
                            if (status == "running")
                            {
                                status = "failed";
                                error = "Interrupted by daemon restart";
                            }

                            _jobs[record.Identifier] = new WebSpacePullJob
                            {
                                Identifier = record.Identifier,
                                WebSpaceUuid = uuid,
                                Progress = record.Progress,
                                Status = status,
                                Error = error,
                                ResultPath = record.ResultPath,
                            };
                        }
                    }
                    catch
                    {
                        // ignore corrupt index
                    }
                }

                _loaded[uuid] = 0;
            }
        }
    }

    private void Persist(Guid uuid)
    {
        try
        {
            var records = _jobs.Values
                .Where(j => j.WebSpaceUuid == uuid)
                .OrderByDescending(j => j.Identifier)
                .Take(MaxPersistedJobs)
                .Select(j => new PullJobRecord
                {
                    Identifier = j.Identifier,
                    WebSpaceUuid = j.WebSpaceUuid,
                    Progress = j.Progress,
                    Status = j.Status,
                    Error = j.Error,
                    ResultPath = j.ResultPath,
                })
                .ToList();

            var path = IndexPath(uuid);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions));
        }
        catch
        {
            // persistence is best-effort
        }
    }

    private string IndexPath(Guid uuid) =>
        Path.Combine(_spaces.EffectiveFsPath(uuid), ".featherquilld", "pull-jobs.json");

    private sealed class PullJobRecord
    {
        public string Identifier { get; set; } = "";
        public Guid WebSpaceUuid { get; set; }
        public int Progress { get; set; }
        public string Status { get; set; } = "pending";
        public string? Error { get; set; }
        public string? ResultPath { get; set; }
    }
}
