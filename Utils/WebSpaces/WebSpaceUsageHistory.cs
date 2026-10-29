using System.Collections.Concurrent;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>One utilization sample kept for usage graphs.</summary>
public sealed record WebSpaceUsageSample(
    DateTimeOffset At,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long DiskUsedBytes,
    long? NetworkRxBytes,
    long? NetworkTxBytes,
    long BandwidthUsedBytes);

/// <summary>
/// In-memory ring buffer of utilization samples per WebSpace, plus edge-triggered limit detection
/// (an alert fires once when usage crosses the threshold and re-arms after it drops back below).
/// </summary>
public sealed class WebSpaceUsageHistory
{
    public const int DefaultCapacity = 1440;
    public const double LimitThreshold = 0.9;

    private readonly int _capacity;
    private readonly ConcurrentDictionary<Guid, LinkedList<WebSpaceUsageSample>> _samples = new();
    private readonly ConcurrentDictionary<(Guid, string), bool> _alerted = new();

    public WebSpaceUsageHistory(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Add(Guid uuid, WebSpaceUsageSample sample)
    {
        var list = _samples.GetOrAdd(uuid, _ => new LinkedList<WebSpaceUsageSample>());
        lock (list)
        {
            list.AddLast(sample);
            while (list.Count > _capacity)
                list.RemoveFirst();
        }
    }

    public IReadOnlyList<WebSpaceUsageSample> Get(Guid uuid, DateTimeOffset? since = null)
    {
        if (!_samples.TryGetValue(uuid, out var list))
            return [];
        lock (list)
        {
            return since is null ? list.ToList() : list.Where(s => s.At >= since).ToList();
        }
    }

    public void Forget(Guid uuid)
    {
        _samples.TryRemove(uuid, out _);
        foreach (var key in _alerted.Keys.Where(k => k.Item1 == uuid).ToList())
            _alerted.TryRemove(key, out _);
    }

    /// <summary>Returns the resources that just crossed the limit threshold (each reported once until it recovers).</summary>
    public IReadOnlyList<(string Resource, long Used, long Limit)> CheckLimits(Guid uuid, WebSpaceUtilizationResponse stats)
    {
        var crossed = new List<(string, long, long)>();
        Check("disk", stats.DiskUsedBytes, stats.DiskLimitBytes);
        Check("bandwidth", stats.BandwidthUsedBytes, stats.BandwidthLimitBytes);
        if (stats.MemoryUsedBytes is long mem && stats.MemoryLimitBytes is long memLimit)
            Check("memory", mem, memLimit);
        return crossed;

        void Check(string resource, long used, long limit)
        {
            if (limit <= 0)
                return;
            var over = used >= limit * LimitThreshold;
            var key = (uuid, resource);
            var was = _alerted.TryGetValue(key, out var v) && v;
            if (over && !was)
                crossed.Add((resource, used, limit));
            _alerted[key] = over;
        }
    }
}

/// <summary>Samples every WebSpace once a minute into <see cref="WebSpaceUsageHistory"/> and reports limit alerts to the panel.</summary>
public sealed class WebSpaceUsageSamplerHostedService(
    WebSpaceStore spaces,
    WebSpaceUtilizationService utilization,
    WebSpaceUsageHistory history,
    WebSpaceActivityReporter? activity = null) : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            SampleAll();
        } while (await SafeWait(timer, stoppingToken));
    }

    private static async Task<bool> SafeWait(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }

    internal void SampleAll()
    {
        foreach (var space in spaces.List())
        {
            try
            {
                var stats = utilization.Get(space.Uuid);
                history.Add(space.Uuid, new WebSpaceUsageSample(
                    DateTimeOffset.UtcNow,
                    stats.CpuPercent,
                    stats.MemoryUsedBytes,
                    stats.DiskUsedBytes,
                    stats.NetworkRxBytes,
                    stats.NetworkTxBytes,
                    stats.BandwidthUsedBytes));
                foreach (var (resource, used, limit) in history.CheckLimits(space.Uuid, stats))
                    activity?.Report(space.Uuid, "limit_reached", new { resource, used_bytes = used, limit_bytes = limit });
            }
            catch
            {
                // A single broken WebSpace must not stop sampling the others.
            }
        }
    }
}
