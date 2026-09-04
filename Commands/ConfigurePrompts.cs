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

    public static async Task<(CreateWebNodeRequest Request, NodeTlsCertificate? Tls)> PromptWebNodeDetailsAsync(
        AdminPanelClient panel,
        IReadOnlyList<AdminPanelLocation> locations,
        string nodeIp,
        string panelUrl,
        ConfigureOAuthOptions options,
        CancellationToken ct = default)
    {
        var hostname = System.Net.Dns.GetHostName();
        if (string.IsNullOrWhiteSpace(hostname))
            hostname = "node";

        var locationId = await PromptWebLocationAsync(panel, locations, options.LocationId, ct)
            .ConfigureAwait(false);

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Node name&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .DefaultValue(string.IsNullOrWhiteSpace(options.NodeName) ? hostname : options.NodeName!)
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error(Mc("&cName is required.&r"))
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

        var (scheme, fqdn, behindProxy, tls) = PromptNodeNetwork(
            panelUrl, nodeIp, hostname, options.NodeFqdn, options.AcmeEmail);

        return (new CreateWebNodeRequest
        {
            Name = name.Trim(),
            Fqdn = fqdn.Trim(),
            LocationId = locationId,
            Scheme = scheme,
            Public = true,
            BehindProxy = behindProxy,
            DaemonListen = daemonListen,
            SftpPort = sftpPort,
            DaemonBase = string.IsNullOrWhiteSpace(options.DaemonBase)
                ? "/var/lib/featherquilld"
                : options.DaemonBase.Trim(),
            Description = $"FeatherQuilld node at {nodeIp}",
            SftpEnabled = true,
        }, tls);
    }

    /// <summary>
    /// Pick an existing web location or create one (same flow as FeatherWings game locations).
    /// </summary>
    public static async Task<int> PromptWebLocationAsync(
        AdminPanelClient panel,
        IReadOnlyList<AdminPanelLocation> locations,
        int? forcedLocationId,
        CancellationToken ct = default)
    {
        if (forcedLocationId is > 0)
            return forcedLocationId.Value;

        AnsiConsole.WriteLine();

        if (locations.Count == 0)
        {
            ColoredConsole.WriteLine("&8No web locations on the panel yet create one to continue.&r");
            AnsiConsole.WriteLine();
            return await PromptCreateWebLocationAsync(panel, ct).ConfigureAwait(false);
        }

        var choices = locations
            .Select(l => new LocationMenuChoice
            {
                Id = l.Id,
                Label = FormatLocationLabel(l),
            })
            .Append(new LocationMenuChoice
            {
                Id = null,
                Label = $"[{Teal}]▸[/] [bold {Ink}]Create new web location…[/]",
            })
            .ToList();

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<LocationMenuChoice>()
                .Title(Mc("&b&lWeb location&r"))
                .HighlightStyle(new Style(Color.FromHex(Teal), decoration: Decoration.Bold))
                .AddChoices(choices)
                .UseConverter(c => c.Label));

        if (pick.Id is null)
            return await PromptCreateWebLocationAsync(panel, ct).ConfigureAwait(false);

        return pick.Id.Value;
    }

    public static async Task<int> PromptCreateWebLocationAsync(
        AdminPanelClient panel,
        CancellationToken ct = default)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(Mc(
                "&7Create a &fweb&7 location on FeatherPanel (Admin → Locations).&r\n" +
                "&8Same API as FeatherWings type is set to &fweb&8 automatically.&r")))
            .Header("[bold] new location [/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex(Teal))
            .Padding(1, 0));
        AnsiConsole.WriteLine();

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Location name&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .Validate(v =>
                {
                    var trimmed = v.Trim();
                    if (trimmed.Length < 2)
                        return ValidationResult.Error(Mc("&cName must be at least 2 characters.&r"));
                    if (trimmed.Length > 255)
                        return ValidationResult.Error(Mc("&cName must be at most 255 characters.&r"));
                    return ValidationResult.Success();
                }));

        var description = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Description&r &8(optional)&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .AllowEmpty());

        var flagCode = AnsiConsole.Prompt(
            new TextPrompt<string>(Mc("&b›&r &7Flag code&r &8(optional e.g. us, de, at)&r"))
                .PromptStyle(new Style(Color.FromHex(Teal)))
                .AllowEmpty());

        var location = await panel.CreateWebLocationAsync(new CreateLocationRequest
        {
            Name = name.Trim(),
            Type = "web",
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            FlagCode = string.IsNullOrWhiteSpace(flagCode) ? null : flagCode.Trim(),
        }, ct).ConfigureAwait(false);

        ColoredConsole.Write("&a✓&r &7Created location &f");
        ColoredConsole.WriteLineLiteral("&f", $"{location.Name} (#{location.Id})");
        AnsiConsole.WriteLine();
        return location.Id;
    }

    /// <summary>Scheme / FQDN / behind_proxy prompts aligned with FeatherWings configure.</summary>
    public static (string Scheme, string Fqdn, bool BehindProxy, NodeTlsCertificate? Tls) PromptNodeNetwork(
        string panelUrl,
        string nodeIp,
        string hostname,
        string? forcedFqdn,
        string? acmeEmail = null)
    {
        AnsiConsole.WriteLine();
        var behindProxy = AnsiConsole.Confirm(
            Mc("&7Use a reverse proxy for this node?&r &8(Cloudflare / nginx / Caddy terminating TLS)&r"),
            false);

        var panelHttps = PanelUsesHttps(panelUrl);
        var panelHostIsIp = PanelHostIsIp(panelUrl);

        string scheme;
        string defaultFqdn;
        string fqdnPrompt;
        var needsLetsEncrypt = false;

        if (behindProxy)
        {
            scheme = "https";
            defaultFqdn = string.IsNullOrWhiteSpace(forcedFqdn) ? "" : forcedFqdn.Trim();
            fqdnPrompt = "&b›&r &7Domain served by your reverse proxy&r &8(e.g. node.example.com)&r";
        }
        else if (!panelHttps || panelHostIsIp)
        {
            scheme = "http";
            defaultFqdn = !string.IsNullOrWhiteSpace(forcedFqdn)
                ? forcedFqdn.Trim()
                : (!string.IsNullOrWhiteSpace(nodeIp) ? nodeIp : hostname);
            fqdnPrompt = panelHttps
                ? "&b›&r &7FQDN or IP the panel uses to reach this node&r"
                : "&b›&r &7FQDN or IP&r &8(panel is HTTP/IP match how the panel reaches this machine)&r";
        }
        else
        {
            scheme = "https";
            needsLetsEncrypt = true;
            defaultFqdn = string.IsNullOrWhiteSpace(forcedFqdn) ? hostname : forcedFqdn.Trim();
            fqdnPrompt = "&b›&r &7Node FQDN&r &8(hostname with a valid TLS certificate on this machine)&r";
        }

        var fqdnPromptBuilder = new TextPrompt<string>(Mc(fqdnPrompt))
            .PromptStyle(new Style(Color.FromHex(Teal)))
            .Validate(v => string.IsNullOrWhiteSpace(v)
                ? ValidationResult.Error(Mc("&cFQDN is required.&r"))
                : ValidationResult.Success());

        if (!string.IsNullOrWhiteSpace(defaultFqdn))
            fqdnPromptBuilder.DefaultValue(defaultFqdn);

        var fqdn = AnsiConsole.Prompt(fqdnPromptBuilder).Trim();

        NodeTlsCertificate? tls = null;
        if (needsLetsEncrypt)
            tls = ConfigureLetsEncrypt.Ensure(fqdn, acmeEmail, nodeIp);

        return (scheme, fqdn, behindProxy, tls);
    }

    private static string FormatLocationLabel(AdminPanelLocation location)
    {
        // Spectre treats [DE] as markup escape names and flag brackets.
        var name = Markup.Escape(location.Name);
        var flag = string.IsNullOrWhiteSpace(location.FlagCode)
            ? ""
            : " " + Markup.Escape($"[{location.FlagCode.ToUpperInvariant()}]");
        return $"[{Teal}]{location.Id}[/] {name}{flag}";
    }

    private static bool PanelUsesHttps(string panelUrl)
    {
        if (!Uri.TryCreate(panelUrl.Trim(), UriKind.Absolute, out var uri))
            return true;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PanelHostIsIp(string panelUrl)
    {
        if (!Uri.TryCreate(panelUrl.Trim(), UriKind.Absolute, out var uri))
            return false;
        return System.Net.IPAddress.TryParse(uri.Host, out _);
    }

    private sealed class LocationMenuChoice
    {
        public int? Id { get; init; }
        public required string Label { get; init; }
    }

    public static bool PromptRevokeOAuthKey(string keyName)
    {
        var description = "Recommended the node is registered and this key is no longer needed.";
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
                .ValidationErrorMessage(Mc("&cInvalid join-data expected base64 YAML from FeatherPanel.&r"))
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
            new TextPrompt<string>(Mc("&b›&r &7Node UUID&r &8(optional leave blank to generate)&r"))
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
