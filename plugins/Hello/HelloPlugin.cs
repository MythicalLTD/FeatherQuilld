using FeatherQuilld.Plugins.Abstractions;
using FeatherQuilld.Plugins.Context;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Plugins.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FeatherQuilld.Plugins.Hello;

/// <summary>Sample plugin demonstrating routes, events, and route hooks.</summary>
public sealed class HelloPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new()
    {
        Id = "hello",
        Name = "Hello Plugin",
        Version = "0.1.0",
        Description = "Adds a greeting route and hooks the health endpoint.",
        Author = "FeatherQuilld",
        MinHostVersion = "0.1.0",
    };

    public void Configure(PluginContext context)
    {
        context.Logger.LogInformation("Hello from {Name}!", Metadata.Name);

        context.Routes
            .MapGet("/api/hello", (HttpContext _) =>
                Results.Json(new { message = "Hello from plugin!", plugin = Metadata.Id }))
            .WithName("hello-greeting")
            .WithTags("Hello");

        context.Events.On<ApplicationStartedEvent>(_ =>
        {
            context.Logger.LogInformation("Host is up — {Name} is live.", Metadata.Name);
            return HookResult.Continue();
        });

        context.Routes.Before("/api/system/*", ctx =>
        {
            context.Logger.LogDebug("Request → {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            return HookResult.Continue();
        });
    }
}
