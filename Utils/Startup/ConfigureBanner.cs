using Spectre.Console;
using Spectre.Console.Rendering;

namespace FeatherQuilld.Utils.Startup;

/// <summary>
/// Animated header shown during <c>featherquilld configure</c>.
/// </summary>
public static class ConfigureBanner
{
    private const string Teal = "#2DD4BF";
    private const string Ink = "#F4F4F5";
    private const string Tagline = "Joining web node to FeatherPanel…";

    public static void Print(bool quiet = false)
    {
        if (quiet)
            return;

        var interactive = !Console.IsOutputRedirected
                          && AnsiConsole.Profile.Capabilities.Ansi;

        AnsiConsole.WriteLine();

        if (interactive)
            RenderAnimated();
        else
            RenderStatic();

        AnsiConsole.WriteLine();
    }

    private static void RenderAnimated()
    {
        var monogram = new FigletText("FQ")
        {
            Justification = Justify.Center,
            LayoutMode = FigletLayoutMode.Smushed,
        };

        AnsiConsole.Live(Align.Center(BuildBody(monogram, taglineVisible: 0, showMeta: false)))
            .AutoClear(false)
            .Start(ctx =>
            {
                for (var frame = 0; frame < 14; frame++)
                {
                    var hue = 160 + frame * 4;
                    var (r, g, b) = HslToRgb(hue, 0.72, 0.52);
                    monogram.Color = new Color((byte)r, (byte)g, (byte)b);
                    ctx.UpdateTarget(Align.Center(BuildBody(monogram, 0, false)));
                    ctx.Refresh();
                    Thread.Sleep(36);
                }

                monogram.Color = Color.FromHex(Teal);
                for (var i = 1; i <= Tagline.Length; i++)
                {
                    ctx.UpdateTarget(Align.Center(BuildBody(monogram, i, false)));
                    ctx.Refresh();
                    Thread.Sleep(18);
                }

                Thread.Sleep(50);
                ctx.UpdateTarget(Align.Center(BuildBody(monogram, Tagline.Length, true)));
                ctx.Refresh();
                Thread.Sleep(100);
            });

        AnsiConsole.Write(new Rule($"[grey dim]node setup[/]")
        {
            Justification = Justify.Center,
            Style = Style.Parse("grey dim"),
        });
        AnsiConsole.WriteLine();
    }

    private static void RenderStatic()
    {
        var monogram = new FigletText("FQ")
        {
            Justification = Justify.Center,
            LayoutMode = FigletLayoutMode.Smushed,
            Color = Color.FromHex(Teal),
        };

        AnsiConsole.Write(Align.Center(BuildBody(monogram, Tagline.Length, true)));
    }

    private static Panel BuildBody(FigletText monogram, int taglineVisible, bool showMeta)
    {
        var rows = new List<IRenderable> { monogram };

        if (taglineVisible > 0)
        {
            var visible = Markup.Escape(Tagline[..Math.Min(taglineVisible, Tagline.Length)]);
            rows.Add(Align.Center(new Markup($"[grey italic]{visible}[/]")));
        }

        if (showMeta)
            rows.Add(Align.Center(new Markup($"[bold {Ink}]Configure[/] [grey]·[/] [dim]panel join flow[/]")));

        return new Panel(new Rows(rows))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0)
            .Header($"[bold {Ink}] web node setup [/]", Justify.Center);
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
