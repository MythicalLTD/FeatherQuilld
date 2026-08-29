using System.Text.Json;

namespace FeatherQuilld.Utils.WebSpaces.Schedules;

/// <summary>
/// Parses schedule task payloads for <c>command</c> actions.
/// Accepts a raw shell string or Wings-style JSON <c>{"command":"..."}</c>.
/// </summary>
public static class ScheduleCommandPayload
{
    public static string Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Schedule command payload is empty.");

        var trimmed = payload.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("command", out var cmd)
                        && cmd.ValueKind == JsonValueKind.String)
                    {
                        var value = cmd.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(value))
                            return value;
                    }

                    throw new ArgumentException("Schedule command payload JSON has no usable command.");
                }
            }
            catch (JsonException)
            {
                // Fall through and treat as a literal command string.
            }
        }

        return trimmed;
    }
}
