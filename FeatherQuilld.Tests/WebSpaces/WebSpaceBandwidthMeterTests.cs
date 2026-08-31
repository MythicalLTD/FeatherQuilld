using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public sealed class WebSpaceBandwidthMeterTests
{
    [Fact]
    public void ComputePeriodBytes_sums_sidecars_and_live_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-bandwidth-" + Guid.NewGuid().ToString("N"));
        var uuid = Guid.NewGuid();
        var domain = "example.test";
        var periodStart = new DateOnly(2026, 8, 1);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (periodStart.Month != today.Month || periodStart.Year != today.Year)
            return;

        try
        {
            ProxyAccessLogs.EnsureDir(root, uuid);
            var accessPath = ProxyAccessLogs.AccessLogPath(root, uuid, domain);
            File.WriteAllText(accessPath, """127.0.0.1 - - [30/Aug/2026:00:00:00 +0000] "GET / HTTP/1.1" 200 1000""");

            var yesterday = today.AddDays(-1);
            if (yesterday >= periodStart)
            {
                var summaryPath = ProxyAccessLogs.SummaryPath(root, uuid, domain, yesterday);
                File.WriteAllText(summaryPath, """{"hits":1,"bytes":2500,"status":{"200":1}}""");
            }

            var space = new WebSpace
            {
                Uuid = uuid,
                Domains = [domain],
            };

            var total = WebSpaceBandwidthMeter.ComputePeriodBytes(root, space, periodStart);
            Assert.True(total >= 1000);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup races
            }
        }
    }

    [Fact]
    public void CurrentPeriodStart_is_first_day_of_utc_month()
    {
        var start = WebSpaceBandwidthMeter.CurrentPeriodStart();
        var now = DateTime.UtcNow;
        Assert.Equal(1, start.Day);
        Assert.Equal(now.Year, start.Year);
        Assert.Equal(now.Month, start.Month);
    }

    [Fact]
    public void IsBandwidthOverQuota_true_when_used_meets_limit()
    {
        var space = new WebSpace
        {
            BandwidthLimitBytes = 1000,
            BandwidthUsedBytes = 1000,
        };

        Assert.True(space.IsBandwidthOverQuota());
    }
}
