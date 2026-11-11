using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Dashboard numbers for one WebSpace's firewall.
///
/// Access logs are emptied every few hours by the log-retention service, so the numbers are not read from them directly. Instead
/// <see cref="Absorb"/> moves what is new in the logs into a small per-WebSpace history file (hourly counters, no visitor
/// addresses except inside attack events) before the logs are emptied; <see cref="Compute"/> only reads that history.
///
/// Sources: the WebSpace's Caddy access logs (requests), the node-wide Coraza audit log (attack events - kept only when the
/// entry's host is one of the WebSpace's own domains), and requests that Quilld's own rules refused (recognised in the access
/// log by the extra "uuid" key Caddy adds to error-handled requests, plus a refusal status).
/// </summary>
public static class WafStats
{
    public const int KeepDays = 31;
    public const int MaxEvents = 3000;
    private const int MaxChunkBytes = 64 * 1024 * 1024;
    private const int MaxKeysPerHour = 40;
    private static readonly int[] RefusalStatuses = [403, 405, 413, 414, 429, 503];
    private static readonly Regex IdRe = new(@"\[id ""(\d+)""\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex MsgRe = new(@"\[msg ""([^""]*)""\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TagRe = new(@"\[tag ""([^""]*)""\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Dictionary<string, string> TagToCategory = CorazaRules.CategoryTags
        .SelectMany(kv => kv.Value.Select(t => (tag: t, cat: kv.Key)))
        .ToDictionary(x => x.tag, x => x.cat, StringComparer.Ordinal);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime at, object value)> Cache = new();

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ---- history model -------------------------------------------------------------------------------------------------

    public sealed class Hour
    {
        [JsonPropertyName("r")] public int Requests { get; set; }
        [JsonPropertyName("s4")] public int Status4xx { get; set; }
        [JsonPropertyName("s5")] public int Status5xx { get; set; }
        [JsonPropertyName("u")] public int Visitors { get; set; }
        [JsonPropertyName("st")] public Dictionary<string, int> Status { get; set; } = new();
        [JsonPropertyName("h")] public Dictionary<string, int> Hosts { get; set; } = new();
        [JsonPropertyName("rf")] public Dictionary<string, int> Referrers { get; set; } = new();
        [JsonPropertyName("br")] public Dictionary<string, int> Browsers { get; set; } = new();
        [JsonPropertyName("os")] public Dictionary<string, int> Systems { get; set; } = new();
        /// <summary>Requests per visitor country (ISO code), from the node's country data; empty when there is none.</summary>
        [JsonPropertyName("co")] public Dictionary<string, int> Countries { get; set; } = new();
        /// <summary>Addresses seen in this hour; only kept while the hour is recent, to count visitors once.</summary>
        [JsonPropertyName("ips")] public List<string>? Ips { get; set; }
    }

    public sealed class Attack
    {
        [JsonPropertyName("t")] public long Time { get; set; }
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("ip")] public string Ip { get; set; } = "";
        [JsonPropertyName("m")] public string Method { get; set; } = "";
        [JsonPropertyName("h")] public string Host { get; set; } = "";
        [JsonPropertyName("p")] public string Path { get; set; } = "";
        [JsonPropertyName("rid")] public string RuleId { get; set; } = "";
        [JsonPropertyName("msg")] public string RuleMsg { get; set; } = "";
        [JsonPropertyName("c")] public string Category { get; set; } = "";
        [JsonPropertyName("a")] public string Action { get; set; } = "";
    }

    public sealed class History
    {
        [JsonPropertyName("access")] public Dictionary<string, long> AccessOffsets { get; set; } = new();
        [JsonPropertyName("audit")] public long AuditOffset { get; set; }
        [JsonPropertyName("hours")] public Dictionary<string, Hour> Hours { get; set; } = new();
        [JsonPropertyName("events")] public List<Attack> Events { get; set; } = [];
    }

    private sealed record Req(DateTimeOffset Time, string Ip, string Method, string Host, string Path, int Status, string Referrer, string Agent, bool Refused);

    public static string HistoryPath(string rootDirectory, Guid uuid) =>
        Path.Combine(CorazaFiles.BaseDirectory(rootDirectory), "history", uuid.ToString("D") + ".json");

    public static (TimeSpan span, TimeSpan bucket) RangeOf(string? range) => range switch
    {
        "7d" => (TimeSpan.FromDays(7), TimeSpan.FromHours(6)),
        "30d" => (TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
        _ => (TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
    };

    // ---- absorbing new log lines -----------------------------------------------------------------------------------------

    /// <summary>Move everything new in the WebSpace's logs into its history. Safe to call often; also called right before logs are emptied.</summary>
    public static void Absorb(string rootDirectory, WebSpace space, DateTimeOffset? now = null)
    {
        var nowValue = now ?? DateTimeOffset.UtcNow;
        var domains = Domains(space);
        lock (Gate)
        {
            var path = HistoryPath(rootDirectory, space.Uuid);
            var history = Load(path);
            var changed = false;

            var newEvents = new List<Attack>();
            var auditPath = CorazaFiles.AuditLogPath(rootDirectory);
            var audit = ReadNew(auditPath, history.AuditOffset, out var auditEnd);
            if (auditEnd != history.AuditOffset)
            {
                history.AuditOffset = auditEnd;
                changed = true;
            }

            newEvents.AddRange(ParseAudit(audit, domains));
            var auditBlocked = new HashSet<string>(newEvents.Where(a => a.Action == "blocked").Select(a => $"{a.Ip}|{a.Path}"), StringComparer.Ordinal);

            var n = 0;
            foreach (var domain in domains)
            {
                var file = ProxyAccessLogs.AccessLogPath(rootDirectory, space.Uuid, domain);
                var offset = history.AccessOffsets.GetValueOrDefault(domain);
                var lines = ReadNew(file, offset, out var end);
                if (end != offset || !history.AccessOffsets.ContainsKey(domain))
                {
                    history.AccessOffsets[domain] = end;
                    changed = true;
                }

                foreach (var line in lines)
                {
                    var req = ParseAccess(line);
                    if (req is null)
                        continue;
                    AddRequest(history, req, rootDirectory, ref changed);
                    if (req.Refused && !auditBlocked.Contains($"{req.Ip}|{req.Path}"))
                    {
                        newEvents.Add(new Attack
                        {
                            Time = req.Time.ToUnixTimeMilliseconds(), Id = $"r{req.Time.ToUnixTimeMilliseconds()}-{n++}", Ip = req.Ip, Method = req.Method,
                            Host = req.Host, Path = req.Path, RuleMsg = RefusalMessage(req.Status), Category = "rules", Action = "blocked",
                        });
                    }
                }
            }

            if (newEvents.Count > 0)
            {
                history.Events.AddRange(newEvents);
                changed = true;
            }

            var cutoff = nowValue.AddDays(-KeepDays);
            var before = history.Hours.Count + history.Events.Count;
            var cutoffHour = cutoff.ToUnixTimeSeconds() / 3600;
            foreach (var key in history.Hours.Keys.Where(k => long.TryParse(k, out var h) && h < cutoffHour).ToList())
                history.Hours.Remove(key);
            history.Events = history.Events.Where(e => e.Time >= cutoff.ToUnixTimeMilliseconds()).OrderBy(e => e.Time).ToList();
            if (history.Events.Count > MaxEvents)
                history.Events = history.Events.Skip(history.Events.Count - MaxEvents).ToList();
            if (history.Hours.Count + history.Events.Count != before)
                changed = true;

            // Addresses are only needed to count a visitor once within the hour; drop them once the hour is over.
            var recent = nowValue.ToUnixTimeSeconds() / 3600 - 2;
            foreach (var kv in history.Hours)
            {
                if (kv.Value.Ips is not null && long.TryParse(kv.Key, out var hr) && hr < recent)
                {
                    kv.Value.Ips = null;
                    changed = true;
                }
            }

            if (changed)
                Save(path, history);
        }
    }

    private static void AddRequest(History h, Req r, string rootDirectory, ref bool changed)
    {
        var key = (r.Time.ToUnixTimeSeconds() / 3600).ToString(CultureInfo.InvariantCulture);
        if (!h.Hours.TryGetValue(key, out var hour))
            h.Hours[key] = hour = new Hour();
        hour.Requests++;
        if (r.Status is >= 400 and < 500) hour.Status4xx++;
        else if (r.Status >= 500) hour.Status5xx++;
        Count(hour.Status, r.Status.ToString(CultureInfo.InvariantCulture));
        Count(hour.Hosts, r.Host);
        Count(hour.Referrers, ReferrerHost(r.Referrer, r.Host));
        Count(hour.Browsers, Browser(r.Agent));
        Count(hour.Systems, OperatingSystem(r.Agent));
        if (WafGeoData.CountryOf(rootDirectory, r.Ip) is { } country)
            Count(hour.Countries, country);
        if (r.Ip.Length > 0)
        {
            hour.Ips ??= [];
            if (!hour.Ips.Contains(r.Ip) && hour.Ips.Count < 20000)
            {
                hour.Ips.Add(r.Ip);
                hour.Visitors++;
            }
        }

        changed = true;
    }

    private static void Count(Dictionary<string, int> dict, string key)
    {
        if (key.Length == 0)
            return;
        if (dict.TryGetValue(key, out var n))
            dict[key] = n + 1;
        else if (dict.Count < MaxKeysPerHour)
            dict[key] = 1;
    }

    /// <summary>Complete new lines after a byte offset. A file that became shorter was emptied: start again from the top.</summary>
    internal static List<string> ReadNew(string path, long offset, out long newOffset)
    {
        newOffset = offset;
        var lines = new List<string>();
        try
        {
            if (!File.Exists(path))
            {
                newOffset = 0;
                return lines;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset)
                offset = 0;
            newOffset = offset;
            var count = (int)Math.Min(fs.Length - offset, MaxChunkBytes);
            if (count <= 0)
                return lines;
            var buffer = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var r = fs.Read(buffer, read, count - read);
                if (r <= 0)
                    break;
                read += r;
            }

            var last = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (last < 0)
                return lines;
            newOffset = offset + last + 1;
            lines.AddRange(Encoding.UTF8.GetString(buffer, 0, last + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        catch (IOException)
        {
            // unreadable right now: try again next time
        }
        catch (UnauthorizedAccessException)
        {
        }

        return lines;
    }

    private static History Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<History>(File.ReadAllText(path), Json) ?? new History();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // damaged history: start over rather than fail the dashboard
        }

        return new History();
    }

    private static void Save(string path, History history)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(history, Json), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    public static void Delete(string rootDirectory, Guid uuid)
    {
        lock (Gate)
        {
            try { File.Delete(HistoryPath(rootDirectory, uuid)); } catch (IOException) { /* ignore */ }
        }
    }

    // ---- reading the history ---------------------------------------------------------------------------------------------

    /// <summary>Overview plus the newest events; cached for a few seconds because the dashboard polls.</summary>
    public static object Compute(string rootDirectory, WebSpace space, string? range, int eventLimit, DateTimeOffset? now = null)
    {
        eventLimit = Math.Clamp(eventLimit, 1, 200);
        var key = $"{space.Uuid}|{range}|{eventLimit}";
        if (now is null)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromSeconds(10))
                    return hit.value;
            }
        }

        var at = now ?? DateTimeOffset.UtcNow;
        Absorb(rootDirectory, space, at);
        History history;
        lock (Gate)
            history = Load(HistoryPath(rootDirectory, space.Uuid));
        var value = Build(history, range, eventLimit, at);
        if (now is null)
        {
            lock (Gate)
                Cache[key] = (DateTime.UtcNow, value);
        }

        return value;
    }

    private static object Build(History history, string? range, int eventLimit, DateTimeOffset now)
    {
        var (span, bucket) = RangeOf(range);
        var from = now - span;
        var fromHour = from.ToUnixTimeSeconds() / 3600;
        var toHour = now.ToUnixTimeSeconds() / 3600;
        var hours = history.Hours.Where(kv => long.TryParse(kv.Key, out var h) && h >= fromHour && h <= toHour)
            .Select(kv => (hour: long.Parse(kv.Key, CultureInfo.InvariantCulture), data: kv.Value)).ToList();
        var attacks = history.Events.Where(e => e.Time >= from.ToUnixTimeMilliseconds()).OrderByDescending(e => e.Time).ToList();
        var blocked = attacks.Where(a => a.Action == "blocked").ToList();
        var detected = attacks.Where(a => a.Action == "detected").ToList();

        long Slot(long unixSeconds) => unixSeconds / (long)bucket.TotalSeconds * (long)bucket.TotalSeconds;
        var buckets = new SortedDictionary<long, (int req, int blk)>();
        for (var t = Slot(from.ToUnixTimeSeconds()); t <= Slot(now.ToUnixTimeSeconds()); t += (long)bucket.TotalSeconds)
            buckets[t] = (0, 0);
        foreach (var (hour, data) in hours)
        {
            var s = Slot(hour * 3600);
            if (buckets.TryGetValue(s, out var b))
                buckets[s] = (b.req + data.Requests, b.blk);
        }

        foreach (var a in blocked)
        {
            var s = Slot(a.Time / 1000);
            if (buckets.TryGetValue(s, out var b))
                buckets[s] = (b.req, b.blk + 1);
        }

        List<object> Merge(Func<Hour, Dictionary<string, int>> pick, string label, int take = 10)
        {
            var total = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, data) in hours)
            {
                foreach (var kv in pick(data))
                    total[kv.Key] = total.GetValueOrDefault(kv.Key) + kv.Value;
            }

            return total.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(take)
                .Select(kv => (object)new Dictionary<string, object> { [label] = kv.Key, ["count"] = kv.Value }).ToList();
        }

        static List<object> Top(IEnumerable<Attack> items, Func<Attack, string> key, string label) =>
            items.Select(key).Where(k => k.Length > 0).GroupBy(k => k, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(10)
                .Select(g => (object)new Dictionary<string, object> { [label] = g.Key, ["count"] = g.Count() }).ToList();

        var overview = new Dictionary<string, object>
        {
            ["engine_available"] = CorazaProbe.IsAvailable(),
            ["bucket_seconds"] = (int)bucket.TotalSeconds,
            ["totals"] = new Dictionary<string, object>
            {
                ["requests"] = hours.Sum(h => h.data.Requests),
                ["blocked"] = blocked.Count,
                ["detected"] = detected.Count,
                // Counted per hour, so a visitor who comes back in another hour is counted again.
                ["unique_ips"] = hours.Sum(h => h.data.Visitors),
                ["blocked_ips"] = blocked.Select(a => a.Ip).Where(i => i.Length > 0).Distinct().Count(),
                ["status_4xx"] = hours.Sum(h => h.data.Status4xx),
                ["status_5xx"] = hours.Sum(h => h.data.Status5xx),
            },
            ["series"] = buckets.Select(kv => (object)new Dictionary<string, object>
            {
                ["ts"] = DateTimeOffset.FromUnixTimeSeconds(kv.Key).ToString("o", CultureInfo.InvariantCulture),
                ["requests"] = kv.Value.req,
                ["blocked"] = kv.Value.blk,
            }).ToList(),
            ["status_codes"] = Merge(h => h.Status, "code", 12),
            ["top_ips"] = Top(attacks, a => a.Ip, "ip"),
            ["top_categories"] = Top(attacks, a => a.Category, "category"),
            ["top_paths"] = Top(attacks, a => a.Path, "path"),
            ["top_hosts"] = Merge(h => h.Hosts, "name"),
            ["referrers"] = Merge(h => h.Referrers, "name"),
            ["browsers"] = Merge(h => h.Browsers, "name"),
            ["systems"] = Merge(h => h.Systems, "name"),
            // ISO codes; the panel shows names and places them on the globe.
            ["countries"] = Merge(h => h.Countries, "name", 12),
        };

        var events = attacks.Take(eventLimit).Select(a => (object)new Dictionary<string, object>
        {
            ["id"] = a.Id,
            ["time"] = DateTimeOffset.FromUnixTimeMilliseconds(a.Time).ToString("o", CultureInfo.InvariantCulture),
            ["ip"] = a.Ip,
            ["method"] = a.Method,
            ["host"] = a.Host,
            ["path"] = a.Path,
            ["rule_id"] = a.RuleId,
            ["rule_msg"] = a.RuleMsg,
            ["category"] = a.Category,
            ["action"] = a.Action,
        }).ToList();

        return new Dictionary<string, object> { ["overview"] = overview, ["events"] = events };
    }

    // ---- parsing ---------------------------------------------------------------------------------------------------------

    private static List<string> Domains(WebSpace space) =>
        space.Domains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim().ToLowerInvariant()).Distinct().ToList();

    internal static string RefusalMessage(int status) => status switch
    {
        405 => "Request method not allowed",
        413 => "Request body too large",
        414 => "URL too long",
        429 => "Too many requests",
        _ => "Refused by an access rule",
    };

    private static Req? ParseAccess(string line)
    {
        if (line.Length < 20 || line[0] != '{')
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("request", out var rq) || !root.TryGetProperty("ts", out var tsEl) || !tsEl.TryGetDouble(out var ts))
                return null;
            var status = root.TryGetProperty("status", out var st) && st.TryGetInt32(out var sc) ? sc : 0;
            string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string Header(string name)
            {
                if (!rq.TryGetProperty("headers", out var h) || h.ValueKind != JsonValueKind.Object)
                    return "";
                foreach (var p in h.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                        return p.Value[0].GetString() ?? "";
                }

                return "";
            }

            var uri = Str(rq, "uri");
            var q = uri.IndexOf('?');
            var ip = Str(rq, "client_ip");
            if (ip.Length == 0)
                ip = Str(rq, "remote_ip");
            var refused = root.TryGetProperty("uuid", out _) && RefusalStatuses.Contains(status);
            return new Req(DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)), ip, Str(rq, "method"), Str(rq, "host").ToLowerInvariant(),
                Clip(q >= 0 ? uri[..q] : uri, 200), status, Header("Referer"), Header("User-Agent"), refused);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static List<Attack> ParseAudit(IEnumerable<string> lines, IReadOnlyCollection<string> domains)
    {
        var result = new List<Attack>();
        if (domains.Count == 0)
            return result;
        var own = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.Length < 20 || line[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("transaction", out var tx))
                    continue;
                var host = tx.TryGetProperty("server_id", out var sv) ? sv.GetString() ?? "" : "";
                if (!own.Contains(host))
                    continue;
                var ns = tx.TryGetProperty("unix_timestamp", out var u) && u.TryGetInt64(out var nsv) ? nsv : 0;
                var interrupted = tx.TryGetProperty("is_interrupted", out var ii) && ii.ValueKind == JsonValueKind.True;
                string ip = tx.TryGetProperty("client_ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
                string method = "", uri = "";
                if (tx.TryGetProperty("request", out var rq) && rq.ValueKind == JsonValueKind.Object)
                {
                    method = rq.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                    uri = rq.TryGetProperty("uri", out var ur) ? ur.GetString() ?? "" : "";
                }

                var q = uri.IndexOf('?');
                var (ruleId, msg, category) = Pick(doc.RootElement);
                result.Add(new Attack
                {
                    Time = ns / 1_000_000, Id = tx.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "", Ip = ip, Method = method,
                    Host = host.ToLowerInvariant(), Path = Clip(q >= 0 ? uri[..q] : uri, 200), RuleId = ruleId, RuleMsg = Clip(msg, 200), Category = category,
                    Action = interrupted ? "blocked" : "detected",
                });
            }
            catch (JsonException)
            {
                // skip a damaged line
            }
        }

        return result;
    }

    /// <summary>The most telling rule of a transaction: the first one with an attack or custom tag, not the anomaly summary.</summary>
    private static (string id, string msg, string category) Pick(JsonElement root)
    {
        (string id, string msg, string category)? fallback = null;
        if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in msgs.EnumerateArray())
            {
                var text = m.TryGetProperty("error_message", out var em) ? em.GetString() ?? "" : "";
                var id = IdRe.Match(text) is { Success: true } mi ? mi.Groups[1].Value : "";
                var msg = MsgRe.Match(text) is { Success: true } mm ? mm.Groups[1].Value : "";
                var tags = TagRe.Matches(text).Select(t => t.Groups[1].Value).ToList();
                var category = tags.Contains("fq-custom") ? "custom" : tags.Select(t => TagToCategory.GetValueOrDefault(t)).FirstOrDefault(c => c is not null) ?? "";
                if (category.Length > 0)
                    return (id, msg, category);
                fallback ??= (id, msg, "other");
            }
        }

        return fallback ?? ("", "", "other");
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    internal static string ReferrerHost(string referrer, string ownHost)
    {
        if (string.IsNullOrWhiteSpace(referrer) || !Uri.TryCreate(referrer, UriKind.Absolute, out var u))
            return "";
        var host = u.Host.ToLowerInvariant();
        return host == ownHost || host == "www." + ownHost || "www." + host == ownHost ? "" : host;
    }

    internal static string Browser(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return "";
        var l = ua.ToLowerInvariant();
        if (l.Contains("bot") || l.Contains("spider") || l.Contains("crawl") || l.Contains("curl/") || l.Contains("python") || l.Contains("wget"))
            return "Bot / script";
        if (l.Contains("edg/")) return "Edge";
        if (l.Contains("opr/") || l.Contains("opera")) return "Opera";
        if (l.Contains("firefox/")) return "Firefox";
        if (l.Contains("chrome/") || l.Contains("crios/")) return "Chrome";
        if (l.Contains("safari/")) return "Safari";
        return "Other";
    }

    internal static string OperatingSystem(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return "";
        var l = ua.ToLowerInvariant();
        if (l.Contains("android")) return "Android";
        if (l.Contains("iphone") || l.Contains("ipad") || l.Contains("ios")) return "iOS";
        if (l.Contains("windows")) return "Windows";
        if (l.Contains("mac os") || l.Contains("macintosh")) return "macOS";
        if (l.Contains("linux") || l.Contains("x11")) return "Linux";
        return "Other";
    }
}
