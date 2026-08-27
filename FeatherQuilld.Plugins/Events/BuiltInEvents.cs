using Microsoft.AspNetCore.Http;

namespace FeatherQuilld.Plugins.Events;

public sealed class PluginConfiguredEvent
{
    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
}

public sealed class ApplicationStartedEvent
{
    public required IServiceProvider Services { get; init; }
}

public sealed class ApplicationStoppingEvent
{
    public required CancellationToken CancellationToken { get; init; }
}

public sealed class HttpRequestEvent
{
    public required HttpContext Context { get; init; }
    public required string Method { get; init; }
    public required PathString Path { get; init; }
}

public sealed class RouteExecutingEvent
{
    public required string RoutePattern { get; init; }
    public required HttpContext Context { get; init; }
    public object? Result { get; set; }
}

public sealed class RouteExecutedEvent
{
    public required string RoutePattern { get; init; }
    public required HttpContext Context { get; init; }
    public object? Result { get; set; }
}

public sealed class HealthCheckEvent
{
    public required HttpContext Context { get; init; }
    public object? Response { get; set; }
}
