using FeatherQuilld.Utils.Config.System;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

internal static class MailPaths
{
    public const string ContainerName = "featherquilld-mailserver";
    public const string ComposeFileName = "docker-compose.yml";

    public static string Root(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.System.Mail.DataPath)
            ? Path.Combine(config.System.RootDirectory, "mail")
            : config.System.Mail.DataPath.Trim();

    public static string ComposeDir(AppConfig config) => Root(config);

    public static string ComposeFile(AppConfig config) =>
        Path.Combine(Root(config), ComposeFileName);

    public static string ApiKeyPath(AppConfig config) =>
        Path.Combine(Root(config), "api-key");

    public static string MailDataDir(AppConfig config) =>
        Path.Combine(Root(config), "mail-data");

    public static string MailStateDir(AppConfig config) =>
        Path.Combine(Root(config), "mail-state");

    public static string ConfigDir(AppConfig config) =>
        Path.Combine(Root(config), "config");

    public static string DomainsFile(AppConfig config) =>
        Path.Combine(Root(config), "domains.txt");

    public static string AutorespondDir(AppConfig config) =>
        Path.Combine(ConfigDir(config), "vacation");

    public static string SieveConfigPath(AppConfig config, string email) =>
        MailVacationHelper.ConfigSievePath(config, email);
}
