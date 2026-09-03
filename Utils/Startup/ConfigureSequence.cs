using FeatherQuilld.Utils;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace FeatherQuilld.Utils.Startup;

/// <summary>
/// Animated configure checklist with Minecraft-style detail lines.
/// </summary>
public sealed class ConfigureSequence
{
    private const string Teal = "#2DD4BF";
    private const string Ink = "#F4F4F5";

    private readonly bool _enabled;
    private readonly List<(string Label, Func<ConfigureReporter, ConfigureStepResult> Work)> _steps = [];

    public ConfigureSequence(bool quiet = false)
    {
        _enabled = !quiet
                   && !Console.IsOutputRedirected
                   && AnsiConsole.Profile.Capabilities.Ansi;
    }

    public ConfigureSequence Step(string label, Func<ConfigureReporter, ConfigureStepResult> work)
    {
        _steps.Add((label, work));
        return this;
    }

    public bool Run(Func<ConfigureSummary?>? buildSummary = null)
    {
        if (!_enabled)
        {
            foreach (var (_, work) in _steps)
            {
                var result = work(new ConfigureReporter());
                if (result.Status == ConfigureStepStatus.Failed)
                    return false;
            }

            var quietSummary = buildSummary?.Invoke();
            if (quietSummary is not null)
                RenderSummaryStatic(quietSummary);

            return true;
        }

        var completed = new List<(string Label, ConfigureStepResult Result)>();
        var failed = false;

        AnsiConsole.Live(BuildChecklist(completed, activeIndex: 0, activeLabel: _steps[0].Label))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                for (var i = 0; i < _steps.Count; i++)
                {
                    var (label, work) = _steps[i];
                    ctx.UpdateTarget(BuildChecklist(completed, i, label));
                    ctx.Refresh();

                    var reporter = new ConfigureReporter();
                    var result = RunStepWithAnimation(ctx, completed, i, label, work, reporter);
                    foreach (var detail in reporter.Details)
                        result.Details.Add(detail);

                    completed.Add((label, result));

                    if (result.Status == ConfigureStepStatus.Failed)
                    {
                        failed = true;
                        ctx.UpdateTarget(BuildChecklist(completed, _steps.Count, "Failed"));
                        ctx.Refresh();
                        Thread.Sleep(120);
                        break;
                    }

                    Thread.Sleep(i == _steps.Count - 1 ? 90 : 150);
                }

                if (!failed)
                {
                    ctx.UpdateTarget(BuildChecklist(completed, _steps.Count, "Ready"));
                    ctx.Refresh();
                    Thread.Sleep(180);
                }
            });

        if (!failed)
        {
            var summary = buildSummary?.Invoke();
            if (summary is not null)
                AnsiConsole.Write(RenderSummary(summary));
        }

        AnsiConsole.WriteLine();
        return !failed;
    }

    private static ConfigureStepResult RunStepWithAnimation(
        LiveDisplayContext ctx,
        IReadOnlyList<(string Label, ConfigureStepResult Result)> completed,
        int activeIndex,
        string label,
        Func<ConfigureReporter, ConfigureStepResult> work,
        ConfigureReporter reporter)
    {
        ConfigureStepResult? result = null;
        Exception? stepError = null;

        var workTask = Task.Run(() =>
        {
            try
            {
                result = work(reporter);
            }
            catch (Exception ex)
            {
                stepError = ex;
            }
        });

        var frame = 0;
        while (!workTask.IsCompleted)
        {
            var status = reporter.Status;
            var activeLabel = string.IsNullOrWhiteSpace(status)
                ? label + new string('.', frame % 3 + 1)
                : $"{label} — {status}";

            ctx.UpdateTarget(BuildChecklist(completed, activeIndex, activeLabel, showEllipsis: string.IsNullOrWhiteSpace(status)));
            ctx.Refresh();
            Thread.Sleep(180);
            frame++;
        }

        workTask.GetAwaiter().GetResult();

        if (stepError is not null)
            throw stepError;

        return result ?? new ConfigureStepResult { Status = ConfigureStepStatus.Failed };
    }

    public static void RenderFailure(string message)
    {
        var rows = new List<IRenderable>
        {
            new Markup(ColoredConsole.ToMarkup("&c&lConfigure failed&r")),
            new Text(""),
            new Markup(ColoredConsole.ToMarkup($"&7{Markup.Escape(message)}&r")),
        };

        AnsiConsole.Write(new Panel(new Rows(rows))
            .Header("[bold red] ✗ error [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Red)
            .Padding(1, 0));
        AnsiConsole.WriteLine();
    }

    public static void RenderUsage()
    {
        AnsiConsole.WriteLine();
        ColoredConsole.WriteLine("&e&lUsage:&r");
        ColoredConsole.WriteLine("  &7featherquilld configure&r                              &8Interactive wizard (OAuth / join-data)&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--join-data &f<base64>&r      &8From FeatherPanel&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--panel-url &f<url>&r         &8OAuth quick setup&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--callback-host &f<ip>&r      &8Public IP for OAuth callback&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--location-id &f<id>&r        &8Existing web location (or create interactively)&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--behind-proxy&r              &8Node behind Cloudflare/nginx/Caddy&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--install-service&r           &8Auto-install systemd&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--no-service&r                &8Skip systemd setup&r");
        ColoredConsole.WriteLine("  &7featherquilld configure &b--override&r                  &8Replace existing config&r");
        AnsiConsole.WriteLine();
    }

    private static IRenderable BuildChecklist(
        IReadOnlyList<(string Label, ConfigureStepResult Result)> completed,
        int activeIndex,
        string activeLabel,
        bool showEllipsis = true)
    {
        var rows = new List<IRenderable>
        {
            new Markup(ColoredConsole.ToMarkup("&b&lPanel join sequence&r")),
            new Text(""),
        };

        for (var i = 0; i < completed.Count; i++)
        {
            var (label, result) = completed[i];
            rows.Add(StepLine(StatusGlyph(result.Status), label, result.Status));

            foreach (var detail in result.Details)
                rows.Add(DetailLine(detail, result.Status));
        }

        if (activeIndex >= completed.Count)
        {
            var suffix = showEllipsis ? " [grey]…[/]" : string.Empty;
            rows.Add(new Markup($"  [bold {Teal}]◉[/] [bold {Ink}]{Markup.Escape(activeLabel)}[/]{suffix}"));
        }

        return new Panel(new Rows(rows))
            .Header($"[bold {Ink}] configuring [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0);
    }

    private static Markup StepLine(string glyph, string label, ConfigureStepStatus status)
    {
        var style = status switch
        {
            ConfigureStepStatus.Warning => "yellow",
            ConfigureStepStatus.Failed => "red",
            ConfigureStepStatus.Skipped => "grey",
            _ => Ink,
        };

        return new Markup($"  {glyph} [{style}]{Markup.Escape(label)}[/]");
    }

    private static Markup DetailLine(string detail, ConfigureStepStatus status)
    {
        var prefix = status switch
        {
            ConfigureStepStatus.Warning => "[yellow]![/]",
            ConfigureStepStatus.Failed => "[red]×[/]",
            ConfigureStepStatus.Skipped => "[grey]-[/]",
            _ => "[grey]›[/]",
        };

        return new Markup($"      {prefix} {ColoredConsole.ToMarkup(detail)}");
    }

    private static string StatusGlyph(ConfigureStepStatus status) => status switch
    {
        ConfigureStepStatus.Success => $"[bold {Teal}]✓[/]",
        ConfigureStepStatus.Warning => "[bold yellow]![/]",
        ConfigureStepStatus.Failed => "[bold red]✗[/]",
        ConfigureStepStatus.Skipped => "[grey]–[/]",
        _ => "[grey]?[/]",
    };

    private static Panel RenderSummary(ConfigureSummary summary)
    {
        var rows = new List<IRenderable>
        {
            Align.Center(new Markup(ColoredConsole.ToMarkup("&a&lNode configured successfully&r"))),
            new Text(""),
            new Markup(ColoredConsole.ToMarkup($"&7node&r   &b{summary.NodeUuid}&r")),
            new Markup(ColoredConsole.ToMarkup($"&7panel&r  &f{Markup.Escape(summary.PanelUrl)}&r")),
            new Markup(ColoredConsole.ToMarkup($"&7config&r &8→&r &f{Markup.Escape(summary.ConfigPath)}&r")),
            new Markup(ColoredConsole.ToMarkup($"&7api&r    &a:{summary.ApiPort}&r &8(&7v{Markup.Escape(summary.Version)}&8)&r")),
        };

        if (summary.SftpEnabled)
            rows.Add(new Markup(ColoredConsole.ToMarkup($"&7sftp&r   &a:{summary.SftpPort}&r &8enabled&r")));

        if (summary.FtpEnabled)
            rows.Add(new Markup(ColoredConsole.ToMarkup($"&7ftp&r    &a:{summary.FtpPort}&r &8enabled&r")));

        if (summary.ServiceInstalled)
        {
            rows.Add(new Markup(ColoredConsole.ToMarkup(
                summary.ServiceStarted
                    ? "&7service &ainstalled &8· &a running&r"
                    : "&7service &ainstalled&r &8· &e not started&r")));
        }
        else if (!summary.ServiceSkipped)
        {
            rows.Add(new Markup(ColoredConsole.ToMarkup(
                "&7service &8not installed&r &8— &7run &fsudo featherquilld configure --install-service&r")));
        }

        rows.Add(new Text(""));
        rows.Add(new Markup(ColoredConsole.ToMarkup(
            summary.ServiceStarted
                ? "&8Daemon is live — &fsystemctl status featherquilld&r"
                : "&8Start the daemon: &fsystemctl start featherquilld&r &8(or &ffeatherquilld&8 for foreground)&r")));

        return new Panel(new Rows(rows))
            .Header($"[bold {Teal}] ready [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0);
    }

    private static void RenderSummaryStatic(ConfigureSummary summary)
    {
        ColoredConsole.WriteLine("&a&lNode configured successfully&r");
        ColoredConsole.WriteLine($"&7node&r   &b{summary.NodeUuid}&r");
        ColoredConsole.WriteLine($"&7panel&r  &f{summary.PanelUrl}&r");
        ColoredConsole.WriteLine($"&7config&r &8→&r &f{summary.ConfigPath}&r");
        ColoredConsole.WriteLine($"&7api&r    &a:{summary.ApiPort}&r");
    }
}
