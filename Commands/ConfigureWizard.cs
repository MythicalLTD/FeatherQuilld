using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Config.Remote;

namespace FeatherQuilld.Commands;

public enum ConfigureInputMode
{
    OAuth,
    JoinData,
    Manual,
}

public sealed record ConfigureWizardResult
{
    public required ConfigureInputMode Mode { get; init; }
    public string? JoinData { get; init; }
    public Config? JoinConfig { get; init; }
    public bool InstallService { get; init; }
    public NodeTlsCertificate? Tls { get; init; }
}

/// <summary>
/// Interactive setup wizard — OAuth, join-data paste, or manual credentials.
/// </summary>
public static class ConfigureWizard
{
    public static bool IsInteractive =>
        !Console.IsOutputRedirected && Environment.UserInteractive;

    public static ConfigureWizardResult Run(
        bool noService,
        bool installServiceFlag,
        ConfigureOAuthOptions? oauthOptions = null)
    {
        oauthOptions ??= new ConfigureOAuthOptions();

        if (!IsInteractive)
        {
            if (!string.IsNullOrWhiteSpace(oauthOptions.PanelUrl))
            {
                var oauth = ConfigureOAuth.ResolveJoinDataAsync(oauthOptions).GetAwaiter().GetResult();
                return new ConfigureWizardResult
                {
                    Mode = ConfigureInputMode.OAuth,
                    JoinData = oauth.JoinData,
                    Tls = oauth.Tls,
                    InstallService = !noService && (installServiceFlag || SystemdServiceInstaller.CanInstall()),
                };
            }

            throw new InvalidOperationException(
                "Non-interactive shell detected. Pass --join-data, set FEATHERQUILLD_JOIN_DATA, or use --panel-url with OAuth flags.");
        }

        ConfigurePrompts.WriteWelcome();

        var mode = ConfigurePrompts.PromptSetupMode().Mode;

        var result = mode switch
        {
            ConfigureInputMode.OAuth => BuildOAuthResult(oauthOptions),
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

    private static ConfigureWizardResult BuildOAuthResult(ConfigureOAuthOptions oauthOptions)
    {
        var oauth = ConfigureOAuth.ResolveJoinDataAsync(oauthOptions).GetAwaiter().GetResult();
        return new ConfigureWizardResult
        {
            Mode = ConfigureInputMode.OAuth,
            JoinData = oauth.JoinData,
            Tls = oauth.Tls,
        };
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
