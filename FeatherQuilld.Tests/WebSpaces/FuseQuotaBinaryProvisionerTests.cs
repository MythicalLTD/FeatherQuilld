using FeatherQuilld.Utils.WebSpaces.Disk;

namespace FeatherQuilld.Tests.WebSpaces;

public class FuseQuotaBinaryProvisionerTests
{
    [Theory]
    [InlineData("fusequota-x86_64-linux")]
    [InlineData("fusequota-aarch64-linux")]
    [InlineData("fusequota-ppc64le-linux")]
    [InlineData("fusequota-riscv64-linux")]
    public void BuildDownloadUrl_uses_expected_asset_name(string asset)
    {
        var url = FuseQuotaBinaryProvisioner.BuildDownloadUrl(asset);
        Assert.Equal(
            $"https://github.com/calagopus/fusequota/releases/latest/download/{asset}",
            url);
    }

    [Fact]
    public void CachePath_is_next_to_app_binary()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "fusequota");
        Assert.Equal(expected, FuseQuotaBinaryProvisioner.CachePath);
    }

    [Fact]
    public void BuildDownloadUrl_honors_custom_release_base()
    {
        var url = FuseQuotaBinaryProvisioner.BuildDownloadUrl(
            "fusequota-x86_64-linux",
            "https://example.com/releases");
        Assert.Equal("https://example.com/releases/fusequota-x86_64-linux", url);
    }
}
