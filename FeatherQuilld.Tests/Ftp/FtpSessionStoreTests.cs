using FeatherQuilld.Utils.Config.Ftp;
using FeatherQuilld.Utils.Ftp;

namespace FeatherQuilld.Tests.Ftp;

public class FtpSessionStoreTests
{
    [Fact]
    public void SetAndTryGet_RoundTripsSession()
    {
        FtpSessionStore.Set("user.abc", new FtpSessionContext("/var/www/site", false));
        Assert.True(FtpSessionStore.TryGet("user.abc", out var session));
        Assert.Equal("/var/www/site", session.RootPath);
        Assert.False(session.ReadOnly);
        FtpSessionStore.Remove("user.abc");
        Assert.False(FtpSessionStore.TryGet("user.abc", out _));
    }
}

public class FtpProbeTests
{
    [Fact]
    public void IsListening_ReturnsFalseWhenDisabled()
    {
        var config = new FtpConfig { Enabled = false, Port = 21 };
        Assert.False(FtpProbe.IsListening(config));
    }
}
