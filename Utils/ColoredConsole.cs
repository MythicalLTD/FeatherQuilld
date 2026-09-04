using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace FeatherQuilld.Utils;

/// <summary>
/// Minecraft-style <c>&amp;</c> color codes rendered through Spectre.Console.
/// </summary>
public static partial class ColoredConsole
{
    private static readonly Regex CodePattern = MinecraftCodeRegex();

    public static void Write(string message) =>
        AnsiConsole.Markup(ToMarkup(message));

    public static void WriteLine(string message) =>
        AnsiConsole.MarkupLine(ToMarkup(message));

    public static void WritePlain(string message) =>
        AnsiConsole.Write(message);

    public static void WriteLinePlain(string message) =>
        AnsiConsole.WriteLine(message);

    /// <summary>
    /// Writes <paramref name="literal"/> in the given Minecraft color without
    /// parsing <c>&amp;</c> codes inside it (URLs, paths, tokens).
    /// </summary>
    public static void WriteLineLiteral(string codes, string literal) =>
        AnsiConsole.MarkupLine(LiteralToMarkup(codes, literal));

    public static string LiteralToMarkup(string codes, string literal)
    {
        const char placeholder = '\uE000';
        var styled = ToMarkup(codes + placeholder);
        return styled.Replace(placeholder.ToString(), Markup.Escape(literal), StringComparison.Ordinal);
    }

    public static string StripCodes(string message) =>
        CodePattern.Replace(message, string.Empty);

    /// <summary>
    /// Converts Minecraft <c>&amp;X</c> codes into balanced Spectre markup spans.
    /// </summary>
    public static string ToMarkup(string message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        var output = new StringBuilder(message.Length);
        string? color = null;
        var bold = false;
        var italic = false;
        var underline = false;
        var strikethrough = false;

        var i = 0;
        while (i < message.Length)
        {
            if (message[i] == '&' && i + 1 < message.Length && IsCode(message[i + 1]))
            {
                ApplyCode(char.ToLowerInvariant(message[i + 1]), ref color, ref bold, ref italic, ref underline, ref strikethrough);
                i += 2;
                continue;
            }

            var start = i;
            i++;
            while (i < message.Length)
            {
                if (message[i] == '&' && i + 1 < message.Length && IsCode(message[i + 1]))
                    break;
                i++;
            }

            var text = message[start..i];
            if (text.Length == 0)
                continue;

            var escaped = Markup.Escape(text);
            var style = BuildStyle(color, bold, italic, underline, strikethrough);
            if (style is null)
                output.Append(escaped);
            else
                output.Append('[').Append(style).Append(']').Append(escaped).Append("[/]");
        }

        return output.ToString();
    }

    private static void ApplyCode(
        char code,
        ref string? color,
        ref bool bold,
        ref bool italic,
        ref bool underline,
        ref bool strikethrough)
    {
        switch (code)
        {
            case '0': color = "#000000"; break;
            case '1': color = "#0000AA"; break;
            case '2': color = "#00AA00"; break;
            case '3': color = "#00AAAA"; break;
            case '4': color = "#AA0000"; break;
            case '5': color = "#AA00AA"; break;
            case '6': color = "#FFAA00"; break;
            case '7': color = "#AAAAAA"; break;
            case '8': color = "#555555"; break;
            case '9': color = "#5555FF"; break;
            case 'a': color = "#55FF55"; break;
            case 'b': color = "#55FFFF"; break;
            case 'c': color = "#FF5555"; break;
            case 'd': color = "#FF55FF"; break;
            case 'e': color = "#FFFF55"; break;
            case 'f': color = "#FFFFFF"; break;
            case 'l': bold = true; break;
            case 'o': italic = true; break;
            case 'n': underline = true; break;
            case 'm': strikethrough = true; break;
            case 'k': break; // obfuscated not supported in Spectre
            case 'r':
                color = null;
                bold = false;
                italic = false;
                underline = false;
                strikethrough = false;
                break;
        }
    }

    private static string? BuildStyle(string? color, bool bold, bool italic, bool underline, bool strikethrough)
    {
        if (color is null && !bold && !italic && !underline && !strikethrough)
            return null;

        var parts = new List<string>(5);
        if (bold) parts.Add("bold");
        if (italic) parts.Add("italic");
        if (underline) parts.Add("underline");
        if (strikethrough) parts.Add("strikethrough");
        if (color is not null) parts.Add(color);
        return string.Join(' ', parts);
    }

    private static bool IsCode(char c)
    {
        c = char.ToLowerInvariant(c);
        return (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f') || c is 'k' or 'l' or 'm' or 'n' or 'o' or 'r';
    }

    [GeneratedRegex(@"&[0-9a-fklmnor]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinecraftCodeRegex();
}
