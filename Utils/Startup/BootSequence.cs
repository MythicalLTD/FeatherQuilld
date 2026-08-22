using Spectre.Console;
using Spectre.Console.Rendering;

namespace FeatherQuilld.Utils.Startup;

/// <summary>
/// Animated boot checklist with per-step details and a framed ready summary.
/// Falls back to silent execution when quiet or stdout is not a TTY.
/// </summary>
public sealed class BootSequence
{
    private const string Teal = "#2DD4BF";
    private const string Ink = "#F4F4F5";
    private const string Muted = "grey";

    private readonly bool _enabled;
    private readonly List<(string Label, Func<BootReporter, BootStepResult> Work)> _steps = [];

    public BootSequence(bool quiet)
    {
        _enabled = !quiet
                   && !Console.IsOutputRedirected
                   && AnsiConsole.Profile.Capabilities.Ansi;
    }

    public BootSequence Step(string label, Action work) =>
        Step(label, _ =>
        {
            work();
            return new BootStepResult();
        });

    public BootSequence Step(string label, Func<BootReporter, BootStepResult> work)
    {
        _steps.Add((label, work));
        return this;
    }

    public void Run(BootSummary? summary = null)
    {
        if (!_enabled)
        {
            foreach (var (_, work) in _steps)
                work(new BootReporter());
            return;
        }

        var completed = new List<(string Label, BootStepResult Result)>();

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

                    var reporter = new BootReporter();
                    var result = work(reporter);
                    foreach (var detail in reporter.Details)
                        result.Details.Add(detail);

                    completed.Add((label, result));
                    Thread.Sleep(i == _steps.Count - 1 ? 80 : 140);
                }

                ctx.UpdateTarget(BuildChecklist(completed, _steps.Count, "Ready"));
                ctx.Refresh();
                Thread.Sleep(160);
            });

        if (summary is not null)
            AnsiConsole.Write(RenderSummary(summary));

        AnsiConsole.WriteLine();
    }

    private static IRenderable BuildChecklist(
        IReadOnlyList<(string Label, BootStepResult Result)> completed,
        int activeIndex,
        string activeLabel)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[bold {Ink}]Boot sequence[/]"),
            new Text(""),
        };

        for (var i = 0; i < completed.Count; i++)
        {
            var (label, result) = completed[i];
            rows.Add(StepLine(StatusGlyph(result.Status), label, result.Status));

            foreach (var detail in result.Details)
                rows.Add(DetailLine(detail, result.Status));
        }

        if (activeIndex < completed.Count + 1 && activeIndex >= completed.Count)
            rows.Add(new Markup($"  [bold {Teal}]◉[/] [bold {Ink}]{Markup.Escape(activeLabel)}[/] [grey]…[/]"));

        return new Panel(new Rows(rows))
            .Header($"[bold {Ink}] wiring up [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0);
    }

    private static Markup StepLine(string glyph, string label, BootStepStatus status)
    {
        var style = status switch
        {
            BootStepStatus.Warning => "yellow",
            BootStepStatus.Failed => "red",
            BootStepStatus.Skipped => Muted,
            _ => Ink,
        };

        return new Markup($"  {glyph} [{style}]{Markup.Escape(label)}[/]");
    }

    private static Markup DetailLine(string detail, BootStepStatus status)
    {
        var prefix = status switch
        {
            BootStepStatus.Warning => "[yellow]![/]",
            BootStepStatus.Failed => "[red]×[/]",
            BootStepStatus.Skipped => "[grey]-[/]",
            _ => "[grey]›[/]",
        };

        return new Markup($"      {prefix} [grey]{Markup.Escape(detail)}[/]");
    }

    private static string StatusGlyph(BootStepStatus status) => status switch
    {
        BootStepStatus.Success => $"[bold {Teal}]✓[/]",
        BootStepStatus.Warning => "[bold yellow]![/]",
        BootStepStatus.Failed => "[bold red]✗[/]",
        BootStepStatus.Skipped => "[grey]–[/]",
        _ => "[grey]?[/]",
    };

    private static Panel RenderSummary(BootSummary summary)
    {
        var rows = new List<IRenderable>
        {
            Align.Center(new Markup($"[bold {Ink}]{Markup.Escape(summary.AppName)}[/] [grey]v{Markup.Escape(summary.Version)}[/]")),
            new Text(""),
            new Markup($"  [grey]listen[/]   [bold {Teal}]{Markup.Escape(summary.ListenAddress)}[/]"),
            new Markup($"  [grey]config[/]   [dim]{Markup.Escape(summary.ConfigPath)}[/]"),
        };

        if (summary.DocsEnabled)
            rows.Add(new Markup($"  [grey]docs[/]     [dim]/scalar[/]"));

        rows.Add(new Markup(
            summary.PluginCount == 0
                ? "  [grey]plugins[/]  [dim]none loaded[/]"
                : $"  [grey]plugins[/]  [bold {Teal}]{summary.PluginCount}[/] [dim]({Markup.Escape(string.Join(", ", summary.Plugins))})[/]"));

        return new Panel(new Rows(rows))
            .Header($"[bold {Teal}] ready [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0);
    }
}
