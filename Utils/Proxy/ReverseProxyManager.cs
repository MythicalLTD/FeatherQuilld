using System.Text;
using System.Text.RegularExpressions;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Writes reverse-proxy config (Caddy / Nginx / Traefik) for WebSpace domains + optional automatic HTTPS.
/// Reload is best-effort via the provider CLI when the binary is available.
/// </summary>
public sealed class ReverseProxyManager
{
    private readonly AppConfig _config;
    private readonly AppLogger? _logger;
    private readonly NginxAcmeService? _acme;
    private readonly IEventBus _events;
    private readonly object _gate = new();

    public ReverseProxyManager(AppConfig config, AppLogger? logger = null, NginxAcmeService? acme = null, IEventBus? events = null)
    {
        _config = config;
        _logger = logger;
        _acme = acme;
        _events = events.OrNoOp();
    }

    public string NormalizedProvider =>
        (_config.System.Proxy.Provider ?? "caddy").Trim().ToLowerInvariant() switch
        {
            "nginx" => "nginx",
            "traefik" => "traefik",
            _ => "caddy",
        };

    public void Rebuild(IEnumerable<WebSpace> spaces)
    {
        var snapshot = spaces as IList<WebSpace> ?? spaces.ToList();
        _events.WithHooks(
            new ProxyRebuildBeforeEvent
            {
                WebSpaceCount = snapshot.Count,
                Provider = NormalizedProvider,
            },
            err => new ProxyRebuildAfterEvent
            {
                WebSpaceCount = snapshot.Count,
                Provider = NormalizedProvider,
                Error = err,
            },
            () => RebuildCore(snapshot));
    }

    private void RebuildCore(IList<WebSpace> spaces)
    {
        if (!_config.System.Proxy.Enabled)
        {
            _logger?.Debug(LoggerTypes.Proxy, "Reverse proxy disabled skip rebuild");
            return;
        }

        lock (_gate)
        {
            var list = spaces.ToList();
            var provider = NormalizedProvider;

            if (_acme is not null)
            {
                _acme.EnsureChallengeLayout();
                var sslSpaces = list.Where(s => s.Ssl && !UsesCustomSsl(s)).ToList();

                foreach (var space in sslSpaces.Where(UsesDns01Ssl))
                {
                    var email = space.ResolveAcmeEmail(_config.System.Proxy.AcmeEmail);
                    if (string.IsNullOrWhiteSpace(email))
                        continue;
                    var apex = ResolveApexDomain(space);
                    if (string.IsNullOrWhiteSpace(apex))
                        continue;
                    try
                    {
                        _acme.EnsureWildcardCertAsync(space.Uuid, apex, email).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(LoggerTypes.Proxy, $"ACME DNS-01 ensure: {ex.Message}");
                    }
                }

                if (provider == "nginx")
                {
                    foreach (var group in sslSpaces.Where(s => !UsesDns01Ssl(s)).GroupBy(
                                 s => s.ResolveAcmeEmail(_config.System.Proxy.AcmeEmail),
                                 StringComparer.OrdinalIgnoreCase))
                    {
                        var email = group.Key;
                        if (string.IsNullOrWhiteSpace(email))
                            continue;

                        var sslDomains = group
                            .SelectMany(s => EffectiveRoutes(s).Where(r => r.Type != "redirect").Select(r => r.Domain))
                            .Where(d => !string.IsNullOrWhiteSpace(d))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (sslDomains.Count == 0)
                            continue;

                        try
                        {
                            _acme.EnsureCertsAsync(sslDomains, email: email).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warning(LoggerTypes.Proxy, $"ACME ensure: {ex.Message}");
                        }
                    }
                }
            }

            var path = ResolveConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var body = provider switch
            {
                "nginx" => BuildNginx(list),
                "traefik" => BuildTraefik(list),
                _ => BuildCaddy(list),
            };

            File.WriteAllText(path, body);
            _logger?.Info(LoggerTypes.Proxy, $"Wrote proxy config → {path}");
            _logger?.Debug(LoggerTypes.Proxy, body.Length > 500 ? body[..500] + "…" : body);

            foreach (var space in list)
            {
                ProxyAccessLogs.EnsureDir(_config.System.RootDirectory, space.Uuid);
                if (string.Equals(space.Runtime, "php", StringComparison.OrdinalIgnoreCase))
                    WebSpaceSiteFiles.WriteApacheAddons(WebSpaceDataPath(space), space);
            }

            TryReload();
        }
    }

    /// <summary>Build provider config without writing (for tests).</summary>
    public string BuildConfig(IEnumerable<WebSpace> spaces) =>
        NormalizedProvider switch
        {
            "nginx" => BuildNginx(spaces),
            "traefik" => BuildTraefik(spaces),
            _ => BuildCaddy(spaces),
        };

    private string ResolveConfigPath()
    {
        var configured = _config.System.Proxy.ConfigPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var fileName = NormalizedProvider switch
        {
            "nginx" => "nginx.conf",
            "traefik" => "traefik-dynamic.yml",
            _ => "Caddyfile",
        };
        return Path.Combine(_config.System.RootDirectory, "proxy", fileName);
    }

    private string ContentRoot(WebSpace space, WebSpaceDomainRoute? route = null)
    {
        var basePath = WebSpaceDataPath(space);
        var rel = route is not null && !string.IsNullOrWhiteSpace(route.DocumentRoot)
            ? route.DocumentRoot
            : space.DocumentRoot;
        return WebSpaceStore.ResolveContentRootPath(basePath, rel);
    }

    private string BuildCaddy(IEnumerable<WebSpace> spaces)
    {
        var sb = new StringBuilder();
        var email = _config.System.Proxy.AcmeEmail?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            sb.AppendLine("{");
            sb.AppendLine($"\temail {email}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine("# Generated by FeatherQuilld do not edit by hand");
        sb.AppendLine();

        var any = false;
        foreach (var space in spaces.OrderBy(s => s.CreatedAt))
        {
            var routes = EffectiveRoutes(space);
            if (routes.Count == 0)
            {
                _logger?.Debug(LoggerTypes.Proxy, $"WebSpace {space.Uuid} has no domains skip");
                continue;
            }

            var appRoutes = routes.Where(r => !string.Equals(r.Type, "redirect", StringComparison.OrdinalIgnoreCase)).ToList();
            var redirectRoutes = routes.Where(r => string.Equals(r.Type, "redirect", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var route in appRoutes)
            {
                any = true;
                sb.AppendLine(route.Domain);
                sb.AppendLine("{");

                if (!space.Ssl)
                {
                    sb.AppendLine("\ttls internal");
                }
                else if (UsesCustomSsl(space) || UsesDns01Ssl(space))
                {
                    var files = ResolveSslFiles(space, route.Domain);
                    if (files is not null)
                        sb.AppendLine($"\ttls {files.Value.cert} {files.Value.key}");
                }
                else if (!string.IsNullOrWhiteSpace(space.AcmeEmail))
                {
                    sb.AppendLine($"\ttls {space.AcmeEmail.Trim()}");
                }

                AppendCaddyWaf(sb, space);
                AppendCaddyBandwidthQuota(sb, space);

                var accessLog = ProxyAccessLogs.AccessLogPath(_config.System.RootDirectory, space.Uuid, route.Domain);
                sb.AppendLine("\tlog {");
                sb.AppendLine($"\t\toutput file {accessLog}");
                sb.AppendLine("\t\tformat json");
                sb.AppendLine("\t}");

                if (space.IsSuspended() || space.IsBandwidthOverQuota())
                {
                    sb.AppendLine("}");
                    sb.AppendLine();
                    continue;
                }

                if (space.BackendPort > 0)
                {
                    var upstream = BackendHostResolver.ResolveUpstream(_config.System.Proxy, space);
                    sb.AppendLine($"\treverse_proxy {upstream}:{space.BackendPort}");
                }
                else
                {
                    var root = ContentRoot(space, route);
                    sb.AppendLine($"\troot * {root}");
                    sb.AppendLine("\tfile_server");
                    sb.AppendLine($"\t# WebSpace {space.Uuid} webplate={space.WebPlateId} backend_port unset");
                }

                sb.AppendLine("}");
                sb.AppendLine();
            }

            foreach (var redirect in redirectRoutes)
            {
                any = true;
                var target = string.IsNullOrWhiteSpace(redirect.RedirectTarget)
                    ? "/"
                    : redirect.RedirectTarget.Trim();
                sb.AppendLine(redirect.Domain);
                sb.AppendLine("{");
                sb.AppendLine($"\tredir {target}{{uri}} permanent");
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        if (!any)
            sb.AppendLine("# No WebSpaces with domains yet");

        return sb.ToString();
    }

    private string BuildNginx(IEnumerable<WebSpace> spaces)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by FeatherQuilld do not edit by hand");
        sb.AppendLine();

        var challengeRoot = _acme?.AcmeWwwRoot
                            ?? Path.Combine(_config.System.RootDirectory, "acme", "www");

        foreach (var space in spaces.OrderBy(s => s.CreatedAt))
        {
            var routes = EffectiveRoutes(space);
            if (routes.Count == 0)
                continue;

            foreach (var route in routes)
            {
                var domain = route.Domain;
                if (string.Equals(route.Type, "redirect", StringComparison.OrdinalIgnoreCase))
                {
                    var target = string.IsNullOrWhiteSpace(route.RedirectTarget) ? "/" : route.RedirectTarget.Trim();
                    sb.AppendLine("server {");
                    sb.AppendLine("    listen 80;");
                    sb.AppendLine($"    server_name {domain};");
                    sb.AppendLine($"    return 301 {target}$request_uri;");
                    sb.AppendLine("}");
                    sb.AppendLine();

                    // HTTPS on redirect hosts so www↔apex works when clients hit https://www.…
                    if (space.Ssl)
                    {
                        var redirectSsl = ResolveSslFiles(space, domain);
                        sb.AppendLine("server {");
                        sb.AppendLine("    listen 443 ssl;");
                        sb.AppendLine($"    server_name {domain};");
                        if (redirectSsl is not null && File.Exists(redirectSsl.Value.cert) && File.Exists(redirectSsl.Value.key))
                        {
                            sb.AppendLine($"    ssl_certificate     {redirectSsl.Value.cert};");
                            sb.AppendLine($"    ssl_certificate_key {redirectSsl.Value.key};");
                        }
                        sb.AppendLine($"    return 301 {target}$request_uri;");
                        sb.AppendLine("}");
                        sb.AppendLine();
                    }

                    continue;
                }

                var accessLog = ProxyAccessLogs.AccessLogPath(_config.System.RootDirectory, space.Uuid, domain);
                var errorLog = ProxyAccessLogs.ErrorLogPath(_config.System.RootDirectory, space.Uuid, domain);

                // Always expose HTTP for ACME challenges (and non-SSL sites).
                sb.AppendLine("server {");
                sb.AppendLine("    listen 80;");
                sb.AppendLine($"    server_name {domain};");
                sb.AppendLine($"    access_log {accessLog};");
                sb.AppendLine($"    error_log {errorLog};");
                if (space.WafEnabled)
                    AppendNginxWafDirectives(sb, space);
                sb.AppendLine("    location ^~ /.well-known/acme-challenge/ {");
                sb.AppendLine($"        root {challengeRoot};");
                sb.AppendLine("        default_type text/plain;");
                sb.AppendLine("    }");

                if (!space.Ssl)
                    AppendNginxAppLocation(sb, space, route);
                else
                    sb.AppendLine("    location / { return 301 https://$host$request_uri; }");

                sb.AppendLine("}");
                sb.AppendLine();

                if (!space.Ssl)
                    continue;

                var sslFiles = ResolveSslFiles(space, domain);
                var crt = sslFiles?.cert ?? NginxAcmeService.CertPath(domain);
                var key = sslFiles?.key ?? NginxAcmeService.KeyPath(domain);
                sb.AppendLine("server {");
                sb.AppendLine("    listen 443 ssl;");
                sb.AppendLine($"    server_name {domain};");
                sb.AppendLine($"    access_log {accessLog};");
                sb.AppendLine($"    error_log {errorLog};");

                if (File.Exists(crt) && File.Exists(key))
                {
                    sb.AppendLine($"    ssl_certificate     {crt};");
                    sb.AppendLine($"    ssl_certificate_key {key};");
                }
                else
                {
                    _logger?.Warning(LoggerTypes.Proxy,
                        $"nginx SSL enabled for {domain} but certs missing at {crt} / {key}");
                    sb.AppendLine($"    # WARN: SSL enabled but certs missing for {domain}");
                    sb.AppendLine($"    # ssl_certificate     {crt};");
                    sb.AppendLine($"    # ssl_certificate_key {key};");
                }

                if (space.WafEnabled)
                    AppendNginxWafDirectives(sb, space);

                AppendNginxAppLocation(sb, space, route);
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Dynamic Traefik file-provider YAML. Static Traefik config must define entryPoints
    /// <c>web</c>/<c>websecure</c> and optionally <c>certificatesResolvers.featherquilld</c>.
    /// Static WebSpaces under Traefik get a loopback <c>backend_port</c> served by
    /// <see cref="StaticFileServerManager"/>; spaces still missing a port are skipped.
    /// </summary>
    private string BuildTraefik(IEnumerable<WebSpace> spaces)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by FeatherQuilld do not edit by hand");
        sb.AppendLine("# Mount this file as a Traefik file provider (watch: true).");
        sb.AppendLine("http:");
        sb.AppendLine("  routers:");

        var anyRouter = false;
        var middlewares = new StringBuilder();
        var wroteMiddlewareHeader = false;
        var services = new StringBuilder();
        services.AppendLine("  services:");

        foreach (var space in spaces.OrderBy(s => s.CreatedAt))
        {
            var allRoutes = EffectiveRoutes(space);
            if (allRoutes.Count == 0)
                continue;

            var appRoutes = allRoutes
                .Where(r => !string.Equals(r.Type, "redirect", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var redirectRoutes = allRoutes
                .Where(r => string.Equals(r.Type, "redirect", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (appRoutes.Count > 0)
            {
                if (space.BackendPort <= 0)
                {
                    _logger?.Warning(LoggerTypes.Proxy,
                        $"Traefik skip WebSpace {space.Uuid}: backend_port required (allocate for static via Traefik)");
                }
                else
                {
                    anyRouter = true;
                    var id = "ws-" + space.Uuid.ToString("N")[..12];
                    var hostRule = string.Join(" || ",
                        appRoutes.Select(d => $"Host(`{EscapeYamlScalar(d.Domain)}`)"));

                    if (space.Ssl)
                    {
                        var httpsRedirectId = $"{id}-https-redirect";
                        if (!wroteMiddlewareHeader)
                        {
                            middlewares.AppendLine("  middlewares:");
                            wroteMiddlewareHeader = true;
                        }

                        middlewares.AppendLine($"    {httpsRedirectId}:");
                        middlewares.AppendLine("      redirectScheme:");
                        middlewares.AppendLine("        scheme: https");
                        middlewares.AppendLine("        permanent: true");

                        sb.AppendLine($"    {id}-http:");
                        sb.AppendLine($"      rule: \"{hostRule}\"");
                        sb.AppendLine("      entryPoints:");
                        sb.AppendLine("        - web");
                        sb.AppendLine("      middlewares:");
                        sb.AppendLine($"        - {httpsRedirectId}");
                        sb.AppendLine($"      service: {id}");
                    }

                    sb.AppendLine($"    {id}:");
                    sb.AppendLine($"      rule: \"{hostRule}\"");
                    sb.AppendLine($"      service: {id}");
                    if (space.Ssl)
                    {
                        sb.AppendLine("      entryPoints:");
                        sb.AppendLine("        - websecure");
                        if (UsesDns01Ssl(space) || UsesCustomSsl(space))
                            sb.AppendLine("      tls: {}");
                        else
                        {
                            sb.AppendLine("      tls:");
                            sb.AppendLine("        certResolver: featherquilld");
                        }
                    }
                    else
                    {
                        sb.AppendLine("      entryPoints:");
                        sb.AppendLine("        - web");
                    }

                    if (space.WafEnabled)
                    {
                        var wafMwId = $"{id}-waf";
                        if (!wroteMiddlewareHeader)
                        {
                            middlewares.AppendLine("  middlewares:");
                            wroteMiddlewareHeader = true;
                        }

                        middlewares.AppendLine($"    {wafMwId}:");
                        middlewares.AppendLine("      headers:");
                        middlewares.AppendLine("        stsSeconds: 31536000");
                        middlewares.AppendLine("        forceSTSHeader: true");
                        middlewares.AppendLine("        contentTypeNosniff: true");
                        middlewares.AppendLine("        customFrameOptionsValue: SAMEORIGIN");
                        middlewares.AppendLine("        referrerPolicy: strict-origin-when-cross-origin");
                        middlewares.AppendLine("      buffering:");
                        middlewares.AppendLine("        maxRequestBodyBytes: 10485760");
                        sb.AppendLine("      middlewares:");
                        sb.AppendLine($"        - {wafMwId}");
                    }

                    if (space.WafEnabled && space.WafDenyIps.Count > 0)
                    {
                        var denyId = $"{id}-ipdeny";
                        if (!wroteMiddlewareHeader)
                        {
                            middlewares.AppendLine("  middlewares:");
                            wroteMiddlewareHeader = true;
                        }

                        var clientIp = string.Join(" || ",
                            space.WafDenyIps.Select(ip => $"ClientIP(`{EscapeYamlScalar(ip)}`)"));
                        sb.AppendLine($"    {denyId}:");
                        sb.AppendLine($"      rule: \"({hostRule}) && ({clientIp})\"");
                        sb.AppendLine("      priority: 100");
                        sb.AppendLine($"      service: {id}");
                        if (space.Ssl)
                        {
                            sb.AppendLine("      entryPoints:");
                            sb.AppendLine("        - websecure");
                        }
                        else
                        {
                            sb.AppendLine("      entryPoints:");
                            sb.AppendLine("        - web");
                        }

                        middlewares.AppendLine($"    {denyId}:");
                        middlewares.AppendLine("      ipAllowList:");
                        middlewares.AppendLine("        sourceRange:");
                        middlewares.AppendLine("          - 255.255.255.255/32");
                        sb.AppendLine("      middlewares:");
                        sb.AppendLine($"        - {denyId}");
                    }

                    if (space.WafEnabled && space.WafDenyPaths.Count > 0)
                    {
                        var pathDenyId = $"{id}-pathdeny";
                        if (!wroteMiddlewareHeader)
                        {
                            middlewares.AppendLine("  middlewares:");
                            wroteMiddlewareHeader = true;
                        }

                        var pathRule = string.Join(" || ",
                            space.WafDenyPaths.Select(p => $"PathPrefix(`{EscapeYamlScalar(p)}`)"));
                        sb.AppendLine($"    {pathDenyId}:");
                        sb.AppendLine($"      rule: \"({hostRule}) && ({pathRule})\"");
                        sb.AppendLine("      priority: 90");
                        sb.AppendLine($"      service: {id}");
                        if (space.Ssl)
                        {
                            sb.AppendLine("      entryPoints:");
                            sb.AppendLine("        - websecure");
                        }
                        else
                        {
                            sb.AppendLine("      entryPoints:");
                            sb.AppendLine("        - web");
                        }

                        middlewares.AppendLine($"    {pathDenyId}:");
                        middlewares.AppendLine("      ipAllowList:");
                        middlewares.AppendLine("        sourceRange:");
                        middlewares.AppendLine("          - 255.255.255.255/32");
                        sb.AppendLine("      middlewares:");
                        sb.AppendLine($"        - {pathDenyId}");
                    }

                    services.AppendLine($"    {id}:");
                    services.AppendLine("      loadBalancer:");
                    services.AppendLine("        servers:");
                    var upstream = BackendHostResolver.ResolveUpstream(_config.System.Proxy, space);
                    services.AppendLine($"          - url: \"http://{upstream}:{space.BackendPort}\"");
                }
            }

            for (var i = 0; i < redirectRoutes.Count; i++)
            {
                var redirect = redirectRoutes[i];
                var target = string.IsNullOrWhiteSpace(redirect.RedirectTarget)
                    ? "/"
                    : redirect.RedirectTarget.Trim();
                var routerId = $"ws-rd-{space.Uuid.ToString("N")[..8]}-{i}";
                var mwId = routerId;

                anyRouter = true;

                if (!wroteMiddlewareHeader)
                {
                    middlewares.AppendLine("  middlewares:");
                    wroteMiddlewareHeader = true;
                }

                sb.AppendLine($"    {routerId}:");
                sb.AppendLine($"      rule: \"Host(`{EscapeYamlScalar(redirect.Domain)}`)\"");
                sb.AppendLine("      entryPoints:");
                sb.AppendLine("        - web");
                sb.AppendLine("      middlewares:");
                sb.AppendLine($"        - {mwId}");
                sb.AppendLine("      service: featherquilld-redirect-sink");

                var escapedDomain = Regex.Escape(redirect.Domain);
                var regex = $"^https?://{escapedDomain}(.*)";
                var replacement = BuildTraefikRedirectReplacement(target);

                middlewares.AppendLine($"    {mwId}:");
                middlewares.AppendLine("      redirectRegex:");
                middlewares.AppendLine($"        regex: \"{regex}\"");
                middlewares.AppendLine($"        replacement: \"{EscapeYamlScalar(replacement)}\"");
                middlewares.AppendLine("        permanent: true");
            }
        }

        if (!anyRouter)
        {
            // Traefik v3 rejects empty maps / comment-only http blocks emit a inert placeholder.
            sb.AppendLine("    featherquilld-placeholder:");
            sb.AppendLine("      rule: \"Host(`featherquilld.invalid`)\"");
            sb.AppendLine("      service: featherquilld-placeholder");
            sb.AppendLine("      entryPoints:");
            sb.AppendLine("        - web");
            sb.AppendLine("  services:");
            sb.AppendLine("    featherquilld-placeholder:");
            sb.AppendLine("      loadBalancer:");
            sb.AppendLine("        servers:");
            sb.AppendLine("          - url: \"http://127.0.0.1:9\"");
            sb.AppendLine("    # No WebSpaces with domains + backend_port yet");
            return sb.ToString();
        }

        if (wroteMiddlewareHeader)
            sb.Append(middlewares);

        if (RedirectRoutesNeedSink(spaces))
        {
            services.AppendLine("    featherquilld-redirect-sink:");
            services.AppendLine("      loadBalancer:");
            services.AppendLine("        servers:");
            services.AppendLine("          - url: \"http://127.0.0.1:9\"");
        }

        sb.Append(services);

        var tlsCerts = new StringBuilder();
        foreach (var space in spaces.Where(s => s.Ssl && (UsesDns01Ssl(s) || UsesCustomSsl(s))).OrderBy(s => s.CreatedAt))
        {
            var files = ResolveSslFiles(space, ResolveApexDomain(space));
            if (files is null || !File.Exists(files.Value.cert) || !File.Exists(files.Value.key))
                continue;
            if (tlsCerts.Length == 0)
            {
                tlsCerts.AppendLine("tls:");
                tlsCerts.AppendLine("  certificates:");
            }
            tlsCerts.AppendLine("    - certFile: " + files.Value.cert);
            tlsCerts.AppendLine("      keyFile: " + files.Value.key);
        }
        if (tlsCerts.Length > 0)
        {
            sb.AppendLine();
            sb.Append(tlsCerts);
        }

        return sb.ToString();
    }

    private static bool RedirectRoutesNeedSink(IEnumerable<WebSpace> spaces) =>
        spaces.Any(s => EffectiveRoutes(s).Any(r =>
            string.Equals(r.Type, "redirect", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Traefik <c>redirectRegex</c> replacement mirrors Caddy <c>redir target{uri}</c>.</summary>
    private static string BuildTraefikRedirectReplacement(string target) =>
        target.TrimEnd('/') + "${1}";

    private static string EscapeYamlScalar(string value) =>
        value.Replace("\"", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    private static IReadOnlyList<WebSpaceDomainRoute> EffectiveRoutes(WebSpace space)
    {
        if (space.DomainRoutes.Count > 0)
            return space.DomainRoutes;

        return space.Domains
            .Select((domain, index) => new WebSpaceDomainRoute
            {
                Domain = domain,
                Type = index == 0 ? "primary" : "alias",
            })
            .ToList();
    }

    private static bool UsesCustomSsl(WebSpace space) =>
        string.Equals(space.SslMode, "custom", StringComparison.OrdinalIgnoreCase);

    private static bool UsesDns01Ssl(WebSpace space) =>
        string.Equals(space.SslMode, "dns01", StringComparison.OrdinalIgnoreCase);

    /// <summary>Apex host for www↔apex redirects and wildcard certs (strips leading www.).</summary>
    internal static string ResolveApexDomain(WebSpace space)
    {
        var routes = EffectiveRoutes(space);
        var primary = routes.FirstOrDefault(r =>
            string.Equals(r.Type, "primary", StringComparison.OrdinalIgnoreCase))?.Domain
            ?? routes.FirstOrDefault()?.Domain
            ?? space.Domains.FirstOrDefault()
            ?? "";
        primary = primary.Trim().TrimEnd('.').ToLowerInvariant();
        if (primary.StartsWith("www.", StringComparison.Ordinal))
            return primary[4..];
        return primary;
    }

    private (string cert, string key)? ResolveSslFiles(WebSpace space, string domain)
    {
        if (UsesCustomSsl(space))
            return CustomSslFiles(space);

        if (UsesDns01Ssl(space))
        {
            var apex = ResolveApexDomain(space);
            if (string.IsNullOrWhiteSpace(apex))
                apex = domain;
            return (NginxAcmeService.CertPath(apex), NginxAcmeService.KeyPath(apex));
        }

        if (string.IsNullOrWhiteSpace(domain))
            return null;
        return (NginxAcmeService.CertPath(domain), NginxAcmeService.KeyPath(domain));
    }

    private (string cert, string key)? CustomSslFiles(WebSpace space)
    {
        if (!UsesCustomSsl(space))
            return null;

        var basePath = WebSpaceDataPath(space);
        var cert = Path.Combine(basePath, "ssl", "custom", "cert.pem");
        var key = Path.Combine(basePath, "ssl", "custom", "key.pem");
        if (File.Exists(cert) && File.Exists(key))
            return (cert, key);

        return null;
    }

    private string WebSpaceDataPath(WebSpace space) =>
        _config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota
            ? FeatherQuilld.Utils.WebSpaces.Disk.FuseQuotaLimiter.GetMountPath(_config.System, space.Uuid)
            : Path.Combine(_config.System.Data, space.Uuid.ToString());

    private static void AppendCaddyBandwidthQuota(StringBuilder sb, WebSpace space)
    {
        if (space.IsSuspended())
        {
            sb.AppendLine("\trespond \"WebSpace suspended\" 403");
            return;
        }

        if (!space.IsBandwidthOverQuota())
            return;

        sb.AppendLine("\trespond \"Bandwidth quota exceeded\" 503");
    }

    private static void AppendCaddyWaf(StringBuilder sb, WebSpace space)
    {
        if (!space.WafEnabled)
            return;

        sb.AppendLine("\theader {");
        sb.AppendLine("\t\tStrict-Transport-Security \"max-age=31536000;\"");
        sb.AppendLine("\t\tX-Content-Type-Options nosniff");
        sb.AppendLine("\t\tX-Frame-Options SAMEORIGIN");
        sb.AppendLine("\t\tReferrer-Policy strict-origin-when-cross-origin");
        sb.AppendLine("\t}");
        sb.AppendLine("\trequest_body {");
        sb.AppendLine("\t\tmax_size 10MB");
        sb.AppendLine("\t}");
        if (space.WafDenyIps.Count > 0)
        {
            var list = string.Join(" ", space.WafDenyIps);
            sb.AppendLine($"\t@denied remote_ip {list}");
            sb.AppendLine("\trespond @denied 403");
        }

        if (space.WafDenyPaths.Count > 0)
        {
            var paths = string.Join(" ", space.WafDenyPaths.Select(EscapeCaddyPathMatcher));
            sb.AppendLine($"\t@deniedpath path {paths}");
            sb.AppendLine("\trespond @deniedpath 403");
        }
    }

    private static string EscapeCaddyPathMatcher(string path)
    {
        // Caddy path matchers are space-separated; quote if needed.
        if (path.Contains(' ', StringComparison.Ordinal) || path.Contains('"', StringComparison.Ordinal))
            return "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return path;
    }

    private static void AppendNginxWafDirectives(StringBuilder sb, WebSpace space)
    {
        sb.AppendLine("    add_header Strict-Transport-Security \"max-age=31536000\" always;");
        sb.AppendLine("    add_header X-Content-Type-Options nosniff always;");
        sb.AppendLine("    add_header X-Frame-Options SAMEORIGIN always;");
        sb.AppendLine("    add_header Referrer-Policy \"strict-origin-when-cross-origin\" always;");
        sb.AppendLine("    client_max_body_size 10m;");
        foreach (var ip in space.WafDenyIps)
            sb.AppendLine($"    deny {ip};");
        foreach (var path in space.WafDenyPaths)
        {
            var escaped = EscapeNginxLocation(path);
            sb.AppendLine($"    location ^~ {escaped} {{");
            sb.AppendLine("        deny all;");
            sb.AppendLine("        return 403;");
            sb.AppendLine("    }");
        }

        if (ModSecurityProbe.IsAvailable())
        {
            var rules = ModSecurityProbe.ResolveRulesFile();
            if (!string.IsNullOrWhiteSpace(rules))
            {
                sb.AppendLine("    modsecurity on;");
                sb.AppendLine($"    modsecurity_rules_file {rules};");
            }
        }
    }

    private static string EscapeNginxLocation(string path)
    {
        // Prefix match; quote the URI so special chars are literal.
        return "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private void AppendNginxAppLocation(StringBuilder sb, WebSpace space, WebSpaceDomainRoute? route = null)
    {
        if (space.IsSuspended())
        {
            sb.AppendLine("    location / {");
            sb.AppendLine("        default_type text/plain;");
            sb.AppendLine("        return 403 \"WebSpace suspended\";");
            sb.AppendLine("    }");
            return;
        }

        if (space.IsBandwidthOverQuota())
        {
            sb.AppendLine("    location / {");
            sb.AppendLine("        default_type text/plain;");
            sb.AppendLine("        return 503 \"Bandwidth quota exceeded\";");
            sb.AppendLine("    }");
            return;
        }

        if (space.BackendPort > 0)
        {
            var upstream = BackendHostResolver.ResolveUpstream(_config.System.Proxy, space);
            sb.AppendLine("    location / {");
            sb.AppendLine($"        proxy_pass http://{upstream}:{space.BackendPort};");
            sb.AppendLine("        proxy_set_header Host $host;");
            sb.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
            sb.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
            sb.AppendLine("    }");
        }
        else
        {
            var root = ContentRoot(space, route);
            sb.AppendLine($"    root {root};");
            sb.AppendLine("    index index.html;");
            sb.AppendLine("    location / { try_files $uri $uri/ =404; }");
        }
    }

    private void TryReload()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                TryReloadCore();
            }
            catch (Exception ex)
            {
                _logger?.Debug(LoggerTypes.Proxy, $"Proxy reload skipped: {ex.Message}");
            }
        });
    }

    private void TryReloadCore()
    {
        try
        {
            var provider = NormalizedProvider;
            if (provider == "traefik")
            {
                // Traefik file provider watches the dynamic file no CLI reload required.
                _logger?.Info(LoggerTypes.Proxy, "traefik dynamic config written (file provider watch)");
                return;
            }

            if (provider == "caddy")
            {
                TryReloadCaddy();
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nginx",
                ArgumentList = { "-s", "reload" },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            RunReloadProcess(psi, "nginx", out _);
        }
        catch (Exception ex)
        {
            _logger?.Debug(LoggerTypes.Proxy, $"Proxy reload skipped: {ex.Message}");
        }
    }

    private void TryReloadCaddy()
    {
        var configPath = ResolveConfigPath();
        var reloadPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "caddy",
            ArgumentList = { "reload", "--config", configPath },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        if (RunReloadProcess(reloadPsi, "caddy", out var reloadOutput))
            return;

        var combined = reloadOutput.ToLowerInvariant();
        if (!combined.Contains("connection refused", StringComparison.Ordinal) &&
            !combined.Contains("no such file", StringComparison.Ordinal) &&
            !combined.Contains("not running", StringComparison.Ordinal))
            return;

        _logger?.Info(LoggerTypes.Proxy, "Caddy admin API unavailable starting Caddy with FeatherQuilld config");
        var startPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "caddy",
            ArgumentList = { "start", "--config", configPath },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        RunReloadProcess(startPsi, "caddy start", out _);
    }

    private bool RunReloadProcess(System.Diagnostics.ProcessStartInfo psi, string label, out string output)
    {
        output = "";
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null)
        {
            _logger?.Debug(LoggerTypes.Proxy, $"Could not start {label}");
            return false;
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(15_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            _logger?.Warning(LoggerTypes.Proxy, $"{label} timed out");
            output = ReadProcessOutput(stderrTask, stdoutTask);
            return false;
        }

        output = ReadProcessOutput(stderrTask, stdoutTask);
        if (proc.ExitCode == 0)
        {
            _logger?.Info(LoggerTypes.Proxy, $"{label} ok");
            return true;
        }

        _logger?.Debug(LoggerTypes.Proxy, $"{label} exit={proc.ExitCode}: {output}");
        return false;
    }

    private static string ReadProcessOutput(Task<string> stderrTask, Task<string> stdoutTask)
    {
        try
        {
            Task.WaitAll([stderrTask, stdoutTask], TimeSpan.FromSeconds(2));
            return (stderrTask.Result + stdoutTask.Result).Trim();
        }
        catch
        {
            return "";
        }
    }
}
