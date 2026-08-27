using FeatherQuilld.Utils.Config;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Config;

public class MergeRuntimeTests
{
    [Fact]
    public void MergeRuntime_PreservesLocalPanelAndPaths()
    {
        var local = new AppConfig
        {
            Uuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TokenId = "local-token-id",
            Token = "local-token-secret-value-with-enough-length",
            Remote =
            {
                Panel = "http://127.0.0.1:8721",
                ConfigPath = "/api/quilld-remote/config",
                HealthPath = "/api/quilld-remote/health",
                Timeout = 30,
                RetryLimit = 10,
            },
        };

        var runtime = new AppConfig
        {
            Uuid = Guid.NewGuid(),
            TokenId = "runtime-id",
            Token = "runtime-token",
            Remote =
            {
                Panel = "https://testingpanel.mythical.systems",
                ConfigPath = "/api/other/config",
                HealthPath = "/api/other/health",
                Timeout = 90,
                RetryLimit = 3,
                AppName = "FromPanel",
                CustomHeaders = new Dictionary<string, string> { ["X-Test"] = "1" },
            },
        };

        local.MergeRuntime(runtime);

        Assert.Equal("http://127.0.0.1:8721", local.Remote.Panel);
        Assert.Equal("/api/quilld-remote/config", local.Remote.ConfigPath);
        Assert.Equal("/api/quilld-remote/health", local.Remote.HealthPath);
        Assert.Equal(90, local.Remote.Timeout);
        Assert.Equal(3, local.Remote.RetryLimit);
        Assert.Equal("FromPanel", local.Remote.AppName);
        Assert.Equal("1", local.Remote.CustomHeaders["X-Test"]);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), local.Uuid);
        Assert.Equal("local-token-id", local.TokenId);
        Assert.Equal("local-token-secret-value-with-enough-length", local.Token);
    }

    [Fact]
    public void MergeRuntime_EmptyLocalPanel_AllowsRuntimePanel()
    {
        var local = new AppConfig
        {
            Remote = { Panel = "  ", ConfigPath = "", HealthPath = "" },
        };
        var runtime = new AppConfig
        {
            Remote =
            {
                Panel = "https://panel.example",
                ConfigPath = "/api/quilld-remote/config",
                HealthPath = "/api/quilld-remote/health",
            },
        };

        local.MergeRuntime(runtime);

        Assert.Equal("https://panel.example", local.Remote.Panel);
        Assert.Equal("/api/quilld-remote/config", local.Remote.ConfigPath);
        Assert.Equal("/api/quilld-remote/health", local.Remote.HealthPath);
    }
}
