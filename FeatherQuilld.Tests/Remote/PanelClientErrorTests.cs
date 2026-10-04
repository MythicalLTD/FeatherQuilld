using FeatherQuilld.Utils.Remote;

namespace FeatherQuilld.Tests.Remote;

public class PanelClientErrorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The api route does not exist! [/api/quilld-remote/webspaces]")]
    [InlineData("Panel route not found")]
    public void FormatNotFoundMessage_MissingRoute_HintsConfigPaths(string? panelMessage)
    {
        var message = PanelClient.FormatNotFoundMessage(
            "/api/quilld-remote/config",
            panelMessage,
            panelMessage ?? "…");

        Assert.Contains("Panel route not found (404)", message);
        Assert.Contains("remote.config_path", message);
    }

    [Fact]
    public void FormatNotFoundMessage_MissingResource_IncludesPanelDetail()
    {
        var message = PanelClient.FormatNotFoundMessage(
            "/api/quilld-remote/webspaces/abc",
            "WebSpace not found",
            "WebSpace not found");

        Assert.Contains("Panel resource not found (404)", message);
        Assert.Contains("WebSpace not found", message);
        Assert.DoesNotContain("remote.config_path", message);
    }
}
