using FeatherQuilld.Utils;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Config;

public class RootPrivilegesTests
{
    [Fact]
    public void RequiresRoot_ForSystemConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.False(RootPrivileges.RequiresRoot(AppConfig.DefaultPath()));
            return;
        }

        Assert.True(RootPrivileges.RequiresRoot(null));
        Assert.True(RootPrivileges.RequiresRoot(AppConfig.DefaultPath()));
        Assert.False(RootPrivileges.RequiresRoot("/tmp/fq-dev/config.yml"));
    }
}
