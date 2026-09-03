using Spectre.Console;
using Spectre.Console.Rendering;

namespace FeatherQuilld.Utils.Startup;

/// <summary>
/// Boot checklist with a framed ready summary.
/// Falls back to silent execution when quiet or stdout is not a TTY.
/// Step work always runs outside Spectre Live — ASP.NET and our logger write to stdout.
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

    public void Run(BootSummary? summary = null) => Run(() => summary);

    /// <summary>
    /// Runs registered steps, then builds the ready panel.
    /// The factory runs after steps so plugin counts and similar state are current.
    /// </summary>
    public void Run(Func<BootSummary?> summaryFactory)
    {
        var completed = new List<(string Label, BootStepResult Result)>();

        if (_enabled)
            AnsiConsole.MarkupLine($"[bold {Ink}]Boot sequence[/]");

        foreach (var (label, work) in _steps)
        {
            if (_enabled)
            {
                AnsiConsole.MarkupLine($"  [grey]…[/] [bold {Ink}]{Markup.Escape(label)}[/]");
                Console.Out.Flush();
            }

            var reporter = new BootReporter();
            var result = work(reporter);
            foreach (var detail in reporter.Details)
                result.Details.Add(detail);

            completed.Add((label, result));

            if (_enabled)
                RenderStepResult(label, result);
        }

        if (!_enabled)
            return;

        AnsiConsole.WriteLine();
        var summary = summaryFactory();
        if (summary is not null)
            AnsiConsole.Write(RenderSummary(summary));

        AnsiConsole.WriteLine();
    }

    private static void RenderStepResult(string label, BootStepResult result)
    {
        var style = result.Status switch
        {
            BootStepStatus.Warning => "yellow",
            BootStepStatus.Failed => "red",
            BootStepStatus.Skipped => Muted,
            _ => Ink,
        };

        AnsiConsole.MarkupLine($"  {StatusGlyph(result.Status)} [{style}]{Markup.Escape(label)}[/]");
        foreach (var detail in result.Details)
            AnsiConsole.MarkupLine($"      [grey]›[/] [grey]{Markup.Escape(detail)}[/]");
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
