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
            Assert.Contains("redirectScheme:", yaml);
            Assert.Contains("scheme: https", yaml);
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
    public void BuildConfig_Caddy_EmitsRedirectBlock()
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
                        Provider = "caddy",
                    },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Domains = ["app.example.com", "legacy.example.com"],
                DomainRoutes =
                [
                    new WebSpaceDomainRoute { Domain = "app.example.com", Type = "primary" },
                    new WebSpaceDomainRoute
                    {
                        Domain = "legacy.example.com",
                        Type = "redirect",
                        RedirectTarget = "https://app.example.com",
                    },
                ],
                Ssl = true,
                BackendPort = 20123,
                DocumentRoot = "public",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var caddyfile = mgr.BuildConfig([space]);

            Assert.Contains("app.example.com", caddyfile);
            Assert.Contains("legacy.example.com", caddyfile);
            Assert.Contains("redir https://app.example.com", caddyfile);
            Assert.Contains("reverse_proxy 127.0.0.1:20123", caddyfile);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Traefik_EmitsRedirectRouter()
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
                Uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Domains = ["app.example.com", "legacy.example.com"],
                DomainRoutes =
                [
                    new WebSpaceDomainRoute { Domain = "app.example.com", Type = "primary" },
                    new WebSpaceDomainRoute
                    {
                        Domain = "legacy.example.com",
                        Type = "redirect",
                        RedirectTarget = "https://app.example.com",
                    },
                ],
                Ssl = true,
                BackendPort = 20123,
                DocumentRoot = "public",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var yaml = mgr.BuildConfig([space]);

            Assert.Contains("Host(`app.example.com`)", yaml);
            Assert.Contains("Host(`legacy.example.com`)", yaml);
            Assert.Contains("redirectRegex:", yaml);
            Assert.Contains("https://app.example.com${1}", yaml);
            Assert.Contains("featherquilld-redirect-sink", yaml);
            Assert.Contains("http://127.0.0.1:20123", yaml);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Nginx_WafEnabled_EmitsSecurityHeaders()
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
                        Provider = "nginx",
                    },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.NewGuid(),
                Domains = ["secure.example.com"],
                Ssl = true,
                WafEnabled = true,
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var nginx = mgr.BuildConfig([space]);

            Assert.Contains("Strict-Transport-Security", nginx);
            Assert.Contains("X-Content-Type-Options nosniff", nginx);
            Assert.Contains("X-Frame-Options SAMEORIGIN", nginx);
            Assert.Contains("Referrer-Policy", nginx);
            Assert.Contains("client_max_body_size 10m", nginx);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Traefik_WafEnabled_EmitsSecurityMiddleware()
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
                Uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Domains = ["app.example.com"],
                Ssl = true,
                WafEnabled = true,
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var yaml = mgr.BuildConfig([space]);

            Assert.Contains("stsSeconds: 31536000", yaml);
            Assert.Contains("contentTypeNosniff: true", yaml);
            Assert.Contains("customFrameOptionsValue: SAMEORIGIN", yaml);
            Assert.Contains("referrerPolicy: strict-origin-when-cross-origin", yaml);
            Assert.Contains("maxRequestBodyBytes: 10485760", yaml);
            Assert.Contains("-waf", yaml);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Caddy_UsesCustomBackendHost()
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
                        Provider = "caddy",
                        BackendHost = "10.0.0.5",
                    },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.NewGuid(),
                Domains = ["app.example.com"],
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var caddyfile = mgr.BuildConfig([space]);
            Assert.Contains("reverse_proxy 10.0.0.5:20123", caddyfile);
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

    [Fact]
    public void BuildConfig_Caddy_EmitsPerSiteTlsEmail()
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
                        Provider = "caddy",
                        AcmeEmail = "ops@example.com",
                    },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.NewGuid(),
                Domains = ["app.example.com"],
                Ssl = true,
                AcmeEmail = "owner@example.com",
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var caddyfile = mgr.BuildConfig([space]);
            Assert.Contains("email ops@example.com", caddyfile);
            Assert.Contains("tls owner@example.com", caddyfile);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Caddy_UsesNodeEmailWhenSpaceHasNone()
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
                        Provider = "caddy",
                        AcmeEmail = "ops@example.com",
                    },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.NewGuid(),
                Domains = ["app.example.com"],
                Ssl = true,
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var caddyfile = mgr.BuildConfig([space]);
            Assert.Contains("email ops@example.com", caddyfile);
            Assert.DoesNotContain("tls ops@example.com", caddyfile);
            Assert.DoesNotContain("tls owner@", caddyfile);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void AccountFileName_IsStableAndPerEmail()
    {
        var a = NginxAcmeService.AccountFileName("Owner@Example.com", staging: false);
        var b = NginxAcmeService.AccountFileName("owner@example.com", staging: false);
        var c = NginxAcmeService.AccountFileName("other@example.com", staging: false);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.EndsWith(".pem", a);
        Assert.Contains("-staging", NginxAcmeService.AccountFileName("owner@example.com", staging: true));
    }

    [Fact]
    public void BuildConfig_Caddy_PerRouteDocumentRootAndAccessLogAndDeny()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            var uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = data,
                    DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
                },
            };
            config.System.Quotas.Enabled = false;

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = uuid,
                Runtime = "static",
                Domains = ["app.example.com", "blog.example.com"],
                DomainRoutes =
                [
                    new WebSpaceDomainRoute { Domain = "app.example.com", Type = "primary", DocumentRoot = "public" },
                    new WebSpaceDomainRoute { Domain = "blog.example.com", Type = "alias", DocumentRoot = "sites/blog" },
                ],
                Ssl = false,
                WafEnabled = true,
                WafDenyIps = ["203.0.113.10", "198.51.100.0/24"],
                BackendPort = 0,
                DocumentRoot = "public",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var caddyfile = mgr.BuildConfig([space]);
            Assert.Contains($"root * {Path.Combine(data, uuid.ToString(), "public")}", caddyfile);
            Assert.Contains($"root * {Path.Combine(data, uuid.ToString(), "sites/blog")}", caddyfile);
            Assert.Contains("blog.example.com.access.log", caddyfile);
            Assert.Contains("@denied remote_ip 203.0.113.10 198.51.100.0/24", caddyfile);
            Assert.Contains("respond @denied 403", caddyfile);
            Assert.Contains("format json", caddyfile);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Caddy_WafDenyPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            var uuid = Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee");
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = data,
                    DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
                },
            };
            config.System.Quotas.Enabled = false;

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = uuid,
                Runtime = "static",
                Domains = ["deny.example.com"],
                Ssl = false,
                WafEnabled = true,
                WafDenyPaths = ["/xmlrpc.php", "/secret"],
                BackendPort = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var caddyfile = mgr.BuildConfig([space]);
            Assert.Contains("@deniedpath path /xmlrpc.php /secret", caddyfile);
            Assert.Contains("respond @deniedpath 403", caddyfile);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Nginx_WafDenyOnHttpAndAccessLog()
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
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Domains = ["secure.example.com"],
                Ssl = true,
                WafEnabled = true,
                WafDenyIps = ["203.0.113.8"],
                WafDenyPaths = ["/xmlrpc.php"],
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var nginx = mgr.BuildConfig([space]);
            Assert.Contains("deny 203.0.113.8;", nginx);
            Assert.Contains("location ^~ \"/xmlrpc.php\"", nginx);
            Assert.Contains("secure.example.com.access.log", nginx);
            Assert.Contains("listen 80;", nginx);
            Assert.Contains("client_max_body_size 10m;", nginx);
            var httpBlock = nginx.IndexOf("listen 80;", StringComparison.Ordinal);
            var denyAt = nginx.IndexOf("deny 203.0.113.8;", StringComparison.Ordinal);
            Assert.True(denyAt > httpBlock, "WAF deny should apply on HTTP as well as HTTPS");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Nginx_RedirectHost_EmitsHttpsWhenSsl()
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
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.NewGuid(),
                Domains = ["example.com", "www.example.com"],
                DomainRoutes =
                [
                    new WebSpaceDomainRoute { Domain = "example.com", Type = "primary" },
                    new WebSpaceDomainRoute
                    {
                        Domain = "www.example.com",
                        Type = "redirect",
                        RedirectTarget = "https://example.com",
                    },
                ],
                Ssl = true,
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var nginx = mgr.BuildConfig([space]);
            Assert.Contains("server_name www.example.com;", nginx);
            Assert.Contains("listen 443 ssl;", nginx);
            Assert.Contains("return 301 https://example.com$request_uri;", nginx);
            Assert.Equal("example.com", ReverseProxyManager.ResolveApexDomain(space));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Nginx_Dns01_UsesApexCertPaths()
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
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.NewGuid(),
                Domains = ["example.com", "blog.example.com"],
                DomainRoutes =
                [
                    new WebSpaceDomainRoute { Domain = "example.com", Type = "primary" },
                    new WebSpaceDomainRoute { Domain = "blog.example.com", Type = "alias" },
                ],
                Ssl = true,
                SslMode = "dns01",
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var nginx = mgr.BuildConfig([space]);
            Assert.Contains(NginxAcmeService.CertPath("example.com"), nginx);
            Assert.DoesNotContain(NginxAcmeService.CertPath("blog.example.com"), nginx);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ProxyAccessLogs_ParsesCombinedAndJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-logs-" + Guid.NewGuid().ToString("N"));
        var uuid = Guid.NewGuid();
        var dir = ProxyAccessLogs.DirectoryFor(root, uuid);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                ProxyAccessLogs.AccessLogPath(root, uuid, "app.example.com"),
                """
                1.2.3.4 - - [30/Aug/2026:00:00:00 +0000] "GET / HTTP/1.1" 200 1234
                {"status":404,"size":50}
                """);

            var space = new WebSpace { Uuid = uuid, Domains = ["app.example.com"] };
            var result = ProxyAccessLogs.Read(root, space, "app.example.com", 50);
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Assert.Contains("\"hits\":2", json);
            Assert.Contains("\"200\":1", json);
            Assert.Contains("\"404\":1", json);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ProxyAccessLogs_History_WritesDailySummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-hist-" + Guid.NewGuid().ToString("N"));
        var uuid = Guid.NewGuid();
        var dir = ProxyAccessLogs.DirectoryFor(root, uuid);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                ProxyAccessLogs.AccessLogPath(root, uuid, "app.example.com"),
                """
                1.2.3.4 - - [29/Aug/2026:00:00:00 +0000] "GET / HTTP/1.1" 200 100
                1.2.3.4 - - [30/Aug/2026:00:00:00 +0000] "GET / HTTP/1.1" 404 50
                """);

            Assert.Equal(new DateOnly(2026, 8, 29), ProxyAccessLogs.ExtractDate(
                "1.2.3.4 - - [29/Aug/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 100"));

            var space = new WebSpace { Uuid = uuid, Domains = ["app.example.com"] };
            var result = ProxyAccessLogs.Read(root, space, "app.example.com", 50, days: 90);
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Assert.Contains("2026-08-29", json);
            Assert.Contains("2026-08-30", json);
            Assert.True(File.Exists(ProxyAccessLogs.SummaryPath(root, uuid, "app.example.com", new DateOnly(2026, 8, 29))));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
