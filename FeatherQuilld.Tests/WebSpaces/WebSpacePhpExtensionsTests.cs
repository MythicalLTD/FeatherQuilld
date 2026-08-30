using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpacePhpExtensionsTests
{
    [Fact]
    public void Sanitize_KeepsCatalogOnly()
    {
        var got = WebSpacePhpExtensions.Sanitize(["gd", "imagick", "INTL", "gd", "../evil", ""]);
        Assert.Equal(["gd", "imagick", "intl"], got);
    }

    [Fact]
    public void WriteAndRead_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, ["zip", "gd"]);
            Assert.Equal(["gd", "zip"], WebSpacePhpExtensions.Read(dir));
            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);
            Assert.Contains("mysqli", bootstrap);
            Assert.Contains("gd", bootstrap);
            Assert.Contains("zip", bootstrap);
            Assert.Contains("docker-php-ext-install", bootstrap);
            Assert.Contains("apache2-foreground", bootstrap);
            Assert.DoesNotContain("{{", bootstrap);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildBootstrap_IncludesPeclRedis()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, ["redis"]);
            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);
            Assert.Contains("pecl install", bootstrap);
            Assert.Contains("redis", bootstrap);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}

public class WebSpaceSanitizeDenyPathsTests
{
    [Fact]
    public void SanitizeDenyPaths_RejectsTraversalAndAcme()
    {
        var got = WebSpaceStore.SanitizeDenyPaths([
            "xmlrpc.php",
            "/wp-config.php",
            "/../etc/passwd",
            "/.well-known/acme-challenge/x",
            "/",
            "//admin",
        ]);
        Assert.Equal(["/xmlrpc.php", "/wp-config.php", "/admin"], got);
    }
}
