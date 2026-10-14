using FeatherQuilld.Utils.WebSpaces.Disk;

namespace FeatherQuilld.Tests.WebSpaces;

public class FuseQuotaBinaryProvisionerTests
{
    [Theory]
    [InlineData("fusequota-x86_64-linux")]
    [InlineData("fusequota-aarch64-linux")]
    [InlineData("fusequota-ppc64le-linux")]
    [InlineData("fusequota-riscv64-linux")]
    public void BuildDownloadUrl_uses_pinned_release_and_expected_asset_name(string asset)
    {
        // The release is pinned to a fixed tag instead of the moving "latest".
        Assert.DoesNotContain("/releases/latest/", FuseQuotaBinaryProvisioner.DefaultReleaseBase);
        Assert.Equal(
            "https://github.com/calagopus/fusequota/releases/download/"
            + FuseQuotaBinaryProvisioner.DefaultReleaseTag,
            FuseQuotaBinaryProvisioner.DefaultReleaseBase);

        var url = FuseQuotaBinaryProvisioner.BuildDownloadUrl(asset, FuseQuotaBinaryProvisioner.DefaultReleaseBase);
        Assert.Equal($"{FuseQuotaBinaryProvisioner.DefaultReleaseBase}/{asset}", url);

        // Every shipped asset of the pinned release carries a known SHA-256.
        Assert.NotNull(FuseQuotaBinaryProvisioner.ExpectedSha256(asset));
        Assert.False(string.IsNullOrWhiteSpace(FuseQuotaBinaryProvisioner.ExpectedSha256(asset)));
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
        // A custom source cannot be hash-pinned.
        Assert.Null(FuseQuotaBinaryProvisioner.ExpectedSha256(
            "fusequota-x86_64-linux", "https://example.com/releases"));
    }
}
