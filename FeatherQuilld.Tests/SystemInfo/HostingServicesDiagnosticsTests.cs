using FeatherQuilld.Utils.SystemInfo;

namespace FeatherQuilld.Tests.SystemInfo;

public class HostingServicesDiagnosticsTests
{
    [Theory]
    [InlineData("nginx", true, true, "modsecurity")]
    [InlineData("nginx", true, false, "basic")]
    [InlineData("caddy", true, true, "basic")]
    [InlineData("traefik", true, false, "basic")]
    [InlineData("nginx", false, true, "off")]
    public void ResolveWafMode_MatchesProviderAndProbe(string provider, bool enabled, bool modsec, string expected)
    {
        Assert.Equal(expected, HostingServicesDiagnostics.ResolveWafMode(provider, enabled, modsec));
    }
}
