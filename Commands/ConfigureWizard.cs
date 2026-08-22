using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Config.Remote;

namespace FeatherQuilld.Commands;

public enum ConfigureInputMode
{
    JoinData,
    Manual,
}

public sealed record ConfigureWizardResult
{
    public required ConfigureInputMode Mode { get; init; }
    public string? JoinData { get; init; }
    public Config? JoinConfig { get; init; }
    public bool InstallService { get; init; }
}

/// <summary>
/// Interactive setup wizard — arrow-key menus, join-data paste, or manual credentials.
/// </summary>
public static class ConfigureWizard
{
    public static bool IsInteractive =>
        !Console.IsOutputRedirected && Environment.UserInteractive;

    public static ConfigureWizardResult Run(bool noService, bool installServiceFlag)
    {
        if (!IsInteractive)
        {
            throw new InvalidOperationException(
                "Non-interactive shell detected. Pass --join-data or set FEATHERQUILLD_JOIN_DATA.");
        }

        ConfigurePrompts.WriteWelcome();

        var mode = ConfigurePrompts.PromptSetupMode().Mode;

        var result = mode switch
        {
            ConfigureInputMode.JoinData => new ConfigureWizardResult
            {
                Mode = ConfigureInputMode.JoinData,
                JoinData = ConfigurePrompts.PromptJoinData(),
            },
            _ => BuildManualResult(ConfigurePrompts.PromptManualCredentials()),
        };

        var installService = ResolveServiceInstall(noService, installServiceFlag);
        return result with { InstallService = installService };
    }

    private static ConfigureWizardResult BuildManualResult(ConfigurePrompts.ManualCredentials creds)
    {
        var config = new Config
        {
            Uuid = creds.Uuid,
            TokenId = creds.TokenId,
            Token = creds.Token,
            Api = { Port = creds.ApiPort },
            Remote =
            {
                Panel = creds.Panel,
                ConfigPath = RemoteConfig.DefaultConfigPath,
                HealthPath = RemoteConfig.DefaultHealthPath,
            },
        };

        if (config.Uuid == Guid.Empty)
            config.Uuid = Guid.NewGuid();

        config.ValidateJoin();

        return new ConfigureWizardResult
        {
            Mode = ConfigureInputMode.Manual,
            JoinConfig = config,
        };
    }

    private static bool ResolveServiceInstall(bool noService, bool installServiceFlag)
    {
        if (noService)
            return false;

        if (installServiceFlag)
            return SystemdServiceInstaller.CanInstall();

        return ConfigurePrompts.PromptInstallService(defaultValue: true);
    }
}
