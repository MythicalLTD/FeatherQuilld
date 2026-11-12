using FeatherQuilld.Utils.Proxy;

namespace FeatherQuilld.Tests.Proxy;

public class CaddyReloadGuardTests
{
    [Theory]
    [InlineData("Error: sending configuration to instance: performing request: Post \"http://localhost:2019/load\": dial tcp localhost:2019: connect: connection refused")]
    [InlineData("no such file or directory")]
    [InlineData("caddy is not running")]
    public void ReloadFailure_AdminApiUnreachable_IsRecognised(string output)
    {
        Assert.True(ReverseProxyManager.ReloadFailureMeansNotRunning(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("adapting config using caddyfile: parsing caddyfile: unexpected character")]
    [InlineData("HTTP 500 invalid configuration")]
    public void ReloadFailure_ConfigError_IsNotTreatedAsNotRunning(string output)
    {
        Assert.False(ReverseProxyManager.ReloadFailureMeansNotRunning(output));
    }

    [Fact]
    public void ShouldStartCaddy_NothingRunning_Starts()
    {
        Assert.True(ReverseProxyManager.ShouldStartCaddy("connection refused", 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ShouldStartCaddy_AlreadyRunning_NeverStartsASecondInstance(int running)
    {
        // Two Caddy processes can share the listen ports and the admin port (SO_REUSEPORT): reloads then
        // reach only one of them and the other keeps serving an outdated config.
        Assert.False(ReverseProxyManager.ShouldStartCaddy("connection refused", running));
    }

    [Fact]
    public void ShouldStartCaddy_UnrelatedFailure_DoesNotStart()
    {
        Assert.False(ReverseProxyManager.ShouldStartCaddy("invalid configuration", 0));
    }
}
