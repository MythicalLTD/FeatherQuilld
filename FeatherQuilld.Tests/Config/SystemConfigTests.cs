using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Tests.Config;

public class SystemConfigTests
{
    [Fact]
    public void EffectiveDiskLimiter_NoneByDefault()
    {
        var sys = new SystemConfig { DiskLimiterMode = "none", Quotas = { Enabled = false } };
        Assert.Equal(DiskLimiterModeKind.None, sys.EffectiveDiskLimiterMode);
    }

    [Fact]
    public void EffectiveDiskLimiter_FuseWhenModeSet()
    {
        var sys = new SystemConfig { DiskLimiterMode = "fuse_quota" };
        Assert.Equal(DiskLimiterModeKind.FuseQuota, sys.EffectiveDiskLimiterMode);
    }

    [Fact]
    public void EffectiveDiskLimiter_FuseWhenQuotasEnabled()
    {
        var sys = new SystemConfig { DiskLimiterMode = "none", Quotas = { Enabled = true } };
        Assert.Equal(DiskLimiterModeKind.FuseQuota, sys.EffectiveDiskLimiterMode);
    }
}
