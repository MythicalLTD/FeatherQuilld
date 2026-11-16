using System.Net;
using System.Security.Cryptography;
using System.Text;
using FeatherQuilld.Utils.Config.Api;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Observability;

/// <summary>Access rules for the metrics and health endpoints.</summary>
public static class ObservabilityAccess
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public static string NormalizedMetricsPath(ApiMetricsConfig metrics)
    {
        var path = string.IsNullOrWhiteSpace(metrics.Path) ? "/metrics" : metrics.Path.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    public static bool HasDedicatedPort(ApiConfig api) =>
        api.Metrics.Enabled && api.Metrics.Port > 0 && api.Metrics.Port != api.Port;

    public static bool IsHealthPath(PathString path)
    {
        var value = path.HasValue ? path.Value!.TrimEnd('/') : "";
        return value.Equals(LivePath, StringComparison.OrdinalIgnoreCase) ||
               value.Equals(ReadyPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bearer from the Authorization header (never from the query string, so it stays out of logs).</summary>
    public static string? ExtractBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>True when the request carries the scrape token or the node token.</summary>
    public static bool HasValidToken(HttpRequest request, AppConfig config)
    {
        var token = ExtractBearer(request);
        if (string.IsNullOrEmpty(token))
            return false;

        var scrape = config.Api.Metrics.Token?.Trim();
        if (!string.IsNullOrEmpty(scrape) && FixedTimeEquals(token, scrape))
            return true;

        return FixedTimeEquals(token, config.BearerToken);
    }

    /// <summary>Empty allowlist = any address. Entries are IPs or CIDR ranges; invalid entries are ignored.</summary>
    public static bool IsAllowedIp(IPAddress? remote, IReadOnlyList<string>? allowed)
    {
        if (allowed is null || allowed.Count == 0)
            return true;
        if (remote is null)
            return false;

        var candidates = new List<IPAddress> { remote };
        if (remote.IsIPv4MappedToIPv6)
            candidates.Add(remote.MapToIPv4());

        foreach (var raw in allowed)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry))
                continue;

            if (!entry.Contains('/'))
            {
                if (IPAddress.TryParse(entry, out var single) && candidates.Any(c => c.Equals(single)))
                    return true;
                continue;
            }

            if (!IPNetwork.TryParse(entry, out var network))
                continue;
            if (candidates.Any(network.Contains))
                return true;
        }

        return false;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

/// <summary>
/// Guards the scrape endpoint (allowlist + token) and keeps the dedicated metrics listener from serving the API.
/// Runs before authentication so Prometheus can use a scrape-only token.
/// </summary>
public sealed class ObservabilityAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AppConfig _config;
    private readonly string _metricsPath;

    public ObservabilityAccessMiddleware(RequestDelegate next, AppConfig config)
    {
        _next = next;
        _config = config;
        _metricsPath = ObservabilityAccess.NormalizedMetricsPath(config.Api.Metrics);
    }

    public Task InvokeAsync(HttpContext context)
    {
        var api = _config.Api;
        var path = context.Request.Path;
        var isMetrics = api.Metrics.Enabled && IsMetricsPath(path, _metricsPath);

        if (ObservabilityAccess.HasDedicatedPort(api))
        {
            var onMetricsPort = context.Connection.LocalPort == api.Metrics.Port;
            if (onMetricsPort && !isMetrics && !(api.Health.Enabled && ObservabilityAccess.IsHealthPath(path)))
                return NotFound(context);
            if (!onMetricsPort && isMetrics)
                return NotFound(context);
        }

        if (!isMetrics)
            return _next(context);

        if (!ObservabilityAccess.IsAllowedIp(context.Connection.RemoteIpAddress, api.Metrics.AllowedIps))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { error = "forbidden", message = "Address not allowed to scrape metrics." });
        }

        if (!api.Metrics.AllowAnonymous && !ObservabilityAccess.HasValidToken(context.Request, _config))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return context.Response.WriteAsJsonAsync(new { error = "unauthorized", message = "Missing or invalid metrics token." });
        }

        return _next(context);
    }

    /// <summary>Routing also matches a trailing slash (<c>/metrics/</c>), so the guard must too.</summary>
    internal static bool IsMetricsPath(PathString path, string metricsPath)
    {
        var value = path.HasValue ? path.Value! : "";
        if (value.Length > 1)
            value = value.TrimEnd('/');
        return value.Equals(metricsPath, StringComparison.OrdinalIgnoreCase);
    }

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
