using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Routing;
using Microsoft.AspNetCore.Http;

namespace FeatherQuilld.Tests.Plugins;

public class RouteRegistryTests
{
    [Fact]
    public void MapGet_RegistersRoute()
    {
        var reg = new RouteRegistry();
        reg.MapGet("/api/x", () => "ok").WithName("x").WithTags("t");
        Assert.Single(reg.Routes);
        Assert.Equal("GET", reg.Routes[0].Method);
        Assert.Equal("/api/x", reg.Routes[0].Pattern);
        Assert.Equal("x", reg.Routes[0].Name);
        Assert.Contains("t", reg.Routes[0].Tags);
    }

    [Fact]
    public async Task RunBeforeHooks_WildcardMatches_AndCanCancel()
    {
        var reg = new RouteRegistry();
        reg.Before("/api/system/*", _ => HookResult.Cancel());

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/system/health";
        var result = await reg.RunBeforeHooksAsync(ctx);
        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task RunBeforeHooks_NonMatch_Continues()
    {
        var reg = new RouteRegistry();
        reg.Before("/api/hello", _ => HookResult.Cancel());
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/other";
        var result = await reg.RunBeforeHooksAsync(ctx);
        Assert.Equal(HookAction.Continue, result.Action);
    }

    [Fact]
    public void ApplyAlterations_MatchesPattern()
    {
        var reg = new RouteRegistry();
        reg.MapGet("/api/hello", () => "h");
        reg.Alter("/api/hello", d => d.Name = "altered");
        reg.ApplyAlterations();
        Assert.Equal("altered", reg.Routes[0].Name);
    }
}
