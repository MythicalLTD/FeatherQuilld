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

    /// <summary>Always installed for PHP Apache WebSpaces.</summary>
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
        "pdo_pgsql",
        "pgsql",
        "redis",
        "imagick",
        "soap",
        "sockets",
        "zip",
    ];

    /// <summary>Extensions installed via PECL rather than docker-php-ext-install.</summary>
    public static readonly IReadOnlyList<string> PeclExtensions = ["redis", "imagick"];

    private static readonly HashSet<string> CatalogSet =
        new(Catalog, StringComparer.OrdinalIgnoreCase);

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

    public static void Write(string dataPath, IEnumerable<string> names)
    {
        Directory.CreateDirectory(Path.Combine(dataPath, ".featherquilld"));
        var sanitized = Sanitize(names);
        var payload = new PhpExtensionsFile { Extensions = sanitized };
        File.WriteAllText(HostPath(dataPath), JsonSerializer.Serialize(payload, JsonOptions) + "\n");
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
        var all = Baseline.Concat(extras).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

        if (core.Count > 0)
            configure.AppendLine($"  docker-php-ext-install -j\"$(nproc)\" {string.Join(" ", core)} >/dev/null");

        foreach (var ext in pecl)
        {
            configure.AppendLine(
                $"  if ! php -m 2>/dev/null | grep -qi \"^{ext}$\"; then pecl install -o -f {ext} >/dev/null && docker-php-ext-enable {ext}; fi");
        }

        var aptList = string.Join(" ", apt);
        var needCheck = string.Join(" ", all);
        var installBody = configure.ToString().TrimEnd();

        return $$"""
            set -e
            NEED_INSTALL=0
            for ext in {{needCheck}}; do
              if ! php -m 2>/dev/null | grep -qi "^$${ext}$"; then
                NEED_INSTALL=1
                break
              fi
            done
            if [ "$$NEED_INSTALL" = "1" ]; then
              export DEBIAN_FRONTEND=noninteractive
              apt-get update -qq
              apt-get install -y -qq --no-install-recommends $$PHPIZE_DEPS {{aptList}} unzip >/dev/null
            {{installBody}}
              a2enmod rewrite >/dev/null 2>&1 || true
              rm -rf /var/lib/apt/lists/*
            fi
            ADDONS=/var/www/html/.featherquilld/apache-addons.conf
            if [ -f "$$ADDONS" ]; then
              cp "$$ADDONS" /etc/apache2/sites-enabled/zzz-featherquilld-addons.conf
            fi
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
            }
        }

        return pkgs.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private sealed class PhpExtensionsFile
    {
        [JsonPropertyName("extensions")]
        public List<string> Extensions { get; set; } = [];
    }
}
