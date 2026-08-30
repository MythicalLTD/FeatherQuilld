using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Docker;

namespace FeatherQuilld.Tests.Docker;

public class DockerNetworkEnsurerTests
{
    [Theory]
    [InlineData("bridge", false)]
    [InlineData("host", false)]
    [InlineData("none", false)]
    [InlineData("default", false)]
    [InlineData("container:abc", false)]
    [InlineData("featherquilld_nw", true)]
    [InlineData("custom_net", true)]
    public void ShouldEnsure_respects_network_mode(string mode, bool expected)
    {
        var config = new DockerConfig { Network = { NetworkMode = mode } };
        Assert.Equal(expected, DockerNetworkEnsurer.ShouldEnsure(config));
    }

    [Theory]
    [InlineData("bridge", null)]
    [InlineData("host", null)]
    [InlineData("none", null)]
    [InlineData("container:deadbeef", null)]
    [InlineData("featherquilld_nw", "featherquilld_nw")]
    [InlineData("  my_net  ", "my_net")]
    public void ResolveNetworkName(string mode, string? expected)
    {
        var config = new DockerConfig { Network = { NetworkMode = mode } };
        Assert.Equal(expected, DockerNetworkEnsurer.ResolveNetworkName(config));
    }
}
