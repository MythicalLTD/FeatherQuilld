using FeatherQuilld.Utils.WebSpaces;
using Xunit;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceUsageHistoryTests
{
    private static WebSpaceUtilizationResponse Stats(long disk, long diskLimit) =>
        new(Guid.Empty, diskLimit, disk, null, null, null, null, null, 0, 0, false, "running");

    [Fact]
    public void Add_KeepsOnlyCapacitySamples()
    {
        var h = new WebSpaceUsageHistory(3);
        var id = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            h.Add(id, new WebSpaceUsageSample(DateTimeOffset.UtcNow.AddMinutes(i), i, null, i, null, null, 0));
        var samples = h.Get(id);
        Assert.Equal(3, samples.Count);
        Assert.Equal(2, samples[0].DiskUsedBytes);
    }

    [Fact]
    public void CheckLimits_FiresOnceUntilRecovered()
    {
        var h = new WebSpaceUsageHistory();
        var id = Guid.NewGuid();
        Assert.Empty(h.CheckLimits(id, Stats(10, 100)));
        Assert.Single(h.CheckLimits(id, Stats(95, 100)));
        Assert.Empty(h.CheckLimits(id, Stats(99, 100)));
        Assert.Empty(h.CheckLimits(id, Stats(10, 100)));
        Assert.Single(h.CheckLimits(id, Stats(91, 100)));
    }
}
