using System.Linq;
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
            Assert.DoesNotContain("modsecurity_rules_file", nginx);
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
            Assert.Contains("ws-aaaaaaaabbbb-waf-headers", yaml);
            Assert.Contains("ws-aaaaaaaabbbb-waf-buffer", yaml);
            // One middleware type per id: headers block must not nest buffering.
            var headersIdx = yaml.IndexOf("ws-aaaaaaaabbbb-waf-headers:", StringComparison.Ordinal);
            var bufferIdx = yaml.IndexOf("ws-aaaaaaaabbbb-waf-buffer:", StringComparison.Ordinal);
            Assert.True(headersIdx >= 0 && bufferIdx > headersIdx);
            var headersBlock = yaml[headersIdx..bufferIdx];
            Assert.Contains("headers:", headersBlock);
            Assert.DoesNotContain("buffering:", headersBlock);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Traefik_WafDenyIpsAndPaths_EmitDenyRouters()
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
                WafDenyIps = ["203.0.113.10", "198.51.100.0/24"],
                WafDenyPaths = ["/wp-admin", "/.env"],
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var yaml = mgr.BuildConfig([space]);

            Assert.Contains("-ipdeny", yaml);
            Assert.Contains("-pathdeny", yaml);
            Assert.Contains("ClientIP(`203.0.113.10`)", yaml);
            Assert.Contains("ClientIP(`198.51.100.0/24`)", yaml);
            Assert.Contains("PathPrefix(`/wp-admin`)", yaml);
            Assert.Contains("PathPrefix(`/.env`)", yaml);
            Assert.Contains("255.255.255.255/32", yaml);
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
    public void BuildConfig_Caddy_OverQuota_Emits503AndSkipsReverseProxy()
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
                    Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var space = new WebSpace
            {
                Uuid = Guid.NewGuid(),
                Domains = ["quota.example.com"],
                BackendPort = 20123,
                BandwidthLimitBytes = 1024,
                BandwidthUsedBytes = 2048,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var caddyfile = mgr.BuildConfig([space]);
            Assert.Contains("respond \"Bandwidth quota exceeded\" 503", caddyfile);
            Assert.DoesNotContain("reverse_proxy", caddyfile);
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
                    // WebSpaceDataPath() resolves via FuseQuotaLimiter when
                    // DiskLimiterMode defaults to fuse_quota, which would put the
                    // cert files below outside of config.System.Data. Force "none"
                    // so CustomSslFiles() actually finds them under Data.
                    DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };

            var mgr = new ReverseProxyManager(config);
            var spaceUuid = Guid.NewGuid();

            // listen 443 ssl is only ever emitted once a real cert exists on disk
            // (see BuildNginx) - use SslMode=custom so the cert lives under the
            // per-space test data dir instead of the hardcoded system cert path.
            var certDir = Path.Combine(config.System.Data, spaceUuid.ToString(), "ssl", "custom");
            Directory.CreateDirectory(certDir);
            File.WriteAllText(Path.Combine(certDir, "cert.pem"), "test-cert");
            File.WriteAllText(Path.Combine(certDir, "key.pem"), "test-key");

            var space = new WebSpace
            {
                Uuid = spaceUuid,
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
                SslMode = "custom",
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var nginx = mgr.BuildConfig([space]);

            // CodeRabbit: bind these assertions to the SAME server block instead
            // of just checking the strings exist anywhere in the file, so this
            // test actually fails if listen 443 ssl and server_name land in
            // different (wrong) server blocks.
            var httpsBlock = ExtractServerBlock(nginx, "listen 443 ssl;", "server_name www.example.com;");
            Assert.NotNull(httpsBlock);
            Assert.Contains("return 301 https://example.com$request_uri;", httpsBlock);
            Assert.Equal("example.com", ReverseProxyManager.ResolveApexDomain(space));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void EnsureSystemNginxIncludes_WritesIncludeFile_WhenConfDDirExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N"));
        var confD = Path.Combine(root, "conf.d");
        var generated = Path.Combine(root, "proxy", "nginx.conf");
        Directory.CreateDirectory(confD);
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "# generated\n");
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = Path.Combine(root, "data"),
                    DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };
            var mgr = new ReverseProxyManager(config);
            var includePath = Path.Combine(confD, "featherquilld.conf");

            mgr.EnsureSystemNginxIncludes(generated, confD, includePath);

            if (OperatingSystem.IsWindows())
            {
                // The method is a deliberate no-op on Windows (nginx conf.d
                // wiring only makes sense on the Linux hosts FeatherQuilld
                // actually manages).
                Assert.False(File.Exists(includePath));
                return;
            }

            Assert.True(File.Exists(includePath));
            var written = File.ReadAllText(includePath);
            Assert.Contains("Managed by FeatherQuilld", written);
            Assert.Contains($"include \"{generated}\";", written);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void EnsureSystemNginxIncludes_EscapesPath_WhenRootContainsSpacesAndQuotes()
    {
        if (OperatingSystem.IsWindows())
            return; // no-op path on Windows, nothing to assert.

        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N") + " with spaces");
        var confD = Path.Combine(root, "conf.d");
        // A path containing a double quote to prove the include directive stays
        // well-formed even for pathological (but valid on Linux) directory names.
        var generated = Path.Combine(root, "pro\"xy", "nginx.conf");
        Directory.CreateDirectory(confD);
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "# generated\n");
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = Path.Combine(root, "data"),
                    DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };
            var mgr = new ReverseProxyManager(config);
            var includePath = Path.Combine(confD, "featherquilld.conf");

            mgr.EnsureSystemNginxIncludes(generated, confD, includePath);

            Assert.True(File.Exists(includePath));
            var written = File.ReadAllText(includePath);
            var expectedEscaped = generated.Replace("\"", "\\\"", StringComparison.Ordinal);
            Assert.Contains($"include \"{expectedEscaped}\";", written);

            // The include line must be syntactically well-formed: exactly one
            // opening and one (escaped-aware) closing quote around the path,
            // proving embedded quotes can't break out of the directive.
            var includeLine = written.Split('\n').Single(l => l.StartsWith("include ", StringComparison.Ordinal));
            Assert.StartsWith("include \"", includeLine);
            Assert.EndsWith("\";", includeLine.TrimEnd('\r'));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void EnsureSystemNginxIncludes_SkipsWrite_WhenConfDDirMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N"));
        var confD = Path.Combine(root, "no-such-conf.d");
        var generated = Path.Combine(root, "proxy", "nginx.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "# generated\n");
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = Path.Combine(root, "data"),
                    DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };
            var mgr = new ReverseProxyManager(config);
            var includePath = Path.Combine(confD, "featherquilld.conf");

            mgr.EnsureSystemNginxIncludes(generated, confD, includePath);

            Assert.False(File.Exists(includePath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void EnsureSystemNginxIncludes_NeverOverwritesUnmanagedIncludeFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-proxy-" + Guid.NewGuid().ToString("N"));
        var confD = Path.Combine(root, "conf.d");
        var generated = Path.Combine(root, "proxy", "nginx.conf");
        Directory.CreateDirectory(confD);
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "# generated\n");
        var includePath = Path.Combine(confD, "featherquilld.conf");
        File.WriteAllText(includePath, "# hand-written by operator\ninclude /custom/path.conf;\n");
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Data = Path.Combine(root, "data"),
                    DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "nginx" },
                },
            };
            var mgr = new ReverseProxyManager(config);

            mgr.EnsureSystemNginxIncludes(generated, confD, includePath);

            Assert.Equal("# hand-written by operator\ninclude /custom/path.conf;\n", File.ReadAllText(includePath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildConfig_Nginx_RedirectHost_SkipsHttpsBlockWhenCertMissing()
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
                    DiskLimiterMode = "none",
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
                SslMode = "custom", // no cert files created -> stays pending
                BackendPort = 20123,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var nginx = mgr.BuildConfig([space]);

            // No cert yet: never emit a broken "listen 443 ssl" block (it would
            // make nginx -t fail for the whole file and silently kill every other
            // domain's ACME challenge along with it), and the main app domain
            // must keep serving over plain :80 instead of redirecting to a https
            // that doesn't work yet.
            Assert.DoesNotContain("listen 443 ssl;", nginx);
            Assert.Contains("location ^~ /.well-known/acme-challenge/", nginx);
            Assert.Contains($"proxy_pass http://127.0.0.1:20123;", nginx);

            // CodeRabbit: this test previously didn't check the redirect host at
            // all, so it would still pass even if www.example.com's :80 redirect
            // block silently disappeared. Verify it's still there with the right
            // target.
            var redirectBlock = ExtractServerBlock(nginx, "listen 80;", "server_name www.example.com;");
            Assert.NotNull(redirectBlock);
            Assert.Contains("return 301 https://example.com$request_uri;", redirectBlock);
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

            // The apex-cert-path resolution this test exists to cover:
            // dns01 mode must resolve every alias to the PRIMARY domain's
            // wildcard cert, never blog.example.com's own (nonexistent) cert.
            Assert.Equal("example.com", ReverseProxyManager.ResolveApexDomain(space));

            // NginxAcmeService.CertPath() points at the hardcoded system path
            // /etc/featherquilld/certs, which tests must not write to (no
            // guaranteed permissions on CI, and it would be shared mutable
            // state across test runs) - so this exercises the realistic
            // certReady=false path instead of asserting the crt/key path
            // string leaks into the config.
            var nginx = mgr.BuildConfig([space]);
            Assert.DoesNotContain("listen 443 ssl;", nginx);
            Assert.DoesNotContain(NginxAcmeService.CertPath("example.com"), nginx);
            Assert.DoesNotContain(NginxAcmeService.CertPath("blog.example.com"), nginx);
            Assert.Contains("server_name example.com;", nginx);
            Assert.Contains("server_name blog.example.com;", nginx);
            Assert.Contains("location ^~ /.well-known/acme-challenge/", nginx);
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

    /// <summary>
    /// Splits a generated nginx config into its individual "server { ... }"
    /// blocks and returns the content of the first block that contains ALL
    /// of the given marker strings, or null if none matches. Lets tests
    /// assert that two directives (e.g. "listen 443 ssl;" and a specific
    /// server_name) landed in the SAME block instead of just appearing
    /// somewhere in the file.
    /// </summary>
    private static string? ExtractServerBlock(string nginxConfig, params string[] mustContainAll)
    {
        foreach (var block in nginxConfig.Split("server {", StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.TrueForAll(mustContainAll, marker => block.Contains(marker)))
                return block;
        }

        return null;
    }
}
