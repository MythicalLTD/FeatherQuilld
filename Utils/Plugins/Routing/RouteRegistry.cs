using System.Text.RegularExpressions;
using FeatherQuilld.Plugins.Sdk.Events;
using FeatherQuilld.Plugins.Sdk.Routing;
using Microsoft.AspNetCore.Http;
using SdkRouteBuilder = FeatherQuilld.Plugins.Sdk.Routing.RouteBuilder;

namespace FeatherQuilld.Utils.Plugins.Routing;

/// <summary>Collects plugin routes and route hooks until the HTTP pipeline is built.</summary>
public sealed class RouteRegistry : IRouteRegistry
{
    private readonly List<RouteDescriptor> _routes = [];
    private readonly List<RouteHook> _beforeHooks = [];
    private readonly List<RouteHook> _afterHooks = [];
    private readonly List<(string Pattern, Action<RouteDescriptor> Alter)> _alterations = [];

    public IReadOnlyList<RouteDescriptor> Routes => _routes;

    public SdkRouteBuilder MapGet(string pattern, Delegate handler, string? name = null) =>
        Map("GET", pattern, handler, name);

    public SdkRouteBuilder MapPost(string pattern, Delegate handler, string? name = null) =>
        Map("POST", pattern, handler, name);

    public SdkRouteBuilder MapPut(string pattern, Delegate handler, string? name = null) =>
        Map("PUT", pattern, handler, name);

    public SdkRouteBuilder MapDelete(string pattern, Delegate handler, string? name = null) =>
        Map("DELETE", pattern, handler, name);

    public SdkRouteBuilder MapPatch(string pattern, Delegate handler, string? name = null) =>
        Map("PATCH", pattern, handler, name);

    public void Before(string pattern, Func<HttpContext, HookResult> hook, int priority = 0) =>
        _beforeHooks.Add(new RouteHook(pattern, priority, (ctx, _) => Task.FromResult(hook(ctx))));

    public void After(string pattern, Func<HttpContext, object?, HookResult> hook, int priority = 0) =>
        _afterHooks.Add(new RouteHook(pattern, priority, (ctx, _) => Task.FromResult(hook(ctx, null))));

    public void Alter(string pattern, Action<RouteDescriptor> alter) =>
        _alterations.Add((pattern, alter));

    internal void ApplyAlterations()
    {
        foreach (var route in _routes)
        {
            foreach (var (pattern, alter) in _alterations)
            {
                if (MatchesPattern(route.Pattern, pattern))
                    alter(route);
            }
        }
    }

    internal async Task<HookResult> RunBeforeHooksAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        foreach (var hook in _beforeHooks.OrderBy(h => h.Priority))
        {
            if (!MatchesPattern(path, hook.Pattern))
                continue;

            var result = await hook.Handler(context, CancellationToken.None).ConfigureAwait(false);
            if (result.Action != HookAction.Continue)
                return result;
        }

        return HookResult.Continue();
    }

    private SdkRouteBuilder Map(string method, string pattern, Delegate handler, string? name)
    {
        var descriptor = new RouteDescriptor
        {
            Pattern = pattern,
            Method = method,
            Handler = handler,
            Name = name,
        };

        _routes.Add(descriptor);
        return new SdkRouteBuilder { Descriptor = descriptor };
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        if (pattern == "*" || pattern == "**")
            return true;

        if (pattern.EndsWith('*') && pattern.Length > 1)
            return path.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);

        return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(path, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                   RegexOptions.IgnoreCase);
    }

    private sealed record RouteHook(
        string Pattern,
        int Priority,
        Func<HttpContext, CancellationToken, Task<HookResult>> Handler);
}
