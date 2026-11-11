using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Country filter without a GeoIP module: Caddy's own <c>remote_ip</c> matcher with the address ranges of each country.
/// The ranges come from <c>proxy/waf/geo/&lt;CC&gt;.txt</c> (one CIDR per line, written by <c>fq-update-geo.sh</c>); every line is
/// parsed and re-written here, so nothing but addresses can reach the Caddyfile. Identical country sets share one include file
/// (<c>proxy/waf/geosets/&lt;hash&gt;.caddy</c>), which the site's matcher pulls in with <c>import</c>.
///
/// A country without data is skipped and reported, never guessed: the rule fails open instead of locking visitors out.
/// </summary>
public static class WafGeoData
{
    private static readonly Regex CountryRe = new("^[A-Z]{2}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, List<string> Ranges)> Cache = new(StringComparer.Ordinal);
    private static string? _lastStamp;

    public static string DataDirectory(string rootDirectory) => Path.Combine(CorazaFiles.BaseDirectory(rootDirectory), "geo");

    public static string SetsDirectory(string rootDirectory) => Path.Combine(CorazaFiles.BaseDirectory(rootDirectory), "geosets");

    /// <summary>Text of <c>geo/VERSION</c> (written by the update script), or empty when there is no data.</summary>
    public static string Stamp(string rootDirectory)
    {
        try
        {
            var path = Path.Combine(DataDirectory(rootDirectory), "VERSION");
            return File.Exists(path) ? (File.ReadLines(path).FirstOrDefault() ?? "").Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>True once when the country data changed since the last call (first call only records it).</summary>
    public static bool DataChanged(string rootDirectory)
    {
        var now = Stamp(rootDirectory);
        lock (Gate)
        {
            var changed = _lastStamp is not null && _lastStamp != now;
            _lastStamp = now;
            return changed;
        }
    }

    internal static bool IsCountry(string code) => CountryRe.IsMatch(code);

    // ---- address -> country (for the dashboard's statistics) -------------------------------------------------------------

    private sealed class CountryIndex
    {
        public string Stamp = "";
        public DateTime BuiltAt;
        public uint[] Start4 = [];
        public uint[] End4 = [];
        public string[] Code4 = [];
        public UInt128[] Start6 = [];
        public UInt128[] End6 = [];
        public string[] Code6 = [];
    }

    private static CountryIndex? _index;
    private static string? _indexRoot;

    /// <summary>
    /// Two-letter country of an address, from the same files the country filter uses; null when unknown or there is no data.
    /// The index (all countries, about 40,000 ranges in memory) is rebuilt when <c>geo/VERSION</c> changes.
    /// </summary>
    public static string? CountryOf(string rootDirectory, string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return null;
        var text = ip.Trim().TrimStart('[').TrimEnd(']');
        if (!IPAddress.TryParse(text, out var address))
            return null;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var index = GetIndex(rootDirectory);
        if (index is null)
            return null;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            var v = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            var i = Array.BinarySearch(index.Start4, v);
            if (i < 0)
                i = ~i - 1;
            return i >= 0 && v <= index.End4[i] ? index.Code4[i] : null;
        }

        UInt128 w = 0;
        foreach (var b in bytes)
            w = (w << 8) | b;
        var j = Array.BinarySearch(index.Start6, w);
        if (j < 0)
            j = ~j - 1;
        return j >= 0 && w <= index.End6[j] ? index.Code6[j] : null;
    }

    private static CountryIndex? GetIndex(string rootDirectory)
    {
        var stamp = Stamp(rootDirectory);
        lock (Gate)
        {
            if (_index is not null && _indexRoot == rootDirectory && (_index.Stamp == stamp || DateTime.UtcNow - _index.BuiltAt < TimeSpan.FromMinutes(10)))
                return _index.Code4.Length + _index.Code6.Length > 0 ? _index : null;
        }

        var built = BuildIndex(rootDirectory, stamp);
        lock (Gate)
        {
            _index = built;
            _indexRoot = rootDirectory;
        }

        return built.Code4.Length + built.Code6.Length > 0 ? built : null;
    }

    private static CountryIndex BuildIndex(string rootDirectory, string stamp)
    {
        var v4 = new List<(uint s, uint e, string c)>();
        var v6 = new List<(UInt128 s, UInt128 e, string c)>();
        try
        {
            var dir = DataDirectory(rootDirectory);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.txt"))
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    if (!IsCountry(code))
                        continue;
                    var info = new FileInfo(file);
                    if (info.Length > 8 * 1024 * 1024)
                        continue;
                    foreach (var raw in File.ReadLines(file))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line.Length > 49 || !IPNetwork.TryParse(line, out var net))
                            continue;
                        var b = net.BaseAddress.GetAddressBytes();
                        if (b.Length == 4)
                        {
                            var s = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                            var host = net.PrefixLength >= 32 ? 0u : uint.MaxValue >> net.PrefixLength;
                            v4.Add((s, s | host, code));
                        }
                        else
                        {
                            UInt128 s = 0;
                            foreach (var x in b)
                                s = (s << 8) | x;
                            var host = net.PrefixLength >= 128 ? UInt128.Zero : UInt128.MaxValue >> net.PrefixLength;
                            v6.Add((s, s | host, code));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // keep whatever was read
        }

        v4.Sort((a, b) => a.s.CompareTo(b.s));
        v6.Sort((a, b) => a.s.CompareTo(b.s));
        return new CountryIndex
        {
            Stamp = stamp,
            BuiltAt = DateTime.UtcNow,
            Start4 = v4.Select(x => x.s).ToArray(),
            End4 = v4.Select(x => x.e).ToArray(),
            Code4 = v4.Select(x => x.c).ToArray(),
            Start6 = v6.Select(x => x.s).ToArray(),
            End6 = v6.Select(x => x.e).ToArray(),
            Code6 = v6.Select(x => x.c).ToArray(),
        };
    }

    /// <summary>Valid ranges of one country, normalized (<c>a.b.c.d/n</c>); empty when the file is missing or unusable.</summary>
    internal static List<string> Ranges(string rootDirectory, string country)
    {
        if (!IsCountry(country))
            return [];
        var path = Path.Combine(DataDirectory(rootDirectory), country + ".txt");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024)
                return [];
            lock (Gate)
            {
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == info.LastWriteTimeUtc)
                    return hit.Ranges;
            }

            var ranges = new List<string>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.Length > 49)
                    continue;
                if (IPNetwork.TryParse(line, out var net))
                    ranges.Add($"{net.BaseAddress}/{net.PrefixLength}");
            }

            lock (Gate)
            {
                Cache[path] = (info.LastWriteTimeUtc, ranges);
            }

            return ranges;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The include file for a country filter: <c>remote_ip …</c> (deny mode) or <c>not remote_ip private_ranges …</c> (allow mode, so
    /// internal addresses are never locked out). Null when the filter is off or no listed country has data.
    /// </summary>
    internal static (string Hash, string Content, List<string> Missing)? Build(string rootDirectory, WafGeo settings)
    {
        var mode = settings.Mode;
        if (mode is not ("deny" or "allow"))
            return null;
        var countries = settings.Countries
            .Select(c => (c ?? "").Trim().ToUpperInvariant())
            .Where(IsCountry)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (countries.Count == 0)
            return null;

        var missing = new List<string>();
        var all = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in countries)
        {
            var r = Ranges(rootDirectory, c);
            if (r.Count == 0)
                missing.Add(c);
            foreach (var x in r)
                all.Add(x);
        }

        // Allow mode with a country that has no data would lock that country out; deny mode would just not block it.
        if (all.Count == 0 || (mode == "allow" && missing.Count > 0))
            return (string.Empty, string.Empty, missing);

        var sb = new StringBuilder();
        sb.Append(mode == "allow" ? "not remote_ip private_ranges" : "remote_ip");
        foreach (var x in all)
            sb.Append(' ').Append(x);
        sb.Append('\n');
        var content = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
        return (hash, content, missing);
    }

    /// <summary>Path of the include file for these settings, or null when the filter cannot be applied.</summary>
    public static string? PathFor(string rootDirectory, WafGeo settings)
    {
        var built = Build(rootDirectory, settings);
        if (built is null || built.Value.Hash.Length == 0)
            return null;
        var path = Path.Combine(SetsDirectory(rootDirectory), built.Value.Hash + ".caddy").Replace('\\', '/');
        return WafSettingsSanitizer.IsSafeToken(path, 400) ? path : null;
    }

    /// <summary>Write the include files the given WebSpaces need and delete the unused ones.</summary>
    public static void Sync(string rootDirectory, IEnumerable<WebSpace> spaces, Action<string>? warn = null)
    {
        try
        {
            var dir = SetsDirectory(rootDirectory);
            Directory.CreateDirectory(dir);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var space in spaces.Where(s => s.WafEnabled && s.WafSettings is not null))
            {
                var s = WafSettingsSanitizer.Sanitize(space.WafSettings!);
                if (s.Mode != "block")
                    continue;
                var built = Build(rootDirectory, s.Geo);
                if (built is null)
                    continue;
                if (built.Value.Missing.Count > 0)
                    warn?.Invoke($"Country filter: no address data for {string.Join(", ", built.Value.Missing)}; " +
                                 (s.Geo.Mode == "allow" ? "filter not applied (allow mode would lock them out)" : "those countries are not blocked"));
                if (built.Value.Hash.Length == 0)
                    continue;
                var file = Path.Combine(dir, built.Value.Hash + ".caddy");
                keep.Add(built.Value.Hash);
                total += built.Value.Content.Count(c => c == ' ');
                if (!File.Exists(file))
                {
                    var tmp = file + ".tmp";
                    File.WriteAllText(tmp, built.Value.Content);
                    File.Move(tmp, file, true);
                }
            }

            // Caddy compiles the ranges once per site (about 350 bytes per range): measured 11 MB for 31,000 ranges per site.
            if (total > 2_000_000)
                warn?.Invoke($"Country filters use {total:N0} address ranges in total (roughly {total * 350 / 1_000_000:N0} MB of Caddy memory and slow reloads); use fewer countries or fewer sites with a filter");

            foreach (var file in Directory.EnumerateFiles(dir, "*.caddy"))
            {
                if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"Country filter files could not be written: {ex.Message}");
        }
    }
}
