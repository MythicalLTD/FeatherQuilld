using FeatherQuilld.Utils.Config.Remote;

namespace FeatherQuilld.Tests.Config;

public class RemoteConfigTests
{
    [Theory]
    [InlineData("", "/api/quilld-remote/config")]
    [InlineData("   ", "/api/quilld-remote/config")]
    [InlineData("api/x", "/api/x")]
    [InlineData("/api/x", "/api/x")]
    public void NormalizePath_HandlesEmptyAndRelative(string input, string expected) =>
        Assert.Equal(expected, RemoteConfig.NormalizePath(input));

    [Fact]
    public void ConfigUrl_CombinesPanelAndPath()
    {
        var remote = new RemoteConfig
        {
            Panel = "http://panel.example/",
            ConfigPath = "/api/quilld-remote/config",
        };
        Assert.Equal("http://panel.example/api/quilld-remote/config", remote.ConfigUrl);
        Assert.Equal("http://panel.example/api/quilld-remote/health", remote.HealthUrl);
    }
}
