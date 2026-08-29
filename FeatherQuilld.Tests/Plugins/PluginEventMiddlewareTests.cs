using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Routing;
using Microsoft.AspNetCore.Http;

namespace FeatherQuilld.Tests.Plugins;

public class PluginEventMiddlewareTests
{
    [Fact]
    public async Task Invoke_EmitsExecutingAndExecuted_AndRunsAfterHooks()
    {
        var bus = new EventBus();
        var routes = new RouteRegistry();
        var executing = false;
        var executed = false;
        var after = false;

        bus.On<RouteExecutingEvent>(_ =>
        {
            executing = true;
            return HookResult.Continue();
        });
        bus.On<RouteExecutedEvent>(_ =>
        {
            executed = true;
            return HookResult.Continue();
        });
        routes.After("/api/*", (_, _) =>
        {
            after = true;
            return HookResult.Continue();
        });

        var mw = new PluginEventMiddleware(
            _ => Task.CompletedTask,
            bus,
            routes);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/system/health";
        await mw.InvokeAsync(ctx);

        Assert.True(executing);
        Assert.True(executed);
        Assert.True(after);
    }

    [Fact]
    public async Task Invoke_CancelHttpRequest_Returns403()
    {
        var bus = new EventBus();
        bus.On<HttpRequestEvent>(_ => HookResult.Cancel());
        var mw = new PluginEventMiddleware(_ => Task.CompletedTask, bus, new RouteRegistry());
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = "/api/x";
        await mw.InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }
}
