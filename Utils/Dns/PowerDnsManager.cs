using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Dns;

public sealed class PowerDnsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppConfig _config;
    private readonly HttpClient _client;

    public PowerDnsManager(AppConfig config)
    {
        _config = config;
        var apiKey = PowerDnsProbe.ResolveApiKey(config);
        if (apiKey.Length == 0)
            throw new InvalidOperationException("PowerDNS API key is not configured.");
        _client = PowerDnsProbe.BuildClient(config, apiKey);
    }

    public object ProbeStatus() => new
    {
        available = PowerDnsProbe.IsAvailable(_config),
        binary = PowerDnsProbe.ResolveBinary(),
        api_url = _config.System.Dns.PowerDnsApiUrl,
    };

    public IReadOnlyList<object> ListZones()
    {
        var zones = new List<object>();
        var url = "/api/v1/servers/localhost/zones";
        while (!string.IsNullOrEmpty(url))
        {
            using var response = _client.GetAsync(url).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStream();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var zone in data.EnumerateArray())
                {
                    var name = zone.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var id = zone.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? name : name;
                    zones.Add(new { id = TrimZoneId(id), name = TrimZoneId(name), status = "active" });
                }
            }

            url = doc.RootElement.TryGetProperty("next", out var next) ? next.GetString() ?? "" : "";
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(url);
                url = uri.PathAndQuery;
            }
        }

        return zones;
    }

    public string CreateZone(string zoneName, string? nodeIp = null)
    {
        zoneName = NormalizeZoneName(zoneName);
        var payload = new
        {
            name = zoneName,
            kind = "Native",
            nameservers = new[] { $"ns1.{TrimZoneId(zoneName)}." },
        };
        using var response = _client.PostAsJsonAsync("/api/v1/servers/localhost/zones", payload, JsonOptions)
            .GetAwaiter()
            .GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            if (!string.IsNullOrWhiteSpace(nodeIp))
                SeedZoneGlue(zoneName, nodeIp.Trim());
            return TrimZoneId(zoneName);
        }

        response.EnsureSuccessStatusCode();
        if (!string.IsNullOrWhiteSpace(nodeIp))
            SeedZoneGlue(zoneName, nodeIp.Trim());
        return TrimZoneId(zoneName);
    }

    /// <summary>Default NS hostnames for a new apex zone.</summary>
    public static IReadOnlyList<string> DefaultNameservers(string zoneName)
    {
        var apex = TrimZoneId(NormalizeZoneName(zoneName));
        return [$"ns1.{apex}"];
    }

    public string? ResolveZoneId(string zoneName)
    {
        zoneName = NormalizeZoneName(zoneName);
        try
        {
            using var response = _client.GetAsync($"/api/v1/servers/localhost/zones/{Uri.EscapeDataString(zoneName)}")
                .GetAwaiter()
                .GetResult();
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return TrimZoneId(zoneName);
        }
        catch
        {
            return null;
        }
    }

    public object ListRecords(string zoneId, string? type = null, string? name = null, int page = 1, int perPage = 100)
    {
        var zone = NormalizeZoneName(zoneId);
        using var response = _client.GetAsync($"/api/v1/servers/localhost/zones/{Uri.EscapeDataString(zone)}")
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        using var doc = JsonDocument.Parse(stream);

        var records = new List<Dictionary<string, object?>>();
        if (!doc.RootElement.TryGetProperty("rrsets", out var rrsets) || rrsets.ValueKind != JsonValueKind.Array)
        {
            return new { records, page, per_page = perPage, total_count = 0 };
        }

        var typeFilter = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToUpperInvariant();
        var nameFilter = string.IsNullOrWhiteSpace(name) ? null : NormalizeRecordName(name, zone);

        foreach (var rrset in rrsets.EnumerateArray())
        {
            var rrType = rrset.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (rrType is "SOA" or "")
                continue;
            var rrName = rrset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (typeFilter is not null && !rrType.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (nameFilter is not null && !rrName.Equals(nameFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var ttl = rrset.TryGetProperty("ttl", out var ttlEl) ? ttlEl.GetInt32() : 300;
            if (!rrset.TryGetProperty("records", out var recs) || recs.ValueKind != JsonValueKind.Array)
                continue;

            var idx = 0;
            foreach (var rec in recs.EnumerateArray())
            {
                var content = rec.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var displayContent = content.Trim('"');
                int? mxPriority = null;
                if (rrType == "MX")
                {
                    var (priority, target) = ParseMxParts(content);
                    mxPriority = priority;
                    displayContent = target;
                }

                records.Add(new Dictionary<string, object?>
                {
                    ["id"] = BuildRecordId(rrType, rrName, idx),
                    ["type"] = rrType,
                    ["name"] = DisplayName(rrName, zone),
                    ["content"] = displayContent,
                    ["ttl"] = ttl,
                    ["proxied"] = false,
                    ["priority"] = mxPriority,
                });
                idx++;
            }
        }

        var total = records.Count;
        var skip = Math.Max(0, (page - 1) * perPage);
        var pageRecords = records.Skip(skip).Take(perPage).ToList();

        return new { records = pageRecords, page, per_page = perPage, total_count = total };
    }

    public Dictionary<string, object?> CreateRecord(string zoneId, Dictionary<string, object?> payload)
    {
        var zone = NormalizeZoneName(zoneId);
        var type = (payload.GetValueOrDefault("type")?.ToString() ?? "").ToUpperInvariant();
        var name = NormalizeRecordName(payload.GetValueOrDefault("name")?.ToString() ?? "", zone);
        var content = payload.GetValueOrDefault("content")?.ToString()?.Trim() ?? "";
        var ttl = payload.TryGetValue("ttl", out var ttlObj) && int.TryParse(ttlObj?.ToString(), out var ttlVal) ? ttlVal : 300;
        if (type is "" or "SOA")
            throw new InvalidOperationException("Invalid record type.");
        if (content.Length == 0)
            throw new InvalidOperationException("content is required.");

        var rrContent = FormatRecordContent(type, content, payload);
        PatchRrset(zone, name, type, ttl, [rrContent], changetype: "REPLACE");
        int? mxPri = null;
        var displayContent = content;
        if (type == "MX")
        {
            var parsed = ParseMxParts(rrContent);
            mxPri = parsed.Priority;
            displayContent = parsed.Target;
        }

        return new Dictionary<string, object?>
        {
            ["id"] = BuildRecordId(type, name, 0),
            ["type"] = type,
            ["name"] = DisplayName(name, zone),
            ["content"] = displayContent,
            ["ttl"] = ttl,
            ["proxied"] = false,
            ["priority"] = mxPri,
        };
    }

    public Dictionary<string, object?> UpdateRecord(string zoneId, string recordId, Dictionary<string, object?> payload)
    {
        var zone = NormalizeZoneName(zoneId);
        var (type, name, index) = ParseRecordId(recordId);
        var list = ListRecords(zoneId, type, DisplayName(name, zone), 1, 100);
        var recordsProp = list.GetType().GetProperty("records")?.GetValue(list) as IEnumerable<Dictionary<string, object?>>
            ?? [];
        var existing = recordsProp.ToList();
        if (index >= existing.Count)
            throw new InvalidOperationException("Record not found.");

        var newType = (payload.GetValueOrDefault("type")?.ToString() ?? type).ToUpperInvariant();
        var newName = NormalizeRecordName(payload.GetValueOrDefault("name")?.ToString() ?? DisplayName(name, zone), zone);
        var newContent = payload.GetValueOrDefault("content")?.ToString()?.Trim() ?? existing[index].GetValueOrDefault("content")?.ToString() ?? "";
        var ttl = payload.TryGetValue("ttl", out var ttlObj) && int.TryParse(ttlObj?.ToString(), out var ttlVal)
            ? ttlVal
            : (int)(existing[index].GetValueOrDefault("ttl") ?? 300);

        if (newType != type || !newName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            DeleteRecord(zoneId, recordId);
            var createPayload = new Dictionary<string, object?>
            {
                ["type"] = newType,
                ["name"] = DisplayName(newName, zone),
                ["content"] = newContent,
                ["ttl"] = ttl,
            };
            if (payload.TryGetValue("priority", out var priorityObj))
                createPayload["priority"] = priorityObj;
            return CreateRecord(zoneId, createPayload);
        }

        var updatePayload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["content"] = newContent,
            ["ttl"] = ttl,
        };
        if (payload.TryGetValue("priority", out var priObj))
            updatePayload["priority"] = priObj;

        var formattedNew = FormatRecordContent(type, newContent, updatePayload);
        var contents = existing.Select((r, i) =>
        {
            if (i != index)
            {
                var existingContent = r.GetValueOrDefault("content")?.ToString() ?? "";
                if (type == "MX")
                {
                    var pri = r.GetValueOrDefault("priority") is int p ? p : ParseMxParts(existingContent).Priority;
                    return FormatMxRrContent(pri, existingContent);
                }

                return existingContent;
            }

            return formattedNew;
        }).ToList();
        PatchRrset(zone, name, type, ttl, contents, changetype: "REPLACE");
        int? mxPri = null;
        var displayContent = newContent;
        if (type == "MX")
        {
            var parsed = ParseMxParts(formattedNew);
            mxPri = parsed.Priority;
            displayContent = parsed.Target;
        }

        return new Dictionary<string, object?>
        {
            ["id"] = BuildRecordId(type, name, index),
            ["type"] = type,
            ["name"] = DisplayName(name, zone),
            ["content"] = displayContent,
            ["ttl"] = ttl,
            ["proxied"] = false,
            ["priority"] = mxPri,
        };
    }

    public void DeleteRecord(string zoneId, string recordId)
    {
        var zone = NormalizeZoneName(zoneId);
        var (type, name, index) = ParseRecordId(recordId);
        var list = ListRecords(zoneId, type, DisplayName(name, zone), 1, 100);
        var recordsProp = list.GetType().GetProperty("records")?.GetValue(list) as IEnumerable<Dictionary<string, object?>>
            ?? [];
        var existing = recordsProp.ToList();
        if (existing.Count == 0)
            return;

        if (existing.Count == 1)
        {
            PatchRrset(zone, name, type, 300, [], changetype: "DELETE");
            return;
        }

        var contents = existing
            .Where((_, i) => i != index)
            .Select(r =>
            {
                var existingContent = r.GetValueOrDefault("content")?.ToString() ?? "";
                if (type == "MX")
                {
                    var pri = r.GetValueOrDefault("priority") is int p ? p : ParseMxParts(existingContent).Priority;
                    return FormatMxRrContent(pri, existingContent);
                }

                return existingContent;
            })
            .ToList();
        var ttl = (int)(existing[0].GetValueOrDefault("ttl") ?? 300);
        PatchRrset(zone, name, type, ttl, contents, changetype: "REPLACE");
    }

    public Dictionary<string, object> UpsertARecord(string zoneId, string name, string ip, int ttl = 300, bool proxied = false)
    {
        _ = proxied;
        var zone = NormalizeZoneName(zoneId);
        var fqdn = NormalizeRecordName(name, zone);
        try
        {
            var existing = ListRecords(zoneId, "A", DisplayName(fqdn, zone), 1, 10);
            var recordsProp = existing.GetType().GetProperty("records")?.GetValue(existing) as IEnumerable<Dictionary<string, object?>>
                ?? [];
            var list = recordsProp.ToList();
            if (list.Count > 0 && list[0].GetValueOrDefault("content")?.ToString() == ip)
                return new Dictionary<string, object> { ["ok"] = true, ["action"] = "unchanged" };

            if (list.Count > 0)
            {
                UpdateRecord(zoneId, list[0]["id"]?.ToString() ?? BuildRecordId("A", fqdn, 0), new Dictionary<string, object?>
                {
                    ["type"] = "A",
                    ["name"] = DisplayName(fqdn, zone),
                    ["content"] = ip,
                    ["ttl"] = ttl,
                });
                return new Dictionary<string, object> { ["ok"] = true, ["action"] = "updated" };
            }

            CreateRecord(zoneId, new Dictionary<string, object?>
            {
                ["type"] = "A",
                ["name"] = DisplayName(fqdn, zone),
                ["content"] = ip,
                ["ttl"] = ttl,
            });
            return new Dictionary<string, object> { ["ok"] = true, ["action"] = "created" };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
        }
    }

    public Dictionary<string, object> CreateTxtRecord(string zoneId, string name, string content, int ttl = 120)
    {
        var zone = NormalizeZoneName(zoneId);
        var fqdn = NormalizeRecordName(name, zone);
        try
        {
            var quoted = content.StartsWith('"') ? content : $"\"{content}\"";
            var existing = ListRecords(zoneId, "TXT", DisplayName(fqdn, zone), 1, 100);
            var recordsProp = existing.GetType().GetProperty("records")?.GetValue(existing) as IEnumerable<Dictionary<string, object?>>
                ?? [];
            var contents = recordsProp.Select(r => r.GetValueOrDefault("content")?.ToString() ?? "").ToList();
            if (!contents.Contains(content.Trim('"')))
                contents.Add(content.Trim('"'));
            PatchRrset(zone, fqdn, "TXT", ttl, contents.Select(c => c.StartsWith('"') ? c : $"\"{c}\"").ToList(), changetype: "REPLACE");
            return new Dictionary<string, object> { ["ok"] = true, ["action"] = "created" };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
        }
    }

    public Dictionary<string, object> DeleteTxtRecords(string zoneId, string name, string? content = null)
    {
        var zone = NormalizeZoneName(zoneId);
        var fqdn = NormalizeRecordName(name, zone);
        try
        {
            var existing = ListRecords(zoneId, "TXT", DisplayName(fqdn, zone), 1, 100);
            var recordsProp = existing.GetType().GetProperty("records")?.GetValue(existing) as IEnumerable<Dictionary<string, object?>>
                ?? [];
            var list = recordsProp.ToList();
            if (list.Count == 0)
                return new Dictionary<string, object> { ["ok"] = true, ["deleted"] = 0 };

            if (content is null)
            {
                PatchRrset(zone, fqdn, "TXT", 120, [], changetype: "DELETE");
                return new Dictionary<string, object> { ["ok"] = true, ["deleted"] = list.Count };
            }

            var needle = content.Trim('"');
            var remaining = list
                .Select(r => r.GetValueOrDefault("content")?.ToString() ?? "")
                .Where(c => c.Trim('"') != needle)
                .ToList();
            var deleted = list.Count - remaining.Count;
            if (remaining.Count == 0)
                PatchRrset(zone, fqdn, "TXT", 120, [], changetype: "DELETE");
            else
                PatchRrset(zone, fqdn, "TXT", 120, remaining.Select(c => $"\"{c.Trim('"')}\"").ToList(), changetype: "REPLACE");

            return new Dictionary<string, object> { ["ok"] = true, ["deleted"] = deleted };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["deleted"] = 0, ["error"] = ex.Message };
        }
    }

    private void PatchRrset(string zone, string name, string type, int ttl, IReadOnlyList<string> contents, string changetype)
    {
        var rrset = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["type"] = type.ToUpperInvariant(),
            ["ttl"] = Math.Max(60, ttl),
            ["changetype"] = changetype,
        };
        if (changetype != "DELETE")
        {
            rrset["records"] = contents.Select(c => new { content = c, disabled = false }).ToList();
        }

        var payload = new { rrsets = new[] { rrset } };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/servers/localhost/zones/{Uri.EscapeDataString(zone)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = _client.Send(request);
        response.EnsureSuccessStatusCode();
    }

    private static string NormalizeZoneName(string zoneName)
    {
        zoneName = zoneName.Trim().ToLowerInvariant().TrimEnd('.');
        return zoneName + ".";
    }

    private static string TrimZoneId(string zone) => zone.Trim().TrimEnd('.').ToLowerInvariant();

    private static string NormalizeRecordName(string name, string zone)
    {
        name = name.Trim().ToLowerInvariant();
        zone = TrimZoneId(zone);
        if (name is "" or "@" || name == zone)
            return NormalizeZoneName(zone);
        if (!name.EndsWith('.'))
        {
            if (name.EndsWith('.' + zone))
                return name + ".";
            if (!name.Contains('.'))
                return $"{name}.{NormalizeZoneName(zone)}";
            return name + ".";
        }

        return name;
    }

    private static string DisplayName(string fqdn, string zone)
    {
        fqdn = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
        var apex = TrimZoneId(zone);
        if (fqdn == apex)
            return apex;
        if (fqdn.EndsWith('.' + apex))
            return fqdn[..^(apex.Length + 1)];
        return fqdn;
    }

    private static string BuildRecordId(string type, string name, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{type}|{name}|{index}"));

    private static (string Type, string Name, int Index) ParseRecordId(string recordId)
    {
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(recordId));
        var parts = raw.Split('|', 3);
        if (parts.Length < 3)
            throw new InvalidOperationException("Invalid record id.");
        return (parts[0], parts[1], int.Parse(parts[2]));
    }

    private void SeedZoneGlue(string zone, string nodeIp)
    {
        if (!System.Net.IPAddress.TryParse(nodeIp, out _))
            return;

        var apex = TrimZoneId(zone);
        var nsHost = $"ns1.{apex}.";
        PatchRrset(zone, nsHost, "A", 300, [nodeIp], changetype: "REPLACE");
        PatchRrset(zone, NormalizeZoneName(apex), "NS", 300, [nsHost], changetype: "REPLACE");
    }

    private static string FormatRecordContent(string type, string content, IReadOnlyDictionary<string, object?> payload)
    {
        if (type != "MX")
            return content;

        var priority = 10;
        if (payload.TryGetValue("priority", out var priObj) && int.TryParse(priObj?.ToString(), out var parsedPri))
            priority = parsedPri;
        else
            priority = ParseMxParts(content).Priority;

        return FormatMxRrContent(priority, content);
    }

    internal static string FormatMxRrContent(int priority, string target)
    {
        target = target.Trim().Trim('"');
        var parts = target.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out _))
            target = parts[1];
        target = target.TrimEnd('.');
        if (target.Length == 0)
            throw new InvalidOperationException("MX target is required.");
        return $"{priority} {target}.";
    }

    internal static (int Priority, string Target) ParseMxParts(string content)
    {
        content = content.Trim().Trim('"');
        var parts = content.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out var priority))
            return (priority, parts[1].TrimEnd('.'));

        return (10, content.TrimEnd('.'));
    }

    private static int? ParseMxPriority(string content) => ParseMxParts(content).Priority;
}
