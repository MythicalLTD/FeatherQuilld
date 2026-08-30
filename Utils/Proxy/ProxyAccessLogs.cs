using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>HTTP access/error logs written by generated Caddy/nginx configs.</summary>
public static class ProxyAccessLogs
{
    public static string DirectoryFor(string rootDirectory, Guid uuid) =>
        Path.Combine(rootDirectory, "proxy", "logs", uuid.ToString("D"));

    public static string AccessLogPath(string rootDirectory, Guid uuid, string domain) =>
        Path.Combine(DirectoryFor(rootDirectory, uuid), $"{domain.Trim().ToLowerInvariant()}.access.log");

    public static string ErrorLogPath(string rootDirectory, Guid uuid, string domain) =>
        Path.Combine(DirectoryFor(rootDirectory, uuid), $"{domain.Trim().ToLowerInvariant()}.error.log");

    public static void EnsureDir(string rootDirectory, Guid uuid) =>
        Directory.CreateDirectory(DirectoryFor(rootDirectory, uuid));

    public static string SummaryPath(string rootDirectory, Guid uuid, string domain, DateOnly day) =>
        Path.Combine(DirectoryFor(rootDirectory, uuid), $"{domain.Trim().ToLowerInvariant()}.{day:yyyy-MM-dd}.json");

    public static object Read(string rootDirectory, WebSpace space, string? domain, int lines, int days = 0)
    {
        lines = Math.Clamp(lines, 1, 5000);
        days = Math.Clamp(days, 0, 90);
        var domains = string.IsNullOrWhiteSpace(domain)
            ? space.Domains.Where(d => !string.IsNullOrWhiteSpace(d)).ToList()
            : [domain.Trim().ToLowerInvariant()];

        var files = new List<object>();
        var totalHits = 0;
        var bytesOut = 0L;
        var statusCounts = new Dictionary<int, int>();
        var byDay = new Dictionary<string, DayBucket>(StringComparer.Ordinal);

        foreach (var host in domains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var accessPath = AccessLogPath(rootDirectory, space.Uuid, host);
            var errorPath = ErrorLogPath(rootDirectory, space.Uuid, host);
            var accessTail = TailFile(accessPath, lines);
            var parsed = ParseLines(accessTail);
            totalHits += parsed.Hits;
            bytesOut += parsed.Bytes;
            foreach (var kv in parsed.Status)
            {
                statusCounts[kv.Key] = statusCounts.GetValueOrDefault(kv.Key) + kv.Value;
            }

            if (days > 0)
                MergeHistory(rootDirectory, space.Uuid, host, days, byDay);

            files.Add(new
            {
                domain = host,
                access_log = accessPath,
                error_log = errorPath,
                access_present = File.Exists(accessPath),
                error_present = File.Exists(errorPath),
                access_tail = accessTail,
                error_tail = TailFile(errorPath, Math.Min(lines, 200)),
                hits = parsed.Hits,
                bytes = parsed.Bytes,
            });
        }

        return new
        {
            uuid = space.Uuid,
            hits = totalHits,
            bytes = bytesOut,
            status = statusCounts.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            by_day = byDay
                .OrderBy(kv => kv.Key)
                .Select(kv => new
                {
                    date = kv.Key,
                    hits = kv.Value.Hits,
                    bytes = kv.Value.Bytes,
                    status = kv.Value.Status.OrderBy(s => s.Key)
                        .ToDictionary(s => s.Key.ToString(), s => s.Value),
                })
                .ToList(),
            files,
        };
    }

    public static void RotateAll(string rootDirectory, IEnumerable<WebSpace> spaces)
    {
        foreach (var space in spaces)
        {
            try
            {
                RotateSpace(rootDirectory, space);
            }
            catch
            {
                // best-effort retention
            }
        }
    }

    public static void RotateSpace(string rootDirectory, WebSpace space)
    {
        EnsureDir(rootDirectory, space.Uuid);
        foreach (var host in space.Domains.Where(d => !string.IsNullOrWhiteSpace(d))
                     .Select(d => d.Trim().ToLowerInvariant())
                     .Distinct())
        {
            PersistLiveLogSummaries(rootDirectory, space.Uuid, host);
        }
    }

    private static string TailFile(string path, int lines)
    {
        if (!File.Exists(path))
            return "";
        try
        {
            var all = File.ReadAllLines(path);
            if (all.Length <= lines)
                return string.Join('\n', all);
            return string.Join('\n', all.AsSpan(all.Length - lines).ToArray());
        }
        catch
        {
            return "";
        }
    }

    private sealed class DayBucket
    {
        public int Hits;
        public long Bytes;
        public Dictionary<int, int> Status { get; } = new();

        public void Add(int hits, long bytes, Dictionary<int, int>? status)
        {
            Hits += hits;
            Bytes += bytes;
            if (status is null)
                return;
            foreach (var kv in status)
                Status[kv.Key] = Status.GetValueOrDefault(kv.Key) + kv.Value;
        }
    }

    private static void MergeHistory(
        string rootDirectory,
        Guid uuid,
        string domain,
        int days,
        Dictionary<string, DayBucket> byDay)
    {
        PersistLiveLogSummaries(rootDirectory, uuid, domain);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (var i = 0; i < days; i++)
        {
            var day = today.AddDays(-i);
            var key = day.ToString("yyyy-MM-dd");
            var sidecar = LoadSummary(SummaryPath(rootDirectory, uuid, domain, day));
            if (sidecar is null)
                continue;
            if (!byDay.TryGetValue(key, out var bucket))
            {
                bucket = new DayBucket();
                byDay[key] = bucket;
            }

            bucket.Add(sidecar.Hits, sidecar.Bytes, sidecar.Status);
        }
    }

    private static void PersistLiveLogSummaries(string rootDirectory, Guid uuid, string domain)
    {
        var accessPath = AccessLogPath(rootDirectory, uuid, domain);
        if (!File.Exists(accessPath))
            return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(accessPath);
        }
        catch
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var buckets = new Dictionary<string, DayBucket>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var day = ExtractDate(line) ?? today;
            var key = day.ToString("yyyy-MM-dd");
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new DayBucket();
                buckets[key] = bucket;
            }

            var code = ExtractStatus(line);
            var status = new Dictionary<int, int>();
            if (code is > 0 and < 600)
                status[code] = 1;
            bucket.Add(1, ExtractBytes(line), status);
        }

        EnsureDir(rootDirectory, uuid);
        foreach (var kv in buckets)
        {
            if (!DateOnly.TryParse(kv.Key, out var day))
                continue;
            // Never overwrite a completed past-day sidecar with a partial live parse
            // unless the sidecar is missing. Today is always rewritten from live log.
            var path = SummaryPath(rootDirectory, uuid, domain, day);
            if (day < today && File.Exists(path))
                continue;
            WriteSummary(path, kv.Value);
        }
    }

    private static DayBucket? LoadSummary(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var json = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var bucket = new DayBucket
            {
                Hits = root.TryGetProperty("hits", out var h) ? h.GetInt32() : 0,
                Bytes = root.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0,
            };
            if (root.TryGetProperty("status", out var status) && status.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in status.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out var code))
                        bucket.Status[code] = prop.Value.GetInt32();
                }
            }

            return bucket;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSummary(string path, DayBucket bucket)
    {
        try
        {
            var status = string.Join(",",
                bucket.Status.OrderBy(kv => kv.Key).Select(kv => $"\"{kv.Key}\":{kv.Value}"));
            File.WriteAllText(path,
                $"{{\"hits\":{bucket.Hits},\"bytes\":{bucket.Bytes},\"status\":{{{status}}}}}");
        }
        catch
        {
            // ignore
        }
    }

    internal static DateOnly? ExtractDate(string line)
    {
        // nginx combined: [30/Aug/2026:00:00:00 +0000]
        var bracket = line.IndexOf('[');
        if (bracket >= 0)
        {
            var end = line.IndexOf(':', bracket);
            if (end > bracket + 1)
            {
                var datePart = line[(bracket + 1)..end];
                if (DateTime.TryParseExact(datePart, "dd/MMM/yyyy",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var nginx))
                    return DateOnly.FromDateTime(nginx);
            }
        }

        // Caddy JSON: "ts":1724976000.12  or  "ts":"2026-08-30T00:00:00Z"
        var ts = line.IndexOf("\"ts\":", StringComparison.Ordinal);
        if (ts >= 0)
        {
            var rest = line[(ts + 5)..].TrimStart();
            if (rest.StartsWith('"'))
            {
                var q = rest.IndexOf('"', 1);
                if (q > 1 && DateTimeOffset.TryParse(rest[1..q], out var iso))
                    return DateOnly.FromDateTime(iso.UtcDateTime);
            }
            else
            {
                var i = 0;
                while (i < rest.Length && (char.IsDigit(rest[i]) || rest[i] == '.'))
                    i++;
                if (i > 0 && double.TryParse(rest[..i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var unix))
                {
                    var dto = DateTimeOffset.FromUnixTimeSeconds((long)unix);
                    return DateOnly.FromDateTime(dto.UtcDateTime);
                }
            }
        }

        return null;
    }

    private static (int Hits, long Bytes, Dictionary<int, int> Status) ParseLines(string text)
    {
        var hits = 0;
        var bytes = 0L;
        var status = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(text))
            return (0, 0, status);

        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            hits++;
            var code = ExtractStatus(line);
            if (code is > 0 and < 600)
                status[code] = status.GetValueOrDefault(code) + 1;
            bytes += ExtractBytes(line);
        }

        return (hits, bytes, status);
    }

    private static int ExtractStatus(string line)
    {
        // nginx combined: ... "GET / HTTP/1.1" 200 1234
        var quote = line.LastIndexOf('"');
        if (quote >= 0 && quote + 2 < line.Length)
        {
            var rest = line[(quote + 1)..].TrimStart();
            var sp = rest.IndexOf(' ');
            if (sp > 0 && int.TryParse(rest[..sp], out var nginxCode))
                return nginxCode;
        }

        // Caddy JSON: "status":200
        var json = line.IndexOf("\"status\":", StringComparison.Ordinal);
        if (json >= 0)
        {
            var num = line[(json + 9)..].TrimStart();
            var i = 0;
            while (i < num.Length && char.IsDigit(num[i]))
                i++;
            if (i > 0 && int.TryParse(num[..i], out var jsonCode))
                return jsonCode;
        }

        return 0;
    }

    private static long ExtractBytes(string line)
    {
        var quote = line.LastIndexOf('"');
        if (quote >= 0 && quote + 2 < line.Length)
        {
            var rest = line[(quote + 1)..].TrimStart();
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var n) && n > 0)
                return n;
        }

        var size = line.IndexOf("\"size\":", StringComparison.Ordinal);
        if (size >= 0)
        {
            var num = line[(size + 7)..].TrimStart();
            var i = 0;
            while (i < num.Length && char.IsDigit(num[i]))
                i++;
            if (i > 0 && long.TryParse(num[..i], out var jsonBytes))
                return jsonBytes;
        }

        return 0;
    }
}
