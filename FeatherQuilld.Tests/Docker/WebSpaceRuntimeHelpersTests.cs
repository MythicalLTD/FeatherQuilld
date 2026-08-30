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

    [Fact]
    public void ResolveRuntimeImage_FixesLeakedInstallCliImage()
    {
        var space = new WebSpace
        {
            Runtime = "php",
            ContainerPort = 80,
            ContainerImage = "php:8.3-cli",
        };
        Assert.Equal("php:8.3-apache", WebSpaceRuntime.ResolveRuntimeImage(space, space.ContainerImage!));
    }

    [Fact]
    public void ResolveRuntimeImage_KeepsCliWhenStartupSet()
    {
        var space = new WebSpace
        {
            Runtime = "php",
            ContainerPort = 80,
            Startup = "php -S 0.0.0.0:80 -t public",
            ContainerImage = "php:8.4-cli",
        };
        Assert.Equal("php:8.4-cli", WebSpaceRuntime.ResolveRuntimeImage(space, space.ContainerImage!));
    }

    [Fact]
    public void EnsurePhpIni_WritesDefaultOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpaceSiteFiles.EnsurePhpIni(dir);
            var path = WebSpaceSiteFiles.PhpIniHostPath(dir);
            Assert.True(File.Exists(path));
            var first = File.ReadAllText(path);
            Assert.Contains("memory_limit", first);
            File.WriteAllText(path, "memory_limit = 64M\n");
            WebSpaceSiteFiles.EnsurePhpIni(dir);
            Assert.Equal("memory_limit = 64M\n", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildApacheAddonConf_EmitsPerHostDocumentRoot()
    {
        var space = new WebSpace
        {
            Runtime = "php",
            DocumentRoot = "public",
            DomainRoutes =
            [
                new WebSpaceDomainRoute { Domain = "app.example.com", Type = "primary", DocumentRoot = "public" },
                new WebSpaceDomainRoute { Domain = "blog.example.com", Type = "alias", DocumentRoot = "sites/blog" },
            ],
        };
        var conf = WebSpaceSiteFiles.BuildApacheAddonConf(space);
        Assert.Contains("ServerName app.example.com", conf);
        Assert.Contains("DocumentRoot /var/www/html/public", conf);
        Assert.Contains("ServerName blog.example.com", conf);
        Assert.Contains("DocumentRoot /var/www/html/sites/blog", conf);
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
