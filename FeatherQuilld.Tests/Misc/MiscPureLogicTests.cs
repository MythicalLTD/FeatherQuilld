using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.Web;
using FeatherQuilld.Utils;
using FeatherQuilld.Utils.WebSpaces.Disk;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Docker;

namespace FeatherQuilld.Tests.Misc;

public class DaemonStateTests
{
    [Fact]
    public void Healthy_WhenNotInMaintenance()
    {
        var state = new DaemonState();
        Assert.True(state.IsHealthy);
        Assert.Equal("healthy", state.HealthStatus);
        Assert.True(state.UptimeSeconds >= 0);
    }

    [Fact]
    public void Unhealthy_WhenMaintenance()
    {
        var state = new DaemonState { MaintenanceMode = true };
        Assert.False(state.IsHealthy);
        Assert.Equal("unhealthy", state.HealthStatus);
    }
}

public class BootStepResultTests
{
    [Fact]
    public void Merge_FailedWins()
    {
        var merged = BootStepResult.Merge(
            new BootStepResult { Status = BootStepStatus.Success },
            new BootStepResult { Status = BootStepStatus.Failed });
        Assert.Equal(BootStepStatus.Failed, merged.Status);
    }

    [Fact]
    public void Merge_WarningOverSuccess()
    {
        var merged = BootStepResult.Merge(
            new BootStepResult { Status = BootStepStatus.Success },
            new BootStepResult { Status = BootStepStatus.Warning });
        Assert.Equal(BootStepStatus.Warning, merged.Status);
    }

    [Fact]
    public void Merge_AllSkipped()
    {
        var merged = BootStepResult.Merge(
            new BootStepResult { Status = BootStepStatus.Skipped },
            new BootStepResult { Status = BootStepStatus.Skipped });
        Assert.Equal(BootStepStatus.Skipped, merged.Status);
    }
}

public class HomePageTests
{
    [Fact]
    public void Render_IncludesAppNameAndVersion()
    {
        var html = HomePage.Render("FeatherQuilld", "0.1.0", docsEnabled: true);
        Assert.Contains("FeatherQuilld", html);
        Assert.Contains("0.1.0", html);
        Assert.Contains("scalar", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_DocsDisabled_OmitsDocsLinkishNoiseStillHtml()
    {
        var html = HomePage.Render("X", "1.0.0", docsEnabled: false);
        Assert.Contains("X", html);
        Assert.DoesNotContain("/scalar", html, StringComparison.OrdinalIgnoreCase);
    }
}

public class ColoredConsoleTests
{
    [Fact]
    public void StripCodes_RemovesAmpCodes()
    {
        var plain = ColoredConsole.StripCodes("&aHello&r &lWorld");
        Assert.DoesNotContain("&a", plain);
        Assert.Contains("Hello", plain);
        Assert.Contains("World", plain);
    }
}

public class FuseQuotaPathTests
{
    [Fact]
    public void GetMountPath_UsesVmountAndUuid()
    {
        var uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var sys = new SystemConfig { VmountDirectory = "/tmp/vmounts" };
        var path = FuseQuotaLimiter.GetMountPath(sys, uuid);
        Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", path);
        Assert.StartsWith("/tmp/vmounts", path);
    }
}

public class WebSpaceInstallerPathTests
{
    [Fact]
    public void InstallLogPath_UnderData()
    {
        var path = WebSpaceInstaller.InstallLogPath("/data/space");
        Assert.Equal(Path.Combine("/data/space", ".install", "install.log"), path);
    }
}
