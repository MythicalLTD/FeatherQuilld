using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Tests.Config;

public class SystemConfigTests
{
    [Fact]
    public void EffectiveDiskLimiter_FuseByDefault()
    {
        var sys = new SystemConfig();
        Assert.Equal(DiskLimiterModeKind.FuseQuota, sys.EffectiveDiskLimiterMode);
        Assert.True(sys.Quotas.Enabled);
    }

    [Fact]
    public void EffectiveDiskLimiter_NoneWhenExplicitlyDisabled()
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
    public void EffectiveDiskLimiter_NoneIgnoresQuotasEnabled()
    {
        var sys = new SystemConfig { DiskLimiterMode = "none", Quotas = { Enabled = true } };
        Assert.Equal(DiskLimiterModeKind.None, sys.EffectiveDiskLimiterMode);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("disabled")]
    public void EffectiveDiskLimiter_NoneAliases(string mode)
    {
        var sys = new SystemConfig { DiskLimiterMode = mode, Quotas = { Enabled = true } };
        Assert.Equal(DiskLimiterModeKind.None, sys.EffectiveDiskLimiterMode);
    }
}
