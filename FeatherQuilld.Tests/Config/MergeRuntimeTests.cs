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

    [Fact]
    public void MergeRuntimeYaml_OmitsApi_PreservesLocalPortSslAndOrigins()
    {
        var local = new AppConfig
        {
            Api =
            {
                Port = 9443,
                AllowedOrigins = ["https://panel.example"],
                Ssl = { Enabled = true, Cert = "/etc/ssl/cert.pem", Key = "/etc/ssl/key.pem" },
                UploadLimit = 100,
            },
            Remote =
            {
                Panel = "https://panel.example",
                ConfigPath = "/api/quilld-remote/config",
                HealthPath = "/api/quilld-remote/health",
            },
        };

        local.MergeRuntimeYaml("""
            remote:
              timeout: 120
              retry_limit: 2
              app_name: FromPanel
            system:
              proxy:
                enabled: true
                provider: nginx
            """);

        Assert.Equal(9443, local.Api.Port);
        Assert.True(local.Api.Ssl.Enabled);
        Assert.Equal("/etc/ssl/cert.pem", local.Api.Ssl.Cert);
        Assert.Equal("/etc/ssl/key.pem", local.Api.Ssl.Key);
        Assert.Equal(["https://panel.example"], local.Api.AllowedOrigins);
        Assert.Equal(120, local.Remote.Timeout);
        Assert.Equal(2, local.Remote.RetryLimit);
        Assert.Equal("FromPanel", local.Remote.AppName);
        Assert.True(local.System.Proxy.Enabled);
        Assert.Equal("nginx", local.System.Proxy.Provider);
    }

    [Fact]
    public void MergeRuntimeYaml_OmitsDiskLimiter_PreservesLocalNone()
    {
        var local = new AppConfig
        {
            System =
            {
                DiskLimiterMode = "none",
                Quotas = { Enabled = false },
                Proxy = { Enabled = false, Provider = "none" },
            },
        };

        local.MergeRuntimeYaml("""
            system:
              proxy:
                enabled: true
                provider: nginx
              backups:
                provider: s3
            """);

        Assert.Equal("none", local.System.DiskLimiterMode);
        Assert.False(local.System.Quotas.Enabled);
        Assert.True(local.System.Proxy.Enabled);
        Assert.Equal("nginx", local.System.Proxy.Provider);
        Assert.Equal("s3", local.System.Backups.Provider);
    }

    [Fact]
    public void MergeRuntimeYaml_PresentOverrides_WinOverLocal()
    {
        var local = new AppConfig
        {
            Api =
            {
                Port = 9443,
                AllowedOrigins = ["https://old.example"],
                Ssl = { Enabled = false },
            },
            System = { DiskLimiterMode = "fuse_quota" },
        };

        local.MergeRuntimeYaml("""
            api:
              port: 8989
              allowed_origins:
                - https://panel.example
              ssl:
                enabled: true
                cert: /etc/letsencrypt/live/x/fullchain.pem
                key: /etc/letsencrypt/live/x/privkey.pem
            system:
              disk_limiter_mode: none
            """);

        Assert.Equal(8989, local.Api.Port);
        Assert.Equal(["https://panel.example"], local.Api.AllowedOrigins);
        Assert.True(local.Api.Ssl.Enabled);
        Assert.Equal("/etc/letsencrypt/live/x/fullchain.pem", local.Api.Ssl.Cert);
        Assert.Equal("none", local.System.DiskLimiterMode);
    }

    [Fact]
    public void DeepMergeMaps_RecursesNestedAndReplacesSequences()
    {
        var target = new Dictionary<string, object?>
        {
            ["api"] = new Dictionary<string, object?>
            {
                ["port"] = 9443,
                ["host"] = "0.0.0.0",
            },
            ["list"] = new List<object?> { "a" },
        };
        var source = new Dictionary<string, object?>
        {
            ["api"] = new Dictionary<string, object?>
            {
                ["port"] = 80,
            },
            ["list"] = new List<object?> { "b", "c" },
            ["new"] = "yes",
        };

        AppConfig.DeepMergeMaps(target, source);

        var api = Assert.IsType<Dictionary<string, object?>>(target["api"]);
        Assert.Equal(80, Convert.ToInt32(api["port"]));
        Assert.Equal("0.0.0.0", api["host"]);
        Assert.Equal(new List<object?> { "b", "c" }, Assert.IsType<List<object?>>(target["list"]));
        Assert.Equal("yes", target["new"]);
    }
}
