using FeatherQuilld.Utils.WebSpaces;
using Xunit;

namespace FeatherQuilld.Tests.WebSpaces;

public class BackupRetentionTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("keep=7", 7)]
    [InlineData("keep = 3", 3)]
    [InlineData("keep=500", 100)]
    [InlineData("nonsense", 0)]
    public void ParseRetention_ReadsKeepCount(string? payload, int expected) =>
        Assert.Equal(expected, WebSpaceBackupService.ParseRetention(payload));
}
