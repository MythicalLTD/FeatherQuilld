using FeatherQuilld.Utils;
using FeatherQuilld.Utils.Remote;
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

    public static string PromptPanelUrl()
    {
        AnsiConsole.WriteLine();
        return AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7FeatherPanel URL&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .ValidationErrorMessage(Mc("&cEnter a valid http(s) URL.&r"))
                .Validate(input =>
                {
                    if (string.IsNullOrWhiteSpace(input))
                        return ValidationResult.Error(Mc("&cPanel URL is required.&r"));
                    if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        return ValidationResult.Error(Mc("&cURL must start with http:// or https://&r"));
                    return ValidationResult.Success();
                }));
    }

    public static string PromptCallbackHost(IReadOnlyList<(string Host, string Source)> candidates)
    {
        AnsiConsole.WriteLine();

        if (candidates.Count == 0)
        {
            return AnsiConsole.Prompt(
                new TextPrompt<string>(Mc("&b›&r &7This machine's public IP&r"))
                    .PromptStyle(new Style(Color.FromHex(Teal)))
                    .Validate(v => string.IsNullOrWhiteSpace(v)
                        ? ValidationResult.Error(Mc("&cIP is required.&r"))
                        : ValidationResult.Success()));
        }

        if (candidates.Count == 1)
        {
            var host = candidates[0].Host;
            var confirmed = AnsiConsole.Prompt(
                new TextPrompt<string>(Mc($"&b›&r &7This machine's public IP&r &8(detected {host})&r"))
                    .PromptStyle(new Style(Color.FromHex(Teal)))
                    .DefaultValue(host)
                    .Validate(v => string.IsNullOrWhiteSpace(v)
                        ? ValidationResult.Error(Mc("&cIP is required.&r"))
                        : ValidationResult.Success()));
            return confirmed.Trim();
        }

        var choices = candidates
            .Select(c => $"{c.Host} ({SourceLabel(c.Source)})")
            .Append("Enter a different IP…")
            .ToList();

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(Mc("&b&lThis machine's IP&r"))
                .HighlightStyle(new Style(Color.FromHex(Teal), decoration: Decoration.Bold))
                .AddChoices(choices));

        if (pick.StartsWith("Enter a different", StringComparison.Ordinal))
        {
            return AnsiConsole.Prompt(
                new TextPrompt<string>(Mc("&b›&r &7Public IP&r"))
                    .PromptStyle(new Style(Color.FromHex(Teal)))
                    .Validate(v => string.IsNullOrWhiteSpace(v)
                        ? ValidationResult.Error(Mc("&cIP is required.&r"))
                        : ValidationResult.Success())).Trim();
        }

        return pick.Split(' ', 2)[0];
    }

    public static CreateWebNodeRequest PromptWebNodeDetails(
        IReadOnlyList<AdminPanelLocation> locations,
        string nodeIp,
        ConfigureOAuthOptions options)
    {
        var hostname = System.Net.Dns.GetHostName();
        if (string.IsNullOrWhiteSpace(hostname))
            hostname = "node";

        var locationChoice = AnsiConsole.Prompt(
            new SelectionPrompt<AdminPanelLocation>()
                .Title(Mc("&b&lWeb location&r"))
                .HighlightStyle(new Style(Color.FromHex(Teal), decoration: Decoration.Bold))
                .AddChoices(locations)
                .UseConverter(l => $"[{Teal}]{l.Id}[/] {l.Name}"));

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Node name&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .DefaultValue(string.IsNullOrWhiteSpace(options.NodeName) ? hostname : options.NodeName!)
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error(Mc("&cName is required.&r"))
                    : ValidationResult.Success()));

        var fqdn = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7FQDN or public hostname&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .DefaultValue(string.IsNullOrWhiteSpace(options.NodeFqdn)
                    ? (string.IsNullOrWhiteSpace(nodeIp) ? hostname : nodeIp)
                    : options.NodeFqdn!)
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error(Mc("&cFQDN is required.&r"))
                    : ValidationResult.Success()));

        var daemonListen = options.DaemonListen is > 0
            ? options.DaemonListen.Value
            : AnsiConsole.Prompt(
                new TextPrompt<int>(Mc("&b›&r &7Daemon API port&r"))
                    .PromptStyle(new Style(Color.FromHex(Teal)))
                    .DefaultValue(8989));

        var sftpPort = options.SftpPort is > 0
            ? options.SftpPort.Value
            : AnsiConsole.Prompt(
                new TextPrompt<int>(Mc("&b›&r &7SFTP port&r"))
                    .PromptStyle(new Style(Color.FromHex(Teal)))
                    .DefaultValue(2222));

        return new CreateWebNodeRequest
        {
            Name = name.Trim(),
            Fqdn = fqdn.Trim(),
            LocationId = locationChoice.Id,
            Scheme = "https",
            Public = true,
            DaemonListen = daemonListen,
            SftpPort = sftpPort,
            DaemonBase = string.IsNullOrWhiteSpace(options.DaemonBase)
                ? "/var/lib/featherquilld"
                : options.DaemonBase.Trim(),
            Description = $"FeatherQuilld node at {nodeIp}",
            SftpEnabled = true,
        };
    }

    public static bool PromptRevokeOAuthKey(string keyName)
    {
        var description = "Recommended — the node is registered and this key is no longer needed.";
        if (!string.IsNullOrWhiteSpace(keyName))
            description = $"{description} ({keyName})";

        AnsiConsole.WriteLine();
        return AnsiConsole.Confirm(Mc($"&7Delete the temporary OAuth API key?&r &8{description}&r"), true);
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
                "&7Enter credentials from &fAdmin → Web Nodes&7.&r\n" +
                "&8Token ID starts with &ffqld_&8.&r")))
            .Header("[bold] manual [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));
        AnsiConsole.WriteLine();

        var panel = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Panel URL&r"))
                .PromptStyle(new Style(Color.FromHex(Teal))));

        var tokenId = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Token ID&r"))
                .PromptStyle(new Style(Color.FromHex(Teal))));

        var token = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Token secret&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .Secret());

        var uuidText = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Node UUID&r &8(optional — leave blank to generate)&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .AllowEmpty());

        var apiPort = AnsiConsole.Prompt(
            new TextPrompt<int>(Mc("&b›&r &7API port&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .DefaultValue(8989));

        Guid.TryParse(uuidText, out var uuid);

        return new ManualCredentials(panel.Trim(), tokenId.Trim(), token.Trim(), uuid, apiPort);
    }

    public static bool PromptInstallService(bool defaultValue)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(Mc(
                "&7Install &ffeatherquilld.service&7 so the daemon starts on boot.&r")))
            .Header("[bold] systemd [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));
        AnsiConsole.WriteLine();

        return AnsiConsole.Confirm(
            Mc("&7Install and start the &ffeatherquilld&7 systemd service?&r"),
            defaultValue);
    }

    private static string SourceLabel(string source) => source switch
    {
        "outbound" => "outbound/public IP",
        "interface" => "network interface",
        "environment" => "environment",
        "manual" => "manual",
        _ => source,
    };

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
                Mode = ConfigureInputMode.OAuth,
                Label = $"[{Teal}]▸[/] [bold {Ink}]OAuth quick setup[/]   [grey](recommended · browser authorize)[/]",
            },
            new()
            {
                Mode = ConfigureInputMode.JoinData,
                Label = $"[{Teal}]▸[/] [bold {Ink}]Paste join-data[/]      [grey](FeatherPanel admin)[/]",
            },
            new()
            {
                Mode = ConfigureInputMode.Manual,
                Label = $"[{Teal}]▸[/] [bold {Ink}]Manual credentials[/]   [grey](panel · fqld_ token · secret)[/]",
            },
        ];
    }
}
