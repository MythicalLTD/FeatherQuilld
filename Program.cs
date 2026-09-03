using FeatherQuilld.Commands;
using FeatherQuilld.Utils;
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
        if (IsHelpCommand(args))
        {
            ConfigureSequence.RenderUsage();
            ColoredConsole.WriteLine("&7Start the daemon:&r  &ffeatherquilld&7 [&b--config&7 path]&r");
            ColoredConsole.WriteLine("&7Version:&r           &ffeatherquilld --version&r");
            Environment.Exit(0);
            return;
        }

        if (IsVersionCommand(args))
        {
            Console.WriteLine(StartupBanner.Version);
            Environment.Exit(0);
            return;
        }

        if (IsConfigureCommand(args))
        {
            var configurePath = ResolveConfigPath(args) ?? Config.DefaultPath();
            if (!EnsureRootFor(configurePath))
                return;

            Environment.Exit(ConfigureCommand.Run(args));
            return;
        }

        var configPath = ResolveConfigPath(args) ?? Config.DefaultPath();
        if (!EnsureRootFor(configPath))
            return;

        if (NeedsSetup(configPath))
        {
            if (!ConfigureWizard.IsInteractive)
            {
                PrintNotReady(new ConfigNotReadyException());
                Environment.Exit(1);
                return;
            }

            ColoredConsole.WriteLine("&eNo node config found&r &8→&r &7starting setup wizard&r");
            AnsiConsoleSafeNewline();

            var setupArgs = new List<string> { "--config", configPath, "--override" };
            var exitCode = ConfigureCommand.Run(setupArgs.ToArray());
            if (exitCode != 0)
                Environment.Exit(exitCode);

            if (NeedsSetup(configPath))
            {
                PrintNotReady(new ConfigNotReadyException());
                Environment.Exit(1);
                return;
            }
        }

        try
        {
            Config = Config.Load(configPath);
        }
        catch (Exception ex) when (ex is ConfigNotReadyException or UnauthorizedAccessException)
        {
            PrintNotReady(ex);
            Environment.Exit(1);
            return;
        }

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
            Environment.Exit(1);
        }
    }

    private static bool NeedsSetup(string configPath)
    {
        if (!File.Exists(configPath))
            return true;

        try
        {
            return !Config.Load(configPath).IsJoinedToPanel();
        }
        catch
        {
            return true;
        }
    }

    private static void AnsiConsoleSafeNewline()
    {
        try { Console.WriteLine(); } catch { /* ignore */ }
    }

    private static bool EnsureRootFor(string configPath)
    {
        if (!RootPrivileges.RequiresRoot(configPath) || RootPrivileges.IsRoot())
            return true;

        PrintHint(RootPrivileges.Hint);
        Environment.Exit(1);
        return false;
    }

    private static void PrintNotReady(Exception ex)
    {
        var message = ex is ConfigNotReadyException
            ? ex.Message
            : $"{ex.Message}\n\n{ConfigNotReadyException.Hint}";

        PrintHint(message);
    }

    private static void PrintHint(string message)
    {
        Console.Error.WriteLine();
        ColoredConsole.WriteLine("&c&lFeatherQuilld cannot start&r");
        Console.Error.WriteLine();
        foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
            ColoredConsole.WriteLine(string.IsNullOrEmpty(line) ? "" : $"&7{line}&r");
        Console.Error.WriteLine();
    }

    private static bool IsConfigureCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "configure", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpCommand(string[] args) =>
        args.Length > 0 && args[0] is "-h" or "--help" or "help" or "-?";

    private static bool IsVersionCommand(string[] args) =>
        args.Length > 0 && args[0] is "-v" or "--version" or "version";

    private static string? ResolveConfigPath(string[] args) =>
        args
            .SkipWhile(a => a != "--config" && a != "-c")
            .Skip(1)
            .FirstOrDefault()
        ?? Environment.GetEnvironmentVariable("FEATHERQUILLD_CONFIG");
}
