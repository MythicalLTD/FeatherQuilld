using System.Collections.Concurrent;
using Docker.DotNet;
using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Logger;
using Newtonsoft.Json;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

public sealed record WebSpaceUtilizationResponse(
    Guid Uuid,
    long DiskLimitBytes,
    long DiskUsedBytes,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryLimitBytes,
    long? NetworkRxBytes,
    long? NetworkTxBytes,
    long BandwidthLimitBytes,
    long BandwidthUsedBytes,
    bool BandwidthOverQuota,
    string State);

/// <summary>Per-WebSpace resource utilization (disk + optional Docker stats).</summary>
public sealed class WebSpaceUtilizationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);

    private readonly DockerConfig _docker;
    private readonly WebSpaceStore _spaces;
    private readonly AppLogger? _logger;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, WebSpaceUtilizationResponse Value)> _cache = new();

    public WebSpaceUtilizationService(DockerConfig docker, WebSpaceStore spaces, AppLogger? logger = null)
    {
        _docker = docker;
        _spaces = spaces;
        _logger = logger;
    }

    public WebSpaceUtilizationResponse Get(Guid uuid)
    {
        if (_cache.TryGetValue(uuid, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
            return cached.Value;

        var result = Build(uuid);
        _cache[uuid] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>Uncached snapshot for live WebSocket stats.</summary>
    public WebSpaceUtilizationResponse GetFresh(Guid uuid) => Build(uuid);

    public void Invalidate(Guid uuid) => _cache.TryRemove(uuid, out _);

    private WebSpaceUtilizationResponse Build(Guid uuid)
    {
        var space = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var response = _spaces.ToResponse(space);

        double? cpu = null;
        long? memUsed = null;
        long? memLimit = null;
        long? netRx = null;
        long? netTx = null;
        var runtimeState = space.State;

        if (WebSpaceRuntime.NeedsContainer(space.Runtime))
        {
            try
            {
                using var client = DockerClientFactory.Create(_docker);
                var containerRef = WebSpaceRuntime.RuntimeName(uuid);
                try
                {
                    var inspect = client.Containers.InspectContainerAsync(containerRef, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    runtimeState = inspect.State?.Running == true ? WebSpaceState.Running : WebSpaceState.Stopped;
                }
                catch (DockerContainerNotFoundException)
                {
                    runtimeState = WebSpaceState.Stopped;
                }
                catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    runtimeState = WebSpaceState.Stopped;
                }

                if (string.Equals(runtimeState, WebSpaceState.Running, StringComparison.OrdinalIgnoreCase))
                {
                    var stats = ReadContainerStats(client, containerRef);
                    if (stats is not null)
                    {
                        cpu = ParseCpuPercent(stats);
                        memUsed = (long?)stats.MemoryStats?.Usage;
                        memLimit = (long?)stats.MemoryStats?.Limit;
                        netRx = stats.Networks?.Values.Sum(n => (long)n.RxBytes);
                        netTx = stats.Networks?.Values.Sum(n => (long)n.TxBytes);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Docker stats can be slow; disk stats still returned.
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"utilization stats {uuid}: {ex.Message}");
            }
        }

        return new WebSpaceUtilizationResponse(
            uuid,
            response.DiskLimitBytes,
            response.DiskUsedBytes,
            cpu,
            memUsed,
            memLimit,
            netRx,
            netTx,
            space.BandwidthLimitBytes,
            space.BandwidthUsedBytes,
            space.IsBandwidthOverQuota(),
            runtimeState);
    }

    /// <summary>Wings-compatible JSON payload for WebSocket <c>stats</c> events.</summary>
    public static string ToWsStatsJson(WebSpaceUtilizationResponse stats)
    {
        var payload = new Dictionary<string, object?>
        {
            ["disk_bytes"] = stats.DiskUsedBytes,
            ["disk_limit_bytes"] = stats.DiskLimitBytes,
            ["state"] = stats.State,
        };

        if (stats.CpuPercent.HasValue)
            payload["cpu_absolute"] = Math.Round(stats.CpuPercent.Value, 2);

        if (stats.MemoryUsedBytes.HasValue)
            payload["memory_bytes"] = stats.MemoryUsedBytes.Value;

        if (stats.MemoryLimitBytes.HasValue)
            payload["memory_limit_bytes"] = stats.MemoryLimitBytes.Value;

        payload["network"] = new
        {
            rx_bytes = stats.NetworkRxBytes ?? 0L,
            tx_bytes = stats.NetworkTxBytes ?? 0L,
        };

        if (stats.BandwidthLimitBytes > 0)
            payload["bandwidth_limit_bytes"] = stats.BandwidthLimitBytes;

        payload["bandwidth_used_bytes"] = stats.BandwidthUsedBytes;
        payload["bandwidth_over_quota"] = stats.BandwidthOverQuota;

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private static ContainerStatsResponse? ReadContainerStats(DockerClient client, string containerRef)
    {
        var first = ReadContainerStatsOnce(client, containerRef);
        if (first is null)
            return null;

        if (ParseCpuPercent(first) is not null)
            return first;

        Thread.Sleep(500);
        var second = ReadContainerStatsOnce(client, containerRef);
        if (second is null)
            return first;

        if (second.PreCPUStats?.SystemUsage is null && first.CPUStats?.SystemUsage is not null)
            second.PreCPUStats = first.CPUStats;

        return second;
    }

    private static ContainerStatsResponse? ReadContainerStatsOnce(DockerClient client, string containerRef)
    {
        using var stream = client.Containers.GetContainerStatsAsync(
            containerRef,
            new ContainerStatsParameters { Stream = false },
            CancellationToken.None).GetAwaiter().GetResult();

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return DeserializeContainerStats(json);
    }

    internal static ContainerStatsResponse? DeserializeContainerStats(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonConvert.DeserializeObject<ContainerStatsResponse>(json);

    internal static double? ParseCpuPercent(ContainerStatsResponse stats)
    {
        if (stats.CPUStats?.SystemUsage is null || stats.PreCPUStats?.SystemUsage is null)
            return null;

        if (stats.CPUStats.CPUUsage?.TotalUsage is null || stats.PreCPUStats.CPUUsage?.TotalUsage is null)
            return null;

        var cpuDelta = (double)(stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage);
        var systemDelta = (double)(stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage);
        if (systemDelta <= 0)
            return null;

        var online = stats.CPUStats.OnlineCPUs;
        if (online == 0)
            online = 1;
        return cpuDelta / systemDelta * online * 100.0;
    }
}
