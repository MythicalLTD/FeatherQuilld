using FeatherQuilld.Utils.Proxy;

namespace FeatherQuilld.Tests.Proxy;

public class ProxyRouteSanitizerTests
{
    [Fact]
    public void NormalizeRedirectTarget_AcceptsHttpsAndPath()
    {
        Assert.Equal("/apex", ProxyRouteSanitizer.NormalizeRedirectTarget("/apex"));
        Assert.Equal(
            "https://example.com/path",
            ProxyRouteSanitizer.NormalizeRedirectTarget("https://example.com/path"));
        Assert.Equal(
            "https://example.com",
            ProxyRouteSanitizer.NormalizeRedirectTarget("https://example.com"));
        Assert.Null(ProxyRouteSanitizer.NormalizeRedirectTarget(null));
        Assert.Null(ProxyRouteSanitizer.NormalizeRedirectTarget("  "));
    }

    [Fact]
    public void NormalizeRedirectTarget_RejectsInjection()
    {
        Assert.Throws<ArgumentException>(() =>
            ProxyRouteSanitizer.NormalizeRedirectTarget("https://x\nadd_header Evil yes;"));
        Assert.Throws<ArgumentException>(() =>
            ProxyRouteSanitizer.NormalizeRedirectTarget("javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() =>
            ProxyRouteSanitizer.NormalizeRedirectTarget("ftp://example.com"));
    }

    [Fact]
    public void NormalizeBackendHost_AcceptsHostAndIp()
    {
        Assert.Equal("127.0.0.1", ProxyRouteSanitizer.NormalizeBackendHost("127.0.0.1"));
        Assert.Equal("backend.local", ProxyRouteSanitizer.NormalizeBackendHost("Backend.Local"));
        Assert.Equal("", ProxyRouteSanitizer.NormalizeBackendHost(null));
    }

    [Fact]
    public void NormalizeBackendHost_RejectsMetacharacters()
    {
        Assert.Throws<ArgumentException>(() =>
            ProxyRouteSanitizer.NormalizeBackendHost("evil; return 200"));
        Assert.Throws<ArgumentException>(() =>
            ProxyRouteSanitizer.NormalizeBackendHost("host:8080"));
        Assert.Throws<ArgumentException>(() =>
            ProxyRouteSanitizer.NormalizeBackendHost("http://127.0.0.1"));
    }
}
