using System.Text;
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
            _logger?.Debug(LoggerTypes.Proxy, "Reverse proxy disabled — skip rebuild");
            return;
        }

        lock (_gate)
        {
            var list = spaces.ToList();
            var provider = NormalizedProvider;

            if (provider == "nginx" && _acme is not null)
            {
                _acme.EnsureChallengeLayout();
                var sslDomains = list
                    .Where(s => s.Ssl)
                    .SelectMany(s => s.Domains)
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sslDomains.Count > 0 && !string.IsNullOrWhiteSpace(_config.System.Proxy.AcmeEmail))
                {
                    try
                    {
                        _acme.EnsureCertsAsync(sslDomains).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(LoggerTypes.Proxy, $"ACME ensure: {ex.Message}");
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

    private string ContentRoot(WebSpace space)
    {
        var basePath = _config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota
            ? FeatherQuilld.Utils.WebSpaces.Disk.FuseQuotaLimiter.GetMountPath(_config.System, space.Uuid)
            : Path.Combine(_config.System.Data, space.Uuid.ToString());
        return WebSpaceStore.ResolveContentRootPath(basePath, space.DocumentRoot);
    }

    private string BuildCaddy(IEnumerable<WebSpace> spaces)
    {
        var sb = new StringBuilder();
        var email = _config.System.Proxy.AcmeEmail?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
            sb.AppendLine($"{{ email {email} }}");

        sb.AppendLine("# Generated by FeatherQuilld — do not edit by hand");
        sb.AppendLine();

        var any = false;
        foreach (var space in spaces.OrderBy(s => s.CreatedAt))
        {
            if (space.Domains.Count == 0)
            {
                _logger?.Debug(LoggerTypes.Proxy, $"WebSpace {space.Uuid} has no domains — skip");
                continue;
            }

            any = true;
            var hosts = string.Join(", ", space.Domains);
            sb.AppendLine(hosts);
            sb.AppendLine("{");

            if (!space.Ssl)
                sb.AppendLine("\ttls internal");

            if (space.BackendPort > 0)
            {
                sb.AppendLine($"\treverse_proxy 127.0.0.1:{space.BackendPort}");
            }
            else
            {
                var root = ContentRoot(space);
                sb.AppendLine($"\troot * {root}");
                sb.AppendLine("\tfile_server");
                sb.AppendLine($"\t# WebSpace {space.Uuid} webplate={space.WebPlateId} — backend_port unset");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (!any)
            sb.AppendLine("# No WebSpaces with domains yet");

        return sb.ToString();
    }

    private string BuildNginx(IEnumerable<WebSpace> spaces)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by FeatherQuilld — do not edit by hand");
        sb.AppendLine();

        var challengeRoot = _acme?.AcmeWwwRoot
                            ?? Path.Combine(_config.System.RootDirectory, "acme", "www");

        foreach (var space in spaces.OrderBy(s => s.CreatedAt))
        {
            if (space.Domains.Count == 0)
                continue;

            foreach (var domain in space.Domains)
            {
                // Always expose HTTP for ACME challenges (and non-SSL sites).
                sb.AppendLine("server {");
                sb.AppendLine("    listen 80;");
                sb.AppendLine($"    server_name {domain};");
                sb.AppendLine("    location ^~ /.well-known/acme-challenge/ {");
                sb.AppendLine($"        root {challengeRoot};");
                sb.AppendLine("        default_type text/plain;");
                sb.AppendLine("    }");

                if (!space.Ssl)
                    AppendNginxAppLocation(sb, space);
                else
                    sb.AppendLine("    location / { return 301 https://$host$request_uri; }");

                sb.AppendLine("}");
                sb.AppendLine();

                if (!space.Ssl)
                    continue;

                var crt = NginxAcmeService.CertPath(domain);
                var key = NginxAcmeService.KeyPath(domain);
                sb.AppendLine("server {");
                sb.AppendLine("    listen 443 ssl;");
                sb.AppendLine($"    server_name {domain};");

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

                AppendNginxAppLocation(sb, space);
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
        sb.AppendLine("# Generated by FeatherQuilld — do not edit by hand");
        sb.AppendLine("# Mount this file as a Traefik file provider (watch: true).");
        sb.AppendLine("http:");
        sb.AppendLine("  routers:");

        var anyRouter = false;
        var services = new StringBuilder();
        services.AppendLine("  services:");

        foreach (var space in spaces.OrderBy(s => s.CreatedAt))
        {
            if (space.Domains.Count == 0)
                continue;

            if (space.BackendPort <= 0)
            {
                _logger?.Warning(LoggerTypes.Proxy,
                    $"Traefik skip WebSpace {space.Uuid}: backend_port required (allocate for static via Traefik)");
                continue;
            }

            anyRouter = true;
            var id = "ws-" + space.Uuid.ToString("N")[..12];
            var hostRule = string.Join(" || ", space.Domains.Select(d => $"Host(`{EscapeYamlScalar(d)}`)"));

            sb.AppendLine($"    {id}:");
            sb.AppendLine($"      rule: \"{hostRule}\"");
            sb.AppendLine($"      service: {id}");
            if (space.Ssl)
            {
                sb.AppendLine("      entryPoints:");
                sb.AppendLine("        - websecure");
                sb.AppendLine("      tls:");
                sb.AppendLine("        certResolver: featherquilld");
            }
            else
            {
                sb.AppendLine("      entryPoints:");
                sb.AppendLine("        - web");
            }

            services.AppendLine($"    {id}:");
            services.AppendLine("      loadBalancer:");
            services.AppendLine("        servers:");
            services.AppendLine($"          - url: \"http://127.0.0.1:{space.BackendPort}\"");
        }

        if (!anyRouter)
        {
            // Traefik v3 rejects empty maps / comment-only http blocks — emit a inert placeholder.
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

        sb.Append(services);
        return sb.ToString();
    }

    private static string EscapeYamlScalar(string value) =>
        value.Replace("\"", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    private void AppendNginxAppLocation(StringBuilder sb, WebSpace space)
    {
        if (space.BackendPort > 0)
        {
            sb.AppendLine("    location / {");
            sb.AppendLine($"        proxy_pass http://127.0.0.1:{space.BackendPort};");
            sb.AppendLine("        proxy_set_header Host $host;");
            sb.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
            sb.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
            sb.AppendLine("    }");
        }
        else
        {
            var root = ContentRoot(space);
            sb.AppendLine($"    root {root};");
            sb.AppendLine("    index index.html;");
            sb.AppendLine("    location / { try_files $uri $uri/ =404; }");
        }
    }

    private void TryReload()
    {
        try
        {
            var provider = NormalizedProvider;
            if (provider == "traefik")
            {
                // Traefik file provider watches the dynamic file — no CLI reload required.
                _logger?.Info(LoggerTypes.Proxy, "traefik dynamic config written (file provider watch)");
                return;
            }

            var psi = provider == "nginx"
                ? new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nginx",
                    ArgumentList = { "-s", "reload" },
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                }
                : new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "caddy",
                    ArgumentList = { "reload", "--config", ResolveConfigPath() },
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                _logger?.Debug(LoggerTypes.Proxy, $"Could not start {provider} reload");
                return;
            }

            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _logger?.Warning(LoggerTypes.Proxy, $"{provider} reload timed out");
                return;
            }

            if (proc.ExitCode == 0)
                _logger?.Info(LoggerTypes.Proxy, $"{provider} reloaded");
            else
                _logger?.Debug(LoggerTypes.Proxy,
                    $"{provider} reload exit={proc.ExitCode}: {proc.StandardError.ReadToEnd().Trim()}");
        }
        catch (Exception ex)
        {
            _logger?.Debug(LoggerTypes.Proxy, $"Proxy reload skipped: {ex.Message}");
        }
    }
}
