using FeatherQuilld.Plugins.Sdk.Events;
using FeatherQuilld.Utils.Plugins.Events;

namespace FeatherQuilld.Utils.Plugins.Routing;

/// <summary>
/// Middleware that emits HTTP events and runs route before-hooks.
/// Lets plugins cancel requests before they reach controllers.
/// </summary>
public sealed class PluginEventMiddleware(RequestDelegate next, EventBus eventBus, RouteRegistry routes)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestEvent = new HttpRequestEvent
        {
            Context = context,
            Method = context.Request.Method,
            Path = context.Request.Path,
        };

        var requestResult = await eventBus.EmitAsync(requestEvent, context.RequestAborted).ConfigureAwait(false);
        if (requestResult.IsCancelled)
            return;

        var hookResult = await routes.RunBeforeHooksAsync(context).ConfigureAwait(false);
        if (hookResult.IsCancelled)
            return;

        await next(context).ConfigureAwait(false);
    }
}
