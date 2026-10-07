using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Docker;

namespace FeatherQuilld.Tests.Docker;

/// <summary>
/// Capability hardening of WebSpace containers. Dropping every capability looks harmless but
/// breaks any plate whose startup installs packages at boot - the built-in PHP bootstrap runs
/// apt-get + docker-php-ext-install and then Apache, all of which need SETUID/SETGID/CHOWN.
/// </summary>
public class WebSpaceContainerSecurityTests
{
    [Fact]
    public void Default_DropsEverythingExceptWhatPackageInstallsNeed()
    {
        var host = new HostConfig();

        WebSpaceRuntime.ApplyContainerSecurity(host, new DockerWebSpaceSecurityConfig());

        Assert.Equal(new[] { "ALL" }, host.CapDrop);
        Assert.Equal(
            DockerWebSpaceSecurityConfig.RequiredByPackageInstalls.ToArray(),
            host.CapAdd.ToArray());
        Assert.Equal(new[] { "no-new-privileges:true" }, host.SecurityOpt);
    }

    [Fact]
    public void Default_KeepsTheCapabilitiesThePhpBootstrapNeeds()
    {
        var kept = new DockerWebSpaceSecurityConfig().Capabilities;

        Assert.Contains("SETUID", kept);
        Assert.Contains("SETGID", kept);
        Assert.Contains("CHOWN", kept);
        Assert.DoesNotContain("NET_RAW", kept);
        Assert.DoesNotContain("SYS_ADMIN", kept);
        Assert.DoesNotContain("ALL", kept);
    }

    [Fact]
    public void EmptyCapabilityList_DropsEveryCapability()
    {
        var host = new HostConfig();
        var security = new DockerWebSpaceSecurityConfig { Capabilities = [] };

        WebSpaceRuntime.ApplyContainerSecurity(host, security);

        Assert.Equal(new[] { "ALL" }, host.CapDrop);
        Assert.True(host.CapAdd is null || host.CapAdd.Count == 0);
        Assert.Equal(new[] { "no-new-privileges:true" }, host.SecurityOpt);
    }

    [Fact]
    public void CustomCapabilityList_IsAppliedVerbatim()
    {
        var host = new HostConfig();
        var security = new DockerWebSpaceSecurityConfig { Capabilities = ["CHOWN", "SETUID"] };

        WebSpaceRuntime.ApplyContainerSecurity(host, security);

        Assert.Equal(new[] { "ALL" }, host.CapDrop);
        Assert.Equal(new[] { "CHOWN", "SETUID" }, host.CapAdd);
    }

    [Fact]
    public void DropCapabilitiesOff_LeavesTheEngineDefaultSet()
    {
        var host = new HostConfig();
        var security = new DockerWebSpaceSecurityConfig { DropCapabilities = false };

        WebSpaceRuntime.ApplyContainerSecurity(host, security);

        Assert.True(host.CapDrop is null || host.CapDrop.Count == 0);
        Assert.True(host.CapAdd is null || host.CapAdd.Count == 0);
        Assert.Equal(new[] { "no-new-privileges:true" }, host.SecurityOpt);
    }

    [Fact]
    public void NoNewPrivilegesOff_LeavesSecurityOptUnset()
    {
        var host = new HostConfig();

        WebSpaceRuntime.ApplyContainerSecurity(
            host,
            new DockerWebSpaceSecurityConfig { NoNewPrivileges = false });

        Assert.True(host.SecurityOpt is null || host.SecurityOpt.Count == 0);
        Assert.Equal(new[] { "ALL" }, host.CapDrop);
    }

    [Fact]
    public void MissingConfig_FallsBackToTheHardenedDefault()
    {
        var host = new HostConfig();

        WebSpaceRuntime.ApplyContainerSecurity(host, null);

        Assert.Equal(new[] { "ALL" }, host.CapDrop);
        Assert.Equal(
            DockerWebSpaceSecurityConfig.RequiredByPackageInstalls.ToArray(),
            host.CapAdd.ToArray());
        Assert.Equal(new[] { "no-new-privileges:true" }, host.SecurityOpt);
    }

    [Fact]
    public void NullHostConfig_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => WebSpaceRuntime.ApplyContainerSecurity(null!, new DockerWebSpaceSecurityConfig()));
}
