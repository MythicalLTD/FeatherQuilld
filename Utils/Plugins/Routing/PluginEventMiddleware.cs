using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Events;

namespace FeatherQuilld.Utils.Plugins.Routing;

/// <summary>
/// Middleware that emits HTTP events and runs route before/after hooks.
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
        {
            await WriteForbiddenAsync(context).ConfigureAwait(false);
            return;
        }

        var hookResult = await routes.RunBeforeHooksAsync(context).ConfigureAwait(false);
        if (hookResult.IsCancelled)
        {
            await WriteForbiddenAsync(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        var executing = new RouteExecutingEvent
        {
            RoutePattern = path,
            Context = context,
        };
        var executingResult = await eventBus.EmitAsync(executing, context.RequestAborted).ConfigureAwait(false);
        if (executingResult.IsCancelled)
        {
            await WriteForbiddenAsync(context).ConfigureAwait(false);
            return;
        }

        Exception? error = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            var executed = new RouteExecutedEvent
            {
                RoutePattern = path,
                Context = context,
                Result = error,
            };
            _ = await eventBus.EmitAsync(executed, CancellationToken.None).ConfigureAwait(false);
            _ = await routes.RunAfterHooksAsync(context, error).ConfigureAwait(false);
        }
    }

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "forbidden",
            message = "Request cancelled by plugin hook.",
        }).ConfigureAwait(false);
    }
}
