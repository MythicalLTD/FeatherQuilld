using System.Collections.Concurrent;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Logger;
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
    string State);

/// <summary>Per-WebSpace resource utilization (disk + optional Docker stats).</summary>
public sealed class WebSpaceUtilizationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions StatsJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

    private WebSpaceUtilizationResponse Build(Guid uuid)
    {
        var space = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var response = _spaces.ToResponse(space);

        double? cpu = null;
        long? memUsed = null;
        long? memLimit = null;
        long? netRx = null;
        long? netTx = null;

        if (!string.IsNullOrWhiteSpace(space.ContainerId) && WebSpaceRuntime.NeedsContainer(space.Runtime))
        {
            try
            {
                using var client = DockerClientFactory.Create(_docker);
                using var stream = client.Containers.GetContainerStatsAsync(
                    space.ContainerId,
                    new ContainerStatsParameters { Stream = false },
                    CancellationToken.None).GetAwaiter().GetResult();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var stats = JsonSerializer.Deserialize<ContainerStatsResponse>(json, StatsJson);
                if (stats is not null)
                {
                    cpu = ParseCpuPercent(stats);
                    memUsed = (long?)stats.MemoryStats?.Usage;
                    memLimit = (long?)stats.MemoryStats?.Limit;
                    netRx = stats.Networks?.Values.Sum(n => (long)n.RxBytes);
                    netTx = stats.Networks?.Values.Sum(n => (long)n.TxBytes);
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
            space.State);
    }

    private static double? ParseCpuPercent(ContainerStatsResponse stats)
    {
        if (stats.CPUStats?.SystemUsage is null || stats.PreCPUStats?.SystemUsage is null)
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
