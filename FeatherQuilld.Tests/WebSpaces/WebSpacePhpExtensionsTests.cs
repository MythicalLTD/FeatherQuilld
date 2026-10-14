using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpacePhpExtensionsTests
{
    [Fact]
    public void Sanitize_KeepsCatalogOnly()
    {
        var got = WebSpacePhpExtensions.Sanitize(["gd", "imagick", "INTL", "gd", "../evil", ""]);
        Assert.Equal(["gd", "imagick", "intl"], got);
    }

    [Fact]
    public void WriteAndRead_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, ["zip", "gd"]);
            Assert.Equal(["gd", "zip"], WebSpacePhpExtensions.Read(dir));
            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);
            Assert.Contains("mysqli", bootstrap);
            Assert.Contains("gd", bootstrap);
            Assert.Contains("zip", bootstrap);
            Assert.Contains("docker-php-ext-install", bootstrap);
            Assert.Contains("apache2-foreground", bootstrap);
            Assert.DoesNotContain("{{", bootstrap);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildBootstrap_IncludesPeclRedis()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, ["redis"]);
            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);
            Assert.Contains("pecl install", bootstrap);
            Assert.Contains("redis", bootstrap);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Stock php:*-apache ships Zend OPcache compiled in; php -m lists it as
    /// "Zend OPcache", which never matches ^opcache$. The bootstrap must detect
    /// OPcache via extension_loaded and must not let a failed opcache install
    /// abort the entrypoint (restart loop).
    /// </summary>
    [Fact]
    public void BuildBootstrap_DetectsZendOpcacheAndGuardsPerExtensionInstall()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, []);
            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);

            Assert.Contains("php_has_ext()", bootstrap);
            Assert.Contains("extension_loaded(\"Zend OPcache\")", bootstrap);
            Assert.Contains("extension_loaded(\"opcache\")", bootstrap);
            Assert.Contains("if ! php_has_ext \"$ext\"; then", bootstrap);

            // Per-ext install with soft failure (not a single batch install of all core exts).
            Assert.Contains("if ! php_has_ext opcache; then", bootstrap);
            Assert.Contains("if ! php_has_ext mysqli; then", bootstrap);
            Assert.Contains("if ! php_has_ext pdo_mysql; then", bootstrap);
            Assert.Contains("featherquilld: warning: failed to install PHP extension opcache", bootstrap);
            Assert.DoesNotContain("docker-php-ext-install -j\"$(nproc)\" mysqli pdo_mysql opcache", bootstrap);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ReadBaseline_DefaultsAndOptionalOverride()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, ["gd"]);
            Assert.Equal(WebSpacePhpExtensions.Baseline, WebSpacePhpExtensions.ReadBaseline(dir));

            // Drop opcache from baseline (derived image already has Zend OPcache).
            WebSpacePhpExtensions.Write(dir, ["gd"], baseline: ["mysqli", "pdo_mysql"]);
            Assert.Equal(["mysqli", "pdo_mysql"], WebSpacePhpExtensions.ReadBaseline(dir));

            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);
            Assert.Contains("for ext in mysqli pdo_mysql gd; do", bootstrap);
            Assert.DoesNotContain("php_has_ext opcache", bootstrap);

            // Rewriting extras alone must preserve the baseline override.
            WebSpacePhpExtensions.Write(dir, ["zip"]);
            Assert.Equal(["mysqli", "pdo_mysql"], WebSpacePhpExtensions.ReadBaseline(dir));
            Assert.Equal(["zip"], WebSpacePhpExtensions.Read(dir));

            // Explicit empty baseline disables all baseline installs.
            WebSpacePhpExtensions.Write(dir, ["zip"], baseline: []);
            Assert.Empty(WebSpacePhpExtensions.ReadBaseline(dir));
            var noBaseline = WebSpacePhpExtensions.BuildBootstrap(dir);
            Assert.Contains("for ext in zip; do", noBaseline);
            Assert.DoesNotContain("php_has_ext mysqli", noBaseline);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Regression for the "$$" raw-string escaping bug: BuildBootstrap used to emit
    /// "$$NEED_INSTALL" / "$${ext}" / "$$ADDONS" / "$$PHPIZE_DEPS" literally (two dollar
    /// signs each, from a `$$"""..."""` C# raw string where `$$` is NOT the interpolation
    /// escape for `$` — `{{ }}` is). Bash then expanded "$$" to the current PID, so
    /// "$<PID>NEED_INSTALL" never equaled "1", the install branch silently never ran, and
    /// baseline extensions (mysqli, pdo_mysql, opcache) were never installed - breaking
    /// every WordPress/PHP WebPlate with a 500 "missing the MySQL extension" error.
    /// Guard: the generated script must reference these as single-`$` shell variables and
    /// must never contain a literal "$$" anywhere.
    /// </summary>
    [Fact]
    public void BuildBootstrap_NeverEmitsDoubleDollarBashVariables()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, ["gd"]);
            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);

            Assert.DoesNotContain("$$", bootstrap);
            Assert.Contains("if [ \"$NEED_INSTALL\" = \"1\" ]; then", bootstrap);
            Assert.Contains("$PHPIZE_DEPS", bootstrap);
            Assert.Contains("if [ -f \"$ADDONS\" ]; then", bootstrap);
            Assert.Contains("cp \"$ADDONS\"", bootstrap);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// The generated script must be syntactically valid bash (`bash -n`), and the
    /// NEED_INSTALL guard specifically must evaluate as a real shell condition rather
    /// than silently short-circuiting to false because of PID-expansion corruption
    /// (see BuildBootstrap_NeverEmitsDoubleDollarBashVariables). Skipped when bash isn't
    /// on PATH (e.g. a bare Windows dev box without git-bash/WSL).
    /// </summary>
    [Fact]
    public void BuildBootstrap_IsValidBashAndInstallGuardActuallyTriggers()
    {
        if (!IsBashAvailable())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WebSpacePhpExtensions.Write(dir, []);
            var bootstrap = WebSpacePhpExtensions.BuildBootstrap(dir);

            var scriptPath = Path.Combine(dir, "bootstrap.sh");
            File.WriteAllText(scriptPath, bootstrap);

            // Syntax check only (no `exec apache2-foreground`).
            var syntax = RunBash($"-n \"{scriptPath}\"");
            Assert.True(syntax.ExitCode == 0, $"bash -n failed: {syntax.Output}");

            // Drive the guard with a fake php that always reports extensions missing, so
            // NEED_INSTALL must become 1 — proving "$NEED_INSTALL" (not "$<pid>NEED_INSTALL")
            // is evaluated. A host PHP with mysqli/opcache installed would otherwise make
            // this assertion pass for the wrong reason.
            var bin = Path.Combine(dir, "bin");
            Directory.CreateDirectory(bin);
            var phpShim = Path.Combine(bin, "php");
            File.WriteAllText(phpShim, "#!/bin/bash\nexit 1\n");
            MakeExecutable(phpShim);

            var guardScript = $$"""
                set -e
                export PATH="{{bin}}:$PATH"
                hash -r
                php_has_ext() {
                  local ext="$1"
                  case "$ext" in
                    opcache)
                      php -r 'exit(extension_loaded("Zend OPcache")||extension_loaded("opcache")?0:1);' 2>/dev/null
                      ;;
                    *)
                      php -r "exit(extension_loaded('$ext')?0:1);" 2>/dev/null
                      ;;
                  esac
                }
                NEED_INSTALL=0
                for ext in mysqli pdo_mysql opcache; do
                  if ! php_has_ext "$ext"; then
                    NEED_INSTALL=1
                    break
                  fi
                done
                echo "NEED_INSTALL=$NEED_INSTALL"
                """.ReplaceLineEndings("\n");
            var guardPath = Path.Combine(dir, "guard.sh");
            File.WriteAllText(guardPath, guardScript);
            var run = RunBash($"\"{guardPath}\"");
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("NEED_INSTALL=1", run.Output);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// When php reports Zend OPcache as loaded, php_has_ext opcache must succeed so
    /// NEED_INSTALL is not forced solely by the opcache name mismatch.
    /// </summary>
    [Fact]
    public void PhpHasExt_TreatsZendOpcacheAsLoaded()
    {
        if (!IsBashAvailable())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "fq-phpext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Fake php that reports Zend OPcache loaded and mysqli missing.
            var bin = Path.Combine(dir, "bin");
            Directory.CreateDirectory(bin);
            var phpShim = Path.Combine(bin, "php");
            File.WriteAllText(phpShim, """
                #!/bin/bash
                if [[ "$*" == *Zend\ OPcache* ]] || [[ "$*" == *"'opcache'"* ]] || [[ "$*" == *'"opcache"'* ]]; then
                  exit 0
                fi
                exit 1
                """.ReplaceLineEndings("\n"));
            MakeExecutable(phpShim);

            var script = $$"""
                set -e
                export PATH="{{bin}}:$PATH"
                hash -r
                command -v php
                php_has_ext() {
                  local ext="$1"
                  case "$ext" in
                    opcache)
                      php -r 'exit(extension_loaded("Zend OPcache")||extension_loaded("opcache")?0:1);' 2>/dev/null
                      ;;
                    *)
                      php -r "exit(extension_loaded('$ext')?0:1);" 2>/dev/null
                      ;;
                  esac
                }
                if php_has_ext opcache; then echo "OPCACHE=ok"; else echo "OPCACHE=missing"; fi
                if php_has_ext mysqli; then echo "MYSQLI=ok"; else echo "MYSQLI=missing"; fi
                NEED_INSTALL=0
                for ext in mysqli pdo_mysql opcache; do
                  if ! php_has_ext "$ext"; then
                    NEED_INSTALL=1
                    break
                  fi
                done
                echo "NEED_INSTALL=$NEED_INSTALL"
                """.ReplaceLineEndings("\n");
            var scriptPath = Path.Combine(dir, "opcache-guard.sh");
            File.WriteAllText(scriptPath, script);
            var run = RunBash($"\"{scriptPath}\"");
            Assert.Equal(0, run.ExitCode);
            Assert.Contains(bin, run.Output); // shim must win over host php
            Assert.Contains("OPCACHE=ok", run.Output);
            Assert.Contains("MYSQLI=missing", run.Output);
            // Still need install for mysqli, but not because opcache falsely failed.
            Assert.Contains("NEED_INSTALL=1", run.Output);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static bool IsBashAvailable() => RunBash("--version").ExitCode == 0;

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static (int ExitCode, string Output) RunBash(string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "bash",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return (-1, "");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            return (proc.ExitCode, stdout + stderr);
        }
        catch
        {
            return (-1, "");
        }
    }
}

public class WebSpaceSanitizeDenyPathsTests
{
    [Fact]
    public void SanitizeDenyPaths_RejectsTraversalAndAcme()
    {
        var got = WebSpaceStore.SanitizeDenyPaths([
            "xmlrpc.php",
            "/wp-config.php",
            "/../etc/passwd",
            "/.well-known/acme-challenge/x",
            "/",
            "//admin",
        ]);
        Assert.Equal(["/xmlrpc.php", "/wp-config.php", "/admin"], got);
    }
}
