using System.Text;
using FeatherQuilld.Utils;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Startup;

namespace FeatherQuilld.Commands;

/// <summary>
/// Interactive and flag-driven node setup: join-data, manual credentials, systemd service.
/// </summary>
public static class ConfigureCommand
{
    public static int Run(string[] args)
    {
        var quiet = HasFlag(args, "--quiet", "-q");
        var noService = HasFlag(args, "--no-service");
        var installServiceFlag = HasFlag(args, "--install-service");
        var overrideExisting = HasFlag(args, "--override");

        try
        {
            var configPath = GetOptionValue(args, "--config")
                             ?? GetOptionValue(args, "-c")
                             ?? Environment.GetEnvironmentVariable("FEATHERQUILLD_CONFIG")
                             ?? Config.DefaultPath();

            var joinData = GetOptionValue(args, "--join-data")
                           ?? Environment.GetEnvironmentVariable("FEATHERQUILLD_JOIN_DATA");

            ConfigureWizardResult? wizard = null;

            if (string.IsNullOrWhiteSpace(joinData))
            {
                if (quiet)
                {
                    ColoredConsole.WriteLine("&c&lMissing --join-data in non-interactive mode.&r");
                    ConfigureSequence.RenderUsage();
                    return 1;
                }

                ConfigureBanner.Print();
                wizard = ConfigureWizard.Run(noService, installServiceFlag);
                joinData = wizard.JoinData;
            }
            else if (!quiet)
            {
                ConfigureBanner.Print();
            }

            if (File.Exists(configPath) && !overrideExisting)
            {
                ColoredConsole.WriteLine($"&e&lConfig already exists&r &8→&r &f{configPath}&r");
                ColoredConsole.WriteLine("&7Use &f--override&7 to replace it.&r");
                return 1;
            }

            var installService = wizard?.InstallService
                                 ?? (installServiceFlag && !noService);

            if (wizard is null && !noService && !installServiceFlag && ConfigureWizard.IsInteractive)
            {
                installService = PromptServiceInstallOnly();
            }

            Config? joinConfig = wizard?.JoinConfig;
            var inputMode = wizard?.Mode ?? ConfigureInputMode.JoinData;

            var sequence = new ConfigureSequence(quiet);
            ServiceInstallResult? serviceResult = null;

            var success = sequence
                .Step(inputMode == ConfigureInputMode.JoinData ? "Decoding join data" : "Loading credentials", _ =>
                {
                    if (inputMode == ConfigureInputMode.JoinData)
                    {
                        var yaml = DecodeJoinData(joinData!);
                        var bytes = Encoding.UTF8.GetByteCount(yaml);
                        return StepOk(
                            $"&7Decoded &a{bytes}&7 bytes of join YAML&r",
                            $"&7Target config &8→&r &f{configPath}&r");
                    }

                    joinConfig!.FilePath = configPath;
                    return StepOk(
                        "&7Manual credentials loaded&r",
                        $"&7Target config &8→&r &f{configPath}&r");
                })
                .Step("Validating node credentials", reporter =>
                {
                    if (inputMode == ConfigureInputMode.JoinData)
                    {
                        var yaml = DecodeJoinData(joinData!);
                        joinConfig = Config.DeserializeYaml(yaml);
                    }

                    joinConfig!.FilePath = configPath;

                    if (joinConfig.Uuid == Guid.Empty)
                        joinConfig.Uuid = Guid.NewGuid();

                    joinConfig.ValidateJoin();

                    reporter.Detail($"&7uuid &b{joinConfig.Uuid}&r");
                    reporter.Detail($"&7token &8{MaskSecret(joinConfig.TokenId)}&r");
                    reporter.Detail($"&7panel &f{joinConfig.Remote.Panel}&r");

                    return new ConfigureStepResult();
                })
                .Step("Writing bootstrap config", _ =>
                {
                    joinConfig!.EnsureDirectories();
                    joinConfig.Save();
                    return StepOk($"&aSaved&r &8→&r &f{joinConfig.FilePath}&r");
                })
                .Step("Connecting to FeatherPanel", _ =>
                    StepOk(
                        $"&7Config &f{joinConfig!.Remote.ConfigUrl}&r",
                        $"&7Health &f{joinConfig.Remote.HealthUrl}&r",
                        $"&7Timeout &a{joinConfig.Remote.Timeout}s&r · &7retries &a{joinConfig.Remote.RetryLimit}&r"))
                .Step("Fetching runtime config", reporter =>
                {
                    var panelClient = new PanelClient(joinConfig!, onProgress: progress =>
                    {
                        reporter.Progress(
                            $"attempt {progress.Attempt}/{progress.MaxAttempts} — {progress.Message}");
                    });

                    var runtime = panelClient.FetchRuntimeConfigAsync().GetAwaiter().GetResult();
                    joinConfig!.MergeRuntime(runtime);
                    joinConfig.EnsureDirectories();
                    joinConfig.Save();

                    return StepOk(
                        "&aRuntime config received&r",
                        $"&7API port &a{joinConfig.Api.Port}&r",
                        joinConfig.Sftp.Enabled
                            ? $"&7SFTP &aenabled&r &8on port &a{joinConfig.Sftp.Port}&r"
                            : "&7SFTP &8disabled&r");
                })
                .Step("Installing systemd service", reporter =>
                {
                    if (!installService)
                    {
                        reporter.Detail("&8Skipped &7(use without --no-service to install)&r");
                        
                        return new ConfigureStepResult { Status = ConfigureStepStatus.Skipped };
                    }

                    serviceResult = SystemdServiceInstaller.Install(joinConfig!);

                    if (!serviceResult.Installed)
                    {
                        reporter.Detail($"&e{serviceResult.Message}&r");
                        return new ConfigureStepResult { Status = ConfigureStepStatus.Warning };
                    }

                    return StepOk(
                        $"&a{serviceResult.Message}&r",
                        serviceResult.UnitPath is not null
                            ? $"&7unit &8→&r &f{serviceResult.UnitPath}&r"
                            : "&7unit installed&r",
                        serviceResult.Started
                            ? "&7status &astarted&r · &7enabled on boot&r"
                            : "&7status &einstalled&r &8(start manually)&r");
                })
                .Run(() =>
                {
                    if (joinConfig is null)
                        return null;

                    return BuildSummary(joinConfig, serviceResult, installService);
                });

            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            ConfigureSequence.RenderFailure(ex.Message);
            return 1;
        }
    }

    public static string DecodeJoinData(string joinData)
    {
        var normalized = joinData.Trim();

        if (normalized.StartsWith('\''))
            normalized = normalized.Trim('\'');

        if (normalized.StartsWith('"'))
            normalized = normalized.Trim('"');

        var bytes = Convert.FromBase64String(normalized);
        return Encoding.UTF8.GetString(bytes);
    }

    private static ConfigureSummary BuildSummary(
        Config joinConfig,
        ServiceInstallResult? serviceResult,
        bool installServiceRequested) =>
        new()
        {
            NodeUuid = joinConfig.Uuid,
            PanelUrl = joinConfig.Remote.Panel,
            ConfigPath = joinConfig.FilePath,
            ApiPort = joinConfig.Api.Port,
            Version = StartupBanner.Version,
            SftpEnabled = joinConfig.Sftp.Enabled,
            SftpPort = joinConfig.Sftp.Port,
            FtpEnabled = joinConfig.Ftp.Enabled,
            FtpPort = joinConfig.Ftp.Port,
            ServiceInstalled = serviceResult?.Installed ?? false,
            ServiceStarted = serviceResult?.Started ?? false,
            ServiceSkipped = !installServiceRequested,
        };

    private static bool PromptServiceInstallOnly() =>
        ConfigurePrompts.PromptInstallService(defaultValue: true);

    private static ConfigureStepResult StepOk(params string[] details)
    {
        var result = new ConfigureStepResult();
        result.Details.AddRange(details);
        return result;
    }

    private static string MaskSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "****";

        if (value.Length <= 4)
            return new string('*', value.Length);

        return value[..2] + new string('*', value.Length - 4) + value[^2..];
    }

    private static bool HasFlag(string[] args, params string[] flags) =>
        args.Any(a => flags.Any(f => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)));

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
