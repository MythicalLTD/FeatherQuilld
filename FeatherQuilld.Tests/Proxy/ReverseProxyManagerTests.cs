using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Proxy;

public class ReverseProxyManagerTests
{
    [Fact]
    public void BuildConfig_Traefik_EmitsHostRouterAndService()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = Path.Combine(root, "data"),
                    Proxy = new ProxyConfig
                    {
                        Enabled = true,
                        Provider = "traefik",
                        AcmeEmail = "ops@example.com",
                    },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Domains = ["app.example.com", "www.example.com"],
                Ssl = true,
                BackendPort = 20123,
                DocumentRoot = "public",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var yaml = mgr.BuildConfig([space]);

            Assert.Contains("traefik", mgr.NormalizedProvider);
            Assert.Contains("Host(`app.example.com`)", yaml);
            Assert.Contains("Host(`www.example.com`)", yaml);
            Assert.Contains("http://127.0.0.1:20123", yaml);
            Assert.Contains("certResolver: featherquilld", yaml);
            Assert.Contains("websecure", yaml);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Traefik_SkipsStaticWithoutBackendPort()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = "/tmp",
                Proxy = new ProxyConfig { Enabled = true, Provider = "traefik" },
            },
        };

        var mgr = new ReverseProxyManager(config);
        var space = new WebSpace
        {
            Uuid = Guid.NewGuid(),
            Domains = ["static.example.com"],
            Ssl = false,
            BackendPort = 0,
            Runtime = "static",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var yaml = mgr.BuildConfig([space]);
        Assert.DoesNotContain("static.example.com", yaml);
        Assert.Contains("No WebSpaces", yaml);
    }

    [Fact]
    public void BuildConfig_Traefik_StaticWithPort_EmitsHostRouter()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = Path.Combine(root, "data"),
                    Proxy = new ProxyConfig
                    {
                        Enabled = true,
                        Provider = "traefik",
                    },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Domains = ["static.example.com"],
                Ssl = false,
                BackendPort = 21001,
                Runtime = "static",
                DocumentRoot = "public",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var yaml = mgr.BuildConfig([space]);

            Assert.Contains("Host(`static.example.com`)", yaml);
            Assert.Contains("http://127.0.0.1:21001", yaml);
            Assert.Contains("web", yaml);
            Assert.DoesNotContain("featherquilld-placeholder", yaml);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void NormalizedProvider_DefaultsUnknownToCaddy()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Proxy = new ProxyConfig { Provider = "weird" },
            },
        };

        Assert.Equal("caddy", new ReverseProxyManager(config).NormalizedProvider);
    }
}
