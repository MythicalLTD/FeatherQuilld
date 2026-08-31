using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

internal static class WebmailPaths
{
    public const string ContainerName = "featherquilld-webmail";
    public const string ComposeFileName = "docker-compose.yml";
    public const int DefaultPort = 8080;

    public static string Root(AppConfig config) =>
        Path.Combine(config.System.RootDirectory, "webmail");

    public static string ComposeFile(AppConfig config) =>
        Path.Combine(Root(config), ComposeFileName);

    public static string DataDir(AppConfig config) =>
        Path.Combine(Root(config), "data");
}
