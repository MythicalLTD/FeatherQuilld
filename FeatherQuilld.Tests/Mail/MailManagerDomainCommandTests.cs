using System.Text.Json;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Mail;

/// <summary>
/// Regression coverage for the docker-mailserver "setup" CLI surface actually
/// invoked by MailManager. Current docker-mailserver images (v13+, the
/// default ACCOUNT_PROVISIONER=FILE mode used by our compose file) have NO
/// top-level "domain" command — domains exist implicitly from mailbox
/// addresses. A call to "setup domain add &lt;domain&gt;" fails with
/// "invalid command" and previously aborted CreateMailbox/AddDomain before
/// "setup email add" ever ran, breaking every mailbox creation.
///
/// These tests run a fake "docker" shell script (prepended onto PATH for the
/// test process only) that records every invocation and always exits 0, so
/// MailManager believes the container is running and every RunDocker call
/// succeeds. They assert on the RECORDED ARGUMENTS, not on any faked stdout,
/// so they catch a regression even if someone reintroduces the domain
/// subcommand under a different action name.
/// </summary>
public class MailManagerDomainCommandTests : IDisposable
{
    private readonly string _root = "";
    private readonly string _fakeBinDir = "";
    private readonly string _invocationLog = "";
    private readonly string? _originalPath;

    public MailManagerDomainCommandTests()
    {
        if (!OperatingSystem.IsLinux())
            return;

        _root = Path.Combine(Path.GetTempPath(), "fq-maildomain-" + Guid.NewGuid().ToString("N"));
        _fakeBinDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_fakeBinDir);
        _invocationLog = Path.Combine(_root, "invocations.log");

        var dockerScript = Path.Combine(_fakeBinDir, "docker");
        File.WriteAllText(dockerScript, $"""
            #!/usr/bin/env bash
            echo "$@" >> "{_invocationLog}"
            if [[ "$1" == "ps" ]]; then
              echo "featherquilld-mailserver"
            fi
            exit 0
            """);
        File.SetUnixFileMode(dockerScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", _fakeBinDir + Path.PathSeparator + _originalPath);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private AppConfig MakeConfig() => new()
    {
        System = new SystemConfig
        {
            RootDirectory = _root,
            Mail = new MailConfig { DataPath = Path.Combine(_root, "mail") },
        },
    };

    private IReadOnlyList<string[]> ReadInvocations()
    {
        if (!File.Exists(_invocationLog))
            return [];

        return File.ReadAllLines(_invocationLog)
            .Where(l => l.Length > 0)
            .Select(l => l.Split(' '))
            .ToList();
    }

    [Fact]
    public void AddDomain_NeverInvokesSetupDomainCommand()
    {
        if (!OperatingSystem.IsLinux())
            return; // fake docker script requires a POSIX shell

        var mgr = new MailManager(MakeConfig());
        mgr.AddDomain("example.com");

        var invocations = ReadInvocations();
        Assert.DoesNotContain(invocations, args =>
            args.Length >= 3 && args[0] == "exec" && args[2] == "setup" &&
            args.Length >= 4 && args[3] == "domain");
    }

    [Fact]
    public void RemoveDomain_NeverInvokesSetupDomainCommand()
    {
        if (!OperatingSystem.IsLinux())
            return; // fake docker script requires a POSIX shell

        var mgr = new MailManager(MakeConfig());
        mgr.AddDomain("example.com");
        mgr.RemoveDomain("example.com");

        var invocations = ReadInvocations();
        Assert.DoesNotContain(invocations, args =>
            args.Length >= 3 && args[0] == "exec" && args[2] == "setup" &&
            args.Length >= 4 && args[3] == "domain");
    }

    [Fact]
    public void AddDomain_PersistsDomainAndListsIt()
    {
        if (!OperatingSystem.IsLinux())
            return; // fake docker script requires a POSIX shell

        var config = MakeConfig();
        var mgr = new MailManager(config);
        mgr.AddDomain("Example.COM");

        Assert.Contains("example.com", mgr.ListDomains());
    }

    [Fact]
    public void CreateMailbox_InvokesSetupEmailAdd_NotSetupDomain()
    {
        if (!OperatingSystem.IsLinux())
            return; // fake docker script requires a POSIX shell

        var mgr = new MailManager(MakeConfig());
        var payload = new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["email"] = "user@example.com",
            ["password"] = "correct horse battery staple",
        };

        mgr.Provision(payload);

        var invocations = ReadInvocations();
        Assert.Contains(invocations, args =>
            args.Length >= 4 && args[0] == "exec" && args[2] == "setup" &&
            args[3] == "email" && args.Length >= 5 && args[4] == "add");
        Assert.DoesNotContain(invocations, args =>
            args.Length >= 4 && args[0] == "exec" && args[2] == "setup" && args[3] == "domain");
    }
}
