using FeatherQuilld.Commands;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Startup;

namespace FeatherQuilld;

public static class Program
{
    public static Logger? Logger { get; private set; }
    public static Config? Config { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (IsConfigureCommand(args))
        {
            Environment.Exit(ConfigureCommand.Run(args));
            return;
        }

        Config = Config.Load(ResolveConfigPath(args));
        StartupBanner.Print(Config.AppName, quiet: Config.Quiet);

        var host = DaemonHost.Build(args, Config);
        Logger = host.Logger;
        host.LogStartup(Config);

        try
        {
            host.Run();
        }
        catch (Exception ex)
        {
            Logger.Error(LoggerTypes.Application, "Fatal error", ex);
            throw;
        }
    }

    private static bool IsConfigureCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "configure", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveConfigPath(string[] args) =>
        args
            .SkipWhile(a => a != "--config" && a != "-c")
            .Skip(1)
            .FirstOrDefault()
        ?? Environment.GetEnvironmentVariable("FEATHERQUILLD_CONFIG");
}
