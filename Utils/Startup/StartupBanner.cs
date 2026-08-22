using System.Reflection;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace FeatherQuilld.Utils.Startup;

/// <summary>
/// FeatherWings-inspired startup banner: animated FIGlet monogram with a teal
/// gradient sweep, typewriter tagline, and framed metadata panel.
/// </summary>
public static class StartupBanner
{
    private const string Teal = "#2DD4BF";
    private const string Ink = "#F4F4F5";

    private const string Tagline = "Light as a feather, sharp as a quill.";

    public static string Version { get; } =
        typeof(StartupBanner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?.Split('+')[0]
        ?? typeof(StartupBanner).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static void Print(string appName, bool quiet = false, bool animate = true)
    {
        if (quiet)
            return;

        var interactive = !Console.IsOutputRedirected
                          && AnsiConsole.Profile.Capabilities.Ansi;

        AnsiConsole.WriteLine();

        if (animate && interactive)
            RenderAnimated(appName);
        else
            RenderStatic(appName, interactive);

        AnsiConsole.WriteLine();
    }

    private static void RenderAnimated(string appName)
    {
        var monogram = new FigletText(FigletGlyph(appName))
        {
            Justification = Justify.Center,
            LayoutMode = FigletLayoutMode.Smushed,
        };

        AnsiConsole.Live(Align.Center(BuildBody(appName, monogram, taglineVisible: 0, showMeta: false)))
            .AutoClear(false)
            .Start(ctx =>
            {
                SweepFigletColor(appName, monogram, ctx);

                monogram.Color = Color.FromHex(Teal);
                for (var i = 1; i <= Tagline.Length; i++)
                {
                    ctx.UpdateTarget(Align.Center(BuildBody(appName, monogram, i, showMeta: false)));
                    ctx.Refresh();
                    Thread.Sleep(22);
                }

                Thread.Sleep(60);

                ctx.UpdateTarget(Align.Center(BuildBody(appName, monogram, Tagline.Length, showMeta: true)));
                ctx.Refresh();
                Thread.Sleep(120);
            });

        WriteVersionRule(Justify.Center);
    }

    private static void RenderStatic(string appName, bool interactive)
    {
        var monogram = new FigletText(FigletGlyph(appName))
        {
            Justification = Justify.Center,
            LayoutMode = FigletLayoutMode.Smushed,
            Color = Color.FromHex(Teal),
        };

        AnsiConsole.Write(Align.Center(BuildBody(appName, monogram, Tagline.Length, showMeta: true)));

        if (interactive)
            WriteVersionRule(Justify.Left);
    }

    private static Panel BuildBody(string appName, FigletText monogram, int taglineVisible, bool showMeta)
    {
        var rows = new List<IRenderable>
        {
            monogram,
            Align.Center(StyleAppName(appName)),
        };

        if (taglineVisible > 0)
        {
            var visible = Markup.Escape(Tagline[..Math.Min(taglineVisible, Tagline.Length)]);
            rows.Add(Align.Center(new Markup($"[grey italic]{visible}[/]")));
        }

        if (showMeta)
        {
            rows.Add(new Text(""));
            rows.Add(Align.Center(new Markup($"[grey]© {DateTime.UtcNow.Year} FeatherQuilld[/]")));
        }

        return new Panel(new Rows(rows))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0)
            .Header($"[bold {Ink}] daemon startup [/]", Justify.Center);
    }

    private static Markup StyleAppName(string appName)
    {
        var parts = SplitCamelCase(appName);
        var colors = new[] { Teal, Ink, "#5EEAD4" };
        var styled = new StringBuilder();

        for (var i = 0; i < parts.Count; i++)
            styled.Append($"[bold {colors[i % colors.Length]}]{Markup.Escape(parts[i])}[/]");

        return new Markup(styled.ToString());
    }

    private static List<string> SplitCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return [value];

        var parts = new List<string>();
        var current = new StringBuilder().Append(value[0]);

        for (var i = 1; i < value.Length; i++)
        {
            if (char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
            {
                parts.Add(current.ToString());
                current.Clear();
            }

            current.Append(value[i]);
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static string FigletGlyph(string appName)
    {
        var initials = new string(appName.Where(char.IsUpper).ToArray());
        if (initials.Length >= 2)
            return initials[..Math.Min(initials.Length, 4)];

        if (appName.Length <= 4)
            return appName.ToUpperInvariant();

        return appName[..Math.Min(2, appName.Length)].ToUpperInvariant();
    }

    private static void SweepFigletColor(string appName, FigletText monogram, LiveDisplayContext ctx)
    {
        for (var frame = 0; frame < 18; frame++)
        {
            var hue = 150 + frame * 5;
            var (r, g, b) = HslToRgb(hue, 0.72, 0.52);
            monogram.Color = new Color((byte)r, (byte)g, (byte)b);
            ctx.UpdateTarget(Align.Center(BuildBody(appName, monogram, taglineVisible: 0, showMeta: false)));
            ctx.Refresh();
            Thread.Sleep(38);
        }
    }

    private static void WriteVersionRule(Justify justification)
    {
        AnsiConsole.Write(new Rule($"[grey]v{Markup.Escape(Version)}[/]")
        {
            Justification = justification,
            Style = Style.Parse("grey dim"),
        });
        AnsiConsole.WriteLine();
    }

    private static (int R, int G, int B) HslToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return ((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
