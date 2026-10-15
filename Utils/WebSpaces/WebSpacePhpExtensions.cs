using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// Curated PHP extension allowlist for stock <c>php:N-apache</c> images (no PECL).
/// Selection is stored at <c>.featherquilld/php-extensions.json</c> and applied at container start.
/// </summary>
public static class WebSpacePhpExtensions
{
    public const string RelativePath = ".featherquilld/php-extensions.json";

    /// <summary>Always installed for PHP Apache WebSpaces (unless overridden via JSON <c>baseline</c>).</summary>
    public static readonly IReadOnlyList<string> Baseline = ["mysqli", "pdo_mysql", "opcache"];

    /// <summary>User-selectable extensions (docker-php-ext-install names, plus PECL <c>redis</c>).</summary>
    public static readonly IReadOnlyList<string> Catalog =
    [
        "bcmath",
        "calendar",
        "exif",
        "gd",
        "gettext",
        "gmp",
        "intl",
        "ldap",
        "mongodb",
        "pdo_pgsql",
        "pgsql",
        "redis",
        "imagick",
        "soap",
        "sockets",
        "zip",
    ];

    /// <summary>Extensions installed via PECL rather than docker-php-ext-install.</summary>
    public static readonly IReadOnlyList<string> PeclExtensions = ["redis", "imagick", "mongodb"];

    private static readonly HashSet<string> CatalogSet =
        new(Catalog, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BaselineSet =
        new(Baseline, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> PeclSet =
        new(PeclExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string HostPath(string dataPath) =>
        Path.Combine(dataPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static List<string> Sanitize(IEnumerable<string>? names)
    {
        var result = new List<string>();
        if (names is null)
            return result;

        foreach (var raw in names)
        {
            var name = (raw ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0 || name.Length > 32)
                continue;
            if (!CatalogSet.Contains(name))
                continue;
            if (!result.Contains(name, StringComparer.Ordinal))
                result.Add(name);
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Sanitize a baseline override: only known baseline names (or empty to disable all baseline).
    /// Unknown names are dropped.
    /// </summary>
    public static List<string> SanitizeBaseline(IEnumerable<string>? names)
    {
        var result = new List<string>();
        if (names is null)
            return result;

        foreach (var raw in names)
        {
            var name = (raw ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0 || name.Length > 32)
                continue;
            if (!BaselineSet.Contains(name))
                continue;
            if (!result.Contains(name, StringComparer.Ordinal))
                result.Add(name);
        }

        // Preserve default Baseline order for stability.
        return Baseline.Where(b => result.Contains(b, StringComparer.Ordinal)).ToList();
    }

    public static List<string> Read(string dataPath)
    {
        var path = HostPath(dataPath);
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return Sanitize(doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? ""));
            }

            if (doc.RootElement.TryGetProperty("extensions", out var ext) &&
                ext.ValueKind == JsonValueKind.Array)
            {
                return Sanitize(ext.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? ""));
            }
        }
        catch
        {
            // ignore corrupt file
        }

        return [];
    }

    /// <summary>
    /// Effective baseline for a WebSpace: JSON <c>baseline</c> when present (may be empty),
    /// otherwise the built-in <see cref="Baseline"/>.
    /// </summary>
    public static IReadOnlyList<string> ReadBaseline(string dataPath)
    {
        var path = HostPath(dataPath);
        if (!File.Exists(path))
            return Baseline;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Baseline;

            if (!doc.RootElement.TryGetProperty("baseline", out var baseline) ||
                baseline.ValueKind != JsonValueKind.Array)
                return Baseline;

            return SanitizeBaseline(baseline.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? ""));
        }
        catch
        {
            return Baseline;
        }
    }

    public static void Write(string dataPath, IEnumerable<string> names, IEnumerable<string>? baseline = null)
    {
        Directory.CreateDirectory(Path.Combine(dataPath, ".featherquilld"));
        var sanitized = Sanitize(names);
        var payload = new PhpExtensionsFile { Extensions = sanitized };
        if (baseline is not null)
        {
            payload.Baseline = SanitizeBaseline(baseline);
        }
        else if (HasExplicitBaseline(dataPath))
        {
            // Preserve an existing baseline override when only extras are rewritten.
            payload.Baseline = ReadBaseline(dataPath).ToList();
        }

        File.WriteAllText(HostPath(dataPath), JsonSerializer.Serialize(payload, JsonOptions) + "\n");
    }

    private static bool HasExplicitBaseline(string dataPath)
    {
        var path = HostPath(dataPath);
        if (!File.Exists(path))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("baseline", out var b) &&
                   b.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureFile(string dataPath)
    {
        var path = HostPath(dataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            Write(dataPath, []);
    }

    /// <summary>Bash entrypoint that installs baseline + selected extensions then starts Apache.</summary>
    public static string BuildBootstrap(string dataPath)
    {
        var extras = Read(dataPath);
        var baseline = ReadBaseline(dataPath);
        var all = baseline.Concat(extras).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var core = all.Where(e => !PeclSet.Contains(e)).ToList();
        var pecl = all.Where(e => PeclSet.Contains(e)).ToList();
        var apt = AptPackagesFor(all);
        var configure = new StringBuilder();

        if (core.Any(e => e.Equals("gd", StringComparison.OrdinalIgnoreCase)))
        {
            configure.AppendLine(
                "  docker-php-ext-configure gd --with-freetype --with-jpeg >/dev/null 2>&1 || " +
                "docker-php-ext-configure gd >/dev/null 2>&1 || true");
        }

        // Install each core extension independently so a single failure (e.g. already-built
        // Zend OPcache on stock php:*-apache) cannot abort the entrypoint under set -e.
        foreach (var ext in core)
        {
            configure.AppendLine($"  if ! php_has_ext {ext}; then");
            configure.AppendLine(
                $"    docker-php-ext-install -j\"$(nproc)\" {ext} >/dev/null 2>&1 || " +
                $"echo \"featherquilld: warning: failed to install PHP extension {ext}\" >&2");
            configure.AppendLine("  fi");
        }

        foreach (var ext in pecl)
        {
            configure.AppendLine($"  if ! php_has_ext {ext}; then");
            configure.AppendLine(
                $"    pecl install -o -f {ext} >/dev/null 2>&1 && docker-php-ext-enable {ext} || " +
                $"echo \"featherquilld: warning: failed to install PECL extension {ext}\" >&2");
            configure.AppendLine("  fi");
        }

        var aptList = string.Join(" ", apt);
        var needCheck = string.Join(" ", all);
        var installBody = configure.ToString().TrimEnd();

        // php_has_ext: stock php:*-apache reports OPcache as "Zend OPcache" under [Zend Modules],
        // so grepping php -m for ^opcache$ never matches even when the module is loaded.
        return $$"""
            set -e
            php_has_ext() {
              local ext="$1"
              case "$ext" in
                opcache)
                  php -r 'exit(extension_loaded("Zend OPcache")||extension_loaded("opcache")?0:1);' 2>/dev/null
                  ;;
                *)
                  php -r "exit(extension_loaded('$ext')?0:1);" 2>/dev/null
                  ;;
              esac
            }
            NEED_INSTALL=0
            for ext in {{needCheck}}; do
              if ! php_has_ext "$ext"; then
                NEED_INSTALL=1
                break
              fi
            done
            if [ "$NEED_INSTALL" = "1" ]; then
              export DEBIAN_FRONTEND=noninteractive
              apt-get update -qq
              apt-get install -y -qq --no-install-recommends $PHPIZE_DEPS {{aptList}} unzip >/dev/null
            {{installBody}}
              a2enmod rewrite >/dev/null 2>&1 || true
              rm -rf /var/lib/apt/lists/*
            fi
            ADDONS=/var/www/html/.featherquilld/apache-addons.conf
            if [ -f "$ADDONS" ]; then
              cp "$ADDONS" /etc/apache2/sites-enabled/zzz-featherquilld-addons.conf
            fi
            # Log real client IPs behind the FeatherQuilld reverse proxy. The proxy
            # always sends X-Forwarded-For, but without mod_remoteip Apache logs only
            # the docker-gateway address — breaking per-IP rate limits, WAF rules and
            # any consumer of the access log (security scanners included).
            a2enmod remoteip >/dev/null 2>&1 || true
            printf 'RemoteIPHeader X-Forwarded-For\nRemoteIPInternalProxy 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16\n' \
              > /etc/apache2/conf-enabled/zzz-featherquilld-remoteip.conf
            # Apache/PHP laeuft als www-data (uid 33), der Installer/Uploader aber als root.
            # Ohne chown kann die App nichts schreiben (WordPress kann wp-config.php nicht
            # anlegen, keine Uploads, keine Plugin-/Core-Updates).
            chown -R www-data:www-data /var/www/html 2>/dev/null || true
            exec apache2-foreground
            """.ReplaceLineEndings("\n");
    }

    private static List<string> AptPackagesFor(IEnumerable<string> extensions)
    {
        var pkgs = new HashSet<string>(StringComparer.Ordinal)
        {
            "libzip-dev",
        };

        foreach (var ext in extensions)
        {
            switch (ext.ToLowerInvariant())
            {
                case "gd":
                    pkgs.Add("libpng-dev");
                    pkgs.Add("libjpeg62-turbo-dev");
                    pkgs.Add("libfreetype6-dev");
                    break;
                case "intl":
                    pkgs.Add("libicu-dev");
                    break;
                case "pgsql":
                case "pdo_pgsql":
                    pkgs.Add("libpq-dev");
                    break;
                case "ldap":
                    pkgs.Add("libldap2-dev");
                    break;
                case "gmp":
                    pkgs.Add("libgmp-dev");
                    break;
                case "soap":
                    pkgs.Add("libxml2-dev");
                    break;
                case "imagick":
                    pkgs.Add("libmagickwand-dev");
                    break;
                case "mongodb":
                    pkgs.Add("libssl-dev");
                    break;
            }
        }

        return pkgs.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private sealed class PhpExtensionsFile
    {
        [JsonPropertyName("extensions")]
        public List<string> Extensions { get; set; } = [];

        /// <summary>
        /// Optional override of the built-in baseline. When serialized as null, the property
        /// is omitted and the default baseline applies. An empty array disables baseline installs.
        /// </summary>
        [JsonPropertyName("baseline")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Baseline { get; set; }
    }
}
