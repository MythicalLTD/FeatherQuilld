using Docker.DotNet.Models;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public sealed class WebSpaceUtilizationServiceTests
{
    private const string SampleStatsJson = """
        {
          "cpu_stats": {
            "cpu_usage": { "total_usage": 792681000 },
            "system_cpu_usage": 1121022960000000,
            "online_cpus": 4
          },
          "precpu_stats": {
            "cpu_usage": { "total_usage": 792664000 },
            "system_cpu_usage": 1121018970000000,
            "online_cpus": 4
          },
          "memory_stats": {
            "usage": 53977088,
            "limit": 2147483648
          },
          "networks": {
            "eth0": {
              "rx_bytes": 14438,
              "tx_bytes": 37631
            }
          }
        }
        """;

    [Fact]
    public void DeserializeContainerStats_maps_docker_snake_case_fields()
    {
        var stats = WebSpaceUtilizationService.DeserializeContainerStats(SampleStatsJson);
        Assert.NotNull(stats);
        Assert.Equal(53977088UL, stats!.MemoryStats?.Usage);
        Assert.Equal(2147483648UL, stats.MemoryStats?.Limit);
        Assert.Equal(14438UL, stats.Networks?["eth0"].RxBytes);
        Assert.Equal(37631UL, stats.Networks?["eth0"].TxBytes);
    }

    [Fact]
    public void ParseCpuPercent_computes_usage_from_precpu_delta()
    {
        var stats = WebSpaceUtilizationService.DeserializeContainerStats(SampleStatsJson);
        Assert.NotNull(stats);

        var cpu = WebSpaceUtilizationService.ParseCpuPercent(stats!);
        Assert.NotNull(cpu);
        Assert.InRange(cpu!.Value, 0, 100);
    }

    [Fact]
    public void ToWsStatsJson_includes_cpu_memory_and_network()
    {
        var response = new WebSpaceUtilizationResponse(
            Guid.NewGuid(),
            DiskLimitBytes: 2_147_483_648,
            DiskUsedBytes: 121_655_296,
            CpuPercent: 1.25,
            MemoryUsedBytes: 53_977_088,
            MemoryLimitBytes: 2_147_483_648,
            NetworkRxBytes: 14_438,
            NetworkTxBytes: 37_631,
            BandwidthLimitBytes: 10L * 1024 * 1024 * 1024,
            BandwidthUsedBytes: 1_073_741_824,
            BandwidthOverQuota: false,
            State: "running");

        var json = WebSpaceUtilizationService.ToWsStatsJson(response);
        Assert.Contains("\"cpu_absolute\":1.25", json);
        Assert.Contains("\"memory_bytes\":53977088", json);
        Assert.Contains("\"bandwidth_used_bytes\":1073741824", json);
        Assert.Contains("\"bandwidth_limit_bytes\":", json);
    }
}
