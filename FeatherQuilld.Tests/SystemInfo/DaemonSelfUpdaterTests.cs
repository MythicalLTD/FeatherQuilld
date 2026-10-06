using FeatherQuilld.Utils.SystemInfo;

namespace FeatherQuilld.Tests.SystemInfo;

public class DaemonSelfUpdaterTests
{
    [Fact]
    public async Task ApplyAsync_RejectsDisableChecksum()
    {
        var result = await DaemonSelfUpdater.ApplyAsync(
            new DaemonSelfUpdater.SelfUpdateRequest(DisableChecksum: true),
            logger: null);
        Assert.False(result.Success);
        Assert.Contains("disable_checksum", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_RejectsDisallowedUrlSource()
    {
        var result = await DaemonSelfUpdater.ApplyAsync(
            new DaemonSelfUpdater.SelfUpdateRequest(
                Source: "url",
                Url: "https://evil.example/bin",
                Sha256: new string('a', 64)),
            logger: null);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_RejectsUrlSourceWithoutSha()
    {
        var result = await DaemonSelfUpdater.ApplyAsync(
            new DaemonSelfUpdater.SelfUpdateRequest(
                Source: "url",
                Url: "https://github.com/mythicalltd/featherquilld/releases/download/v1/FeatherQuilld"),
            logger: null);
        Assert.False(result.Success);
        Assert.Contains("sha256", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAllowedDownloadUrl_AcceptsGithubHostsOnly()
    {
        Assert.True(DaemonSelfUpdater.IsAllowedDownloadUrl(
            "https://github.com/mythicalltd/featherquilld/releases/download/v1/x"));
        Assert.True(DaemonSelfUpdater.IsAllowedDownloadUrl(
            "https://objects.githubusercontent.com/github-production-release-asset/1"));
        Assert.False(DaemonSelfUpdater.IsAllowedDownloadUrl("http://github.com/x"));
        Assert.False(DaemonSelfUpdater.IsAllowedDownloadUrl("https://evil.example/x"));
    }

    [Fact]
    public void IsAllowedRepo_RestrictsOwnerAndName()
    {
        Assert.True(DaemonSelfUpdater.IsAllowedRepo(null, null));
        Assert.True(DaemonSelfUpdater.IsAllowedRepo("mythicalltd", "featherquilld"));
        Assert.False(DaemonSelfUpdater.IsAllowedRepo("evil", "featherquilld"));
        Assert.False(DaemonSelfUpdater.IsAllowedRepo("mythicalltd", "malware"));
    }
}
