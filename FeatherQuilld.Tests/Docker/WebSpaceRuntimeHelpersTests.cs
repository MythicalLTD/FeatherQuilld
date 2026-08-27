using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.Docker;

public class WebSpaceRuntimeHelpersTests
{
    [Theory]
    [InlineData("static", false)]
    [InlineData("STATIC", false)]
    [InlineData("node", true)]
    [InlineData("php", true)]
    [InlineData("python", true)]
    [InlineData("custom", true)]
    public void NeedsContainer(string runtime, bool expected) =>
        Assert.Equal(expected, WebSpaceRuntime.NeedsContainer(runtime));

    [Theory]
    [InlineData("node", 0, 3000)]
    [InlineData("python", 0, 8000)]
    [InlineData("php", 0, 80)]
    [InlineData("node", 8080, 8080)]
    public void DefaultContainerPort(string runtime, int plate, int expected) =>
        Assert.Equal(expected, WebSpaceRuntime.DefaultContainerPort(runtime, plate));

    [Theory]
    [InlineData("php", "/var/www/html")]
    [InlineData("node", "/home/container")]
    [InlineData("static", "/home/container")]
    public void MountTarget(string runtime, string expected) =>
        Assert.Equal(expected, WebSpaceRuntime.MountTarget(runtime));

    [Fact]
    public void RuntimeName_IsUuidString()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal(id.ToString(), WebSpaceRuntime.RuntimeName(id));
    }
}

public class PortAllocatorTests
{
    [Fact]
    public void Allocate_PreferredWhenFree()
    {
        var allocator = new PortAllocator(new ProxyConfig { BackendPortMin = 29100, BackendPortMax = 29110 });
        var port = allocator.Allocate([], preferred: 29105);
        Assert.Equal(29105, port);
    }

    [Fact]
    public void Allocate_SkipsUsedPreferred()
    {
        var allocator = new PortAllocator(new ProxyConfig { BackendPortMin = 29200, BackendPortMax = 29205 });
        var used = new[] { new WebSpace { BackendPort = 29200 } };
        var port = allocator.Allocate(used, preferred: 29200);
        Assert.NotEqual(29200, port);
        Assert.InRange(port, 29201, 29205);
    }
}
