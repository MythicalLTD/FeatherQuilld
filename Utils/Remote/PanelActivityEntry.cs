namespace FeatherQuilld.Utils.Remote;

public sealed record PanelActivityEntry(
    Guid Webspace,
    string Event,
    object? Metadata = null,
    string? User = null,
    string? Ip = null,
    DateTimeOffset? Timestamp = null);
