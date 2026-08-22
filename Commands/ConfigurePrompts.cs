using FeatherQuilld.Utils;
using Spectre.Console;

namespace FeatherQuilld.Commands;

/// <summary>
/// Spectre.Console prompts styled with FeatherQuilld teal + Minecraft color helpers.
/// </summary>
internal static class ConfigurePrompts
{
    public const string Teal = "#2DD4BF";
    public const string Ink = "#F4F4F5";

    public static void WriteWelcome()
    {
        AnsiConsole.WriteLine();

        var body = new Rows(
            new Markup(ColoredConsole.ToMarkup("&b&lWelcome to FeatherQuilld setup&r")),
            new Text(""),
            new Markup(ColoredConsole.ToMarkup("&7Connect this machine to &fFeatherPanel&7 as a web hosting node.&r")),
            new Markup(ColoredConsole.ToMarkup("&8Use &f↑↓&8 to navigate · &fEnter&8 to confirm · &fEsc&8 to go back where supported&r")));

        AnsiConsole.Write(new Panel(body)
            .Header("[bold] node wizard [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));

        AnsiConsole.WriteLine();
    }

    public static SetupModeOption PromptSetupMode()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<SetupModeOption>()
                .Title(Mc("&b&lHow would you like to configure this node?&r"))
                .PageSize(8)
                .HighlightStyle(new Style(Color.FromHex(Teal), decoration: Decoration.Bold))
                .AddChoices(SetupModeOption.All)
                .UseConverter(o => o.Label));
    }

    public static string PromptJoinData()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(Mc(
                "&7Copy the &fbase64 join-data&7 string from&r\n" +
                "&8Admin → Web Nodes → your node → FeatherQuilld tab&r")))
            .Header("[bold] join-data [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));
        AnsiConsole.WriteLine();

        return AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Paste join-data&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .ValidationErrorMessage(Mc("&cInvalid join-data — expected base64 YAML from FeatherPanel.&r"))
                .Validate(input =>
                {
                    if (string.IsNullOrWhiteSpace(input))
                        return ValidationResult.Error(Mc("&cJoin-data cannot be empty.&r"));

                    try
                    {
                        ConfigureCommand.DecodeJoinData(input);
                        return ValidationResult.Success();
                    }
                    catch (FormatException)
                    {
                        return ValidationResult.Error(Mc("&cNot valid base64. Copy the entire string from the panel.&r"));
                    }
                }));
    }

    public static ManualCredentials PromptManualCredentials()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(Mc(
                "&7Enter credentials from &fAdmin → Web Nodes → FeatherQuilld&r\n" +
                "&8Web node tokens use the &ffqld_&8 prefix.&r")))
            .Header("[bold] manual setup [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));
        AnsiConsole.WriteLine();

        var panel = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Panel URL&r"))
                .DefaultValue("https://panel.example.com")
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .ValidationErrorMessage(Mc("&cPanel URL is required.&r"))
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error(Mc("&cPanel URL is required.&r"))
                    : ValidationResult.Success()));

        var tokenId = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Token ID&r &8(fqld_…)&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .ValidationErrorMessage(Mc("&cToken ID is required.&r"))
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error(Mc("&cToken ID is required.&r"))
                    : ValidationResult.Success()));

        if (!tokenId.StartsWith("fqld_", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine(Mc("&e  ! &7Token should start with &ffqld_&7 for web nodes.&r"));
        }

        var token = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Token secret&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .Secret()
                .ValidationErrorMessage(Mc("&cToken secret is required.&r"))
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error(Mc("&cToken secret is required.&r"))
                    : ValidationResult.Success()));

        var uuidRaw = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Node UUID&r &8(optional — Enter to skip)&r"))
                .AllowEmpty()
                .DefaultValue("")
                .PromptStyle(new Style(Color.FromHex(Teal))));

        Guid uuid = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(uuidRaw))
        {
            if (!Guid.TryParse(uuidRaw, out uuid))
            {
                AnsiConsole.MarkupLine(Mc("&e  ! &7Invalid UUID — a new one will be assigned.&r"));
                uuid = Guid.Empty;
            }
        }

        var portRaw = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7API port&r"))
                .DefaultValue("8989")
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .ValidationErrorMessage(Mc("&cPort must be 1–65535.&r"))
                .Validate(v => int.TryParse(v, out var p) && p is > 0 and <= 65535
                    ? ValidationResult.Success()
                    : ValidationResult.Error(Mc("&cPort must be 1–65535.&r"))));

        _ = int.TryParse(portRaw, out var port);

        return new ManualCredentials(panel.Trim(), tokenId.Trim(), token, uuid, port);
    }

    public static bool PromptInstallService(bool defaultValue = true)
    {
        if (!SystemdServiceInstaller.CanInstall())
            return false;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(Mc(
                "&7Writes &f/etc/systemd/system/featherquilld.service&r\n" +
                "&7Runs &fsystemctl enable --now featherquilld&r\n" +
                "&8Requires root · needs a published binary (not dotnet run)&r")))
            .Header("[bold] systemd [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));
        AnsiConsole.WriteLine();

        return AnsiConsole.Confirm(
            Mc("&7Install and start the &ffeatherquilld&7 systemd service?&r"),
            defaultValue);
    }

    private static string Mc(string message) => ColoredConsole.ToMarkup(message);

    internal sealed record ManualCredentials(
        string Panel,
        string TokenId,
        string Token,
        Guid Uuid,
        int ApiPort);

    internal sealed class SetupModeOption
    {
        public required ConfigureInputMode Mode { get; init; }
        public required string Label { get; init; }

        public static SetupModeOption[] All { get; } =
        [
            new()
            {
                Mode = ConfigureInputMode.JoinData,
                Label = $"[{Teal}]▸[/] [bold {Ink}]Paste join-data[/]      [grey](recommended · FeatherPanel admin)[/]",
            },
            new()
            {
                Mode = ConfigureInputMode.Manual,
                Label = $"[{Teal}]▸[/] [bold {Ink}]Manual credentials[/]   [grey](panel · fqld_ token · secret)[/]",
            },
        ];
    }
}
