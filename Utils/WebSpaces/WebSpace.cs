using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>A hosted workspace on this node. Authoritative data lives on FeatherPanel.</summary>
public sealed class WebSpace
{
    public Guid Uuid { get; set; }
    public string Name { get; set; } = "";
    public string WebPlateId { get; set; } = "";
    public string Runtime { get; set; } = "static";
    public long DiskLimitBytes { get; set; }
    public List<string> Domains { get; set; } = [];
    public bool Ssl { get; set; }
    public int BackendPort { get; set; }
    public int ContainerPort { get; set; }
    public string DocumentRoot { get; set; } = "public";
    public string? ContainerImage { get; set; }
    public string? Startup { get; set; }
    public string? ContainerId { get; set; }
    public string Status { get; set; } = WebSpaceStatus.Installed;
    public string State { get; set; } = WebSpaceState.Stopped;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class WebSpaceStatus
{
    public const string Installed = "installed";
    public const string Installing = "installing";
    public const string Reinstalling = "reinstalling";
    public const string Failed = "failed";
}

public static class WebSpaceState
{
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
}

/// <summary>Panel → daemon create payload (Wings-style).</summary>
public sealed class CreateWebSpaceRequest
{
    public Guid Uuid { get; set; }
    public bool StartOnCompletion { get; set; }
    public bool SkipScripts { get; set; }
}

public sealed record WebSpaceResponse(
    Guid Uuid,
    string Name,
    [property: JsonPropertyName("webplate_id")] string WebPlateId,
    string Runtime,
    [property: JsonPropertyName("disk_limit_bytes")] long DiskLimitBytes,
    [property: JsonPropertyName("disk_used_bytes")] long DiskUsedBytes,
    IReadOnlyList<string> Domains,
    bool Ssl,
    [property: JsonPropertyName("backend_port")] int BackendPort,
    [property: JsonPropertyName("container_port")] int ContainerPort,
    [property: JsonPropertyName("document_root")] string DocumentRoot,
    [property: JsonPropertyName("container_image")] string? ContainerImage,
    string Status,
    string State,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record WebSpaceStatusResponse(
    Guid Uuid,
    string Status,
    string State,
    [property: JsonPropertyName("backend_port")] int BackendPort,
    [property: JsonPropertyName("container_id")] string? ContainerId);

public static partial class WebSpaceValidation
{
    [GeneratedRegex(
        @"^(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DomainRegex();

    public static bool IsValidDomain(string domain) =>
        !string.IsNullOrWhiteSpace(domain) && DomainRegex().IsMatch(domain.Trim().TrimEnd('.'));
}
