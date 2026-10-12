using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Native;

namespace FeatherQuilld.Tests.Native;

public class FqCtlTests
{
    [Theory]
    [InlineData(0, FqCtlExitCode.Ok)]
    [InlineData(1, FqCtlExitCode.Usage)]
    [InlineData(2, FqCtlExitCode.NotFound)]
    [InlineData(3, FqCtlExitCode.Permission)]
    [InlineData(4, FqCtlExitCode.Busy)]
    [InlineData(5, FqCtlExitCode.OsError)]
    [InlineData(99, FqCtlExitCode.OsError)]
    public void MapExitCode_MatchesStableContract(int code, FqCtlExitCode expected)
    {
        Assert.Equal(expected, FqCtl.MapExitCode(code));
    }

    [Fact]
    public void TryResolveBinaryPath_PrefersConfiguredAbsolutePath()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "fqctl-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fake = Path.Combine(dir, "fqctl");
        try
        {
            File.WriteAllText(fake, "#!/bin/true\n");
            var system = new SystemConfig { FqctlPath = fake };
            Assert.True(FqCtl.TryResolveBinaryPath(system, out var path));
            Assert.Equal(Path.GetFullPath(fake), path);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore and stfu <3 */ }
        }
    }

    [Fact]
    public void Version_WhenBinaryPresent_ReturnsOk()
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!FqCtl.TryResolveBinaryPath(null, out var bin))
            return;

        var result = FqCtl.Run(bin, ["version"]);
        Assert.True(result.Succeeded, result.Stderr);
    }

    [Fact]
    public void Usage_WhenBinaryPresent_ReturnsUsageExitCode()
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!FqCtl.TryResolveBinaryPath(null, out var bin))
            return;

        var result = FqCtl.Run(bin, ["not-a-command"]);
        Assert.Equal(FqCtlExitCode.Usage, result.Code);
    }

    [Fact]
    public void ChownRecursive_UsageOnBadOwnerSpec_WhenBinaryPresent()
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!FqCtl.TryResolveBinaryPath(null, out var bin))
            return;

        var result = FqCtl.Run(bin, ["chown-r", "www-data:www-data", "/tmp"]);
        Assert.Equal(FqCtlExitCode.Usage, result.Code);
    }
}
