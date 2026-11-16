using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Observability;

/// <summary>Wires health probes and the Prometheus endpoint into the daemon host.</summary>
public static class ObservabilitySetup
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    public static void AddFeatherQuilldObservability(this IServiceCollection services, AppConfig config)
    {
        var timeout = TimeSpan.FromSeconds(5);
        services.AddHealthChecks()
            .AddCheck("process", () => HealthCheckResult.Healthy("Process is up."), tags: [LiveTag])
            .AddCheck<DaemonStateHealthCheck>("daemon", tags: [ReadyTag], timeout: timeout)
            .AddCheck<ReverseProxyHealthCheck>("reverse_proxy", tags: [ReadyTag], timeout: timeout)
            .AddCheck<DockerHealthCheck>("docker", tags: [ReadyTag], timeout: timeout)
            .AddCheck<StorageHealthCheck>("storage", tags: [ReadyTag], timeout: timeout)
            .AddCheck<DiskSpaceHealthCheck>("disk_space", tags: [ReadyTag], timeout: timeout)
            .AddCheck<DiskLimiterHealthCheck>("disk_limiter", tags: [ReadyTag], timeout: timeout);
    }

    /// <summary>Extra listen URL for the dedicated metrics listener, or null.</summary>
    public static string? DedicatedListenUrl(AppConfig config) =>
        ObservabilityAccess.HasDedicatedPort(config.Api)
            ? $"http://{(string.IsNullOrWhiteSpace(config.Api.Metrics.Host) ? "127.0.0.1" : config.Api.Metrics.Host.Trim())}:{config.Api.Metrics.Port}"
            : null;

    /// <summary>Access guard; must run before authentication.</summary>
    public static void UseFeatherQuilldObservabilityGuard(this WebApplication app) =>
        app.UseMiddleware<ObservabilityAccessMiddleware>();

    public static void MapFeatherQuilldObservability(this WebApplication app, AppConfig config)
    {
        if (config.Api.Health.Enabled)
        {
            var codes = new Dictionary<HealthStatus, int>
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            };
            var writer = HealthResponseWriter.For(config);

            app.MapHealthChecks(ObservabilityAccess.LivePath, new HealthCheckOptions
            {
                Predicate = c => c.Tags.Contains(LiveTag),
                ResultStatusCodes = codes,
                ResponseWriter = writer,
            }).AllowAnonymous();

            app.MapHealthChecks(ObservabilityAccess.ReadyPath, new HealthCheckOptions
            {
                Predicate = c => c.Tags.Contains(ReadyTag),
                ResultStatusCodes = codes,
                ResponseWriter = writer,
            }).AllowAnonymous();
        }

        if (config.Api.Metrics.Enabled)
        {
            QuilldMetrics.Register(app.Services, config);
            // Token / allowlist enforcement lives in ObservabilityAccessMiddleware (scrape tokens are not node logins).
            app.MapMetrics(ObservabilityAccess.NormalizedMetricsPath(config.Api.Metrics)).AllowAnonymous();
        }
    }
}
