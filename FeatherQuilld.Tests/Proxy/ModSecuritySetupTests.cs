using FeatherQuilld.Utils.Proxy;

namespace FeatherQuilld.Tests.Proxy;

public class ModSecuritySetupTests
{
    [Fact]
    public void BuildMainConf_EmitsIncludes()
    {
        var text = ModSecuritySetup.BuildMainConf(
            "/etc/modsecurity/modsecurity.conf",
            "/usr/share/modsecurity-crs/crs-setup.conf",
            "/usr/share/modsecurity-crs/rules/*.conf");

        Assert.Contains("Include /etc/modsecurity/modsecurity.conf", text);
        Assert.Contains("Include /usr/share/modsecurity-crs/crs-setup.conf", text);
        Assert.Contains("Include /usr/share/modsecurity-crs/rules/*.conf", text);
    }

    [Fact]
    public void IsValidRulesFile_MissingIncludes_ReturnsFalse()
    {
        using var dir = new TempDir();
        var main = Path.Combine(dir.Path, "main.conf");
        File.WriteAllText(main, """
            Include /tmp/does-not-exist-modsecurity.conf
            Include /tmp/does-not-exist-crs-setup.conf
            """);

        Assert.False(ModSecuritySetup.IsValidRulesFile(main));
    }

    [Fact]
    public void IsValidRulesFile_CompleteIncludes_ReturnsTrue()
    {
        using var dir = new TempDir();
        var modSec = Path.Combine(dir.Path, "modsecurity.conf");
        var crs = Path.Combine(dir.Path, "crs-setup.conf");
        var rulesDir = Path.Combine(dir.Path, "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(modSec, "SecRuleEngine On\n");
        File.WriteAllText(crs, "# crs\n");
        File.WriteAllText(Path.Combine(rulesDir, "REQUEST-901.conf"), "# rule\n");

        var main = Path.Combine(dir.Path, "main.conf");
        File.WriteAllText(main, ModSecuritySetup.BuildMainConf(
            modSec,
            crs,
            Path.Combine(rulesDir, "*.conf")));

        Assert.True(ModSecuritySetup.IsValidRulesFile(main));
    }

    [Fact]
    public void IsValidRulesFile_MissingFile_ReturnsFalse()
    {
        Assert.False(ModSecuritySetup.IsValidRulesFile("/tmp/fq-modsec-missing-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void ForceSecRuleEngineOnText_ReplacesDetectionOnly()
    {
        var result = ModSecuritySetup.ForceSecRuleEngineOnText("SecRuleEngine DetectionOnly\n");
        Assert.Contains("SecRuleEngine On", result);
        Assert.DoesNotContain("DetectionOnly", result);
    }

    [Fact]
    public void ForceSecRuleEngineOnText_AppendsWhenMissing()
    {
        var result = ModSecuritySetup.ForceSecRuleEngineOnText("# comment only\n");
        Assert.Contains("SecRuleEngine On", result);
    }

    [Fact]
    public void TryForceSecRuleEngineOn_UpdatesFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "modsecurity.conf");
        File.WriteAllText(path, "SecRuleEngine DetectionOnly\nSecRequestBodyAccess On\n");

        Assert.True(ModSecuritySetup.TryForceSecRuleEngineOn(path, out var error), error);
        var text = File.ReadAllText(path);
        Assert.Contains("SecRuleEngine On", text);
        Assert.DoesNotContain("DetectionOnly", text);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fq-modsec-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }
}
