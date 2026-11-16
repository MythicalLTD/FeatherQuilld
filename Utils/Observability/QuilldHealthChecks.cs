using System.Text.Json;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.WebSpaces;
using FeatherQuilld.Utils.WebSpaces.Disk;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Observability;

/// <summary>Maintenance mode (unhealthy, same rule as <c>/api/system/health</c>) and panel reachability (degraded).</summary>
public sealed class DaemonStateHealthCheck(DaemonState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["maintenance_mode"] = state.MaintenanceMode,
            ["panel_reachable"] = state.PanelReachable,
            ["uptime_seconds"] = state.UptimeSeconds,
        };

        if (state.MaintenanceMode)
            return Task.FromResult(HealthCheckResult.Unhealthy("Node is in maintenance mode.", data: data));

        if (!state.PanelReachable)
        {
            if (!string.IsNullOrEmpty(state.LastPanelError))
                data["last_panel_error"] = state.LastPanelError;
            return Task.FromResult(HealthCheckResult.Degraded("Panel is not reachable; WebSpaces keep serving.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Daemon running, panel reachable.", data));
    }
}

/// <summary>Reverse proxy (Caddy / nginx / Traefik) binary present, process running, last reload not failed.</summary>
public sealed class ReverseProxyHealthCheck(AppConfig config) : IHealthCheck
{
    internal Func<string, bool> BinaryOnPath { get; init; } = ProxyProbe.BinaryOnPath;
    internal Func<string, int> RunningProcesses { get; init; } = QuilldMetrics.CountProcesses;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var proxy = config.System.Proxy;
        var provider = ProxyProbe.NormalizeProvider(proxy.Provider);
        var data = new Dictionary<string, object> { ["provider"] = provider, ["enabled"] = proxy.Enabled };

        if (!proxy.Enabled)
            return Task.FromResult(HealthCheckResult.Healthy("Reverse proxy management is disabled.", data));

        if (!BinaryOnPath(provider))
            return Task.FromResult(HealthCheckResult.Unhealthy($"{provider} binary not found on PATH.", data: data));

        var running = RunningProcesses(provider);
        data["processes"] = running;
        if (running == 0)
            return Task.FromResult(HealthCheckResult.Unhealthy($"{provider} is not running.", data: data));

        if (ProxyCommandTracker.Last is { } last && last.Provider == provider)
        {
            data["last_reload_ok"] = last.Ok;
            data["last_reload_at"] = last.At.ToString("O");
            if (!last.Ok)
                return Task.FromResult(HealthCheckResult.Degraded($"Last {provider} reload failed; it may serve an outdated config.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"{provider} running.", data));
    }
}

/// <summary>Docker daemon reachable. Unhealthy only when a WebSpace on this node needs a container.</summary>
public sealed class DockerHealthCheck(AppConfig config, WebSpaceStore spaces) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var containerSpaces = spaces.List().Count(s => WebSpaceRuntime.NeedsContainer(s.Runtime));
        var data = new Dictionary<string, object> { ["container_webspaces"] = containerSpaces };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = DockerClientFactory.Create(config.Docker);
            await client.System.PingAsync(timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Docker reachable.", data);
        }
        catch (Exception ex)
        {
            data["error"] = ex.Message;
            return containerSpaces > 0
                ? HealthCheckResult.Unhealthy("Docker is not reachable.", data: data)
                : HealthCheckResult.Degraded("Docker is not reachable (no container WebSpaces yet).", data: data);
        }
    }
}

/// <summary>WebSpace data directory exists and is writable.</summary>
public sealed class StorageHealthCheck(AppConfig config) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var dir = config.System.Data;
        var data = new Dictionary<string, object> { ["path"] = dir };
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return Task.FromResult(HealthCheckResult.Unhealthy("Data directory is missing.", data: data));

        var probe = Path.Combine(dir, $".quilld-health-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy("Data directory writable.", data));
        }
        catch (Exception ex)
        {
            try { File.Delete(probe); } catch { /* ignore */ }
            data["error"] = ex.Message;
            return Task.FromResult(HealthCheckResult.Unhealthy("Data directory is not writable.", data: data));
        }
    }
}

/// <summary>Free space on the data disk against the configured thresholds.</summary>
public sealed class DiskSpaceHealthCheck(AppConfig config) : IHealthCheck
{
    internal Func<string, DataDisk.Usage?> Read { get; init; } = DataDisk.Read;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var usage = Read(config.System.Data);
        if (usage is null)
            return Task.FromResult(HealthCheckResult.Degraded("Could not read data disk usage."));

        var health = config.Api.Health;
        var free = Math.Round(usage.FreePercent, 2);
        var data = new Dictionary<string, object>
        {
            ["path"] = usage.Path,
            ["total_bytes"] = usage.Total,
            ["free_bytes"] = usage.Free,
            ["free_percent"] = free,
        };

        if (free < health.DiskCriticalPercent)
            return Task.FromResult(HealthCheckResult.Unhealthy($"Only {free}% free on the data disk.", data: data));
        if (free < health.DiskWarnPercent)
            return Task.FromResult(HealthCheckResult.Degraded($"{free}% free on the data disk.", data: data));
        return Task.FromResult(HealthCheckResult.Healthy($"{free}% free on the data disk.", data));
    }
}

/// <summary>Disk quota enforcement available (fusequota binary present when that limiter is selected).</summary>
public sealed class DiskLimiterHealthCheck(AppConfig config) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var mode = config.System.EffectiveDiskLimiterMode;
        var data = new Dictionary<string, object> { ["mode"] = mode.ToString().ToLowerInvariant() };
        if (mode != DiskLimiterModeKind.FuseQuota)
            return Task.FromResult(HealthCheckResult.Healthy("No FUSE quota limiter in use.", data));

        var available = FuseQuotaLimiter.IsBinaryAvailable(config.System);
        data["fusequota_available"] = available;
        return Task.FromResult(available
            ? HealthCheckResult.Healthy("fusequota available.", data)
            : HealthCheckResult.Degraded("fusequota binary missing; disk limits are not enforced.", data: data));
    }
}

/// <summary>JSON body for <c>/health/live</c> and <c>/health/ready</c>.</summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static Func<HttpContext, HealthReport, Task> For(AppConfig config) => (context, report) =>
    {
        var details = config.Api.Health.PublicDetails || ObservabilityAccess.HasValidToken(context.Request, config);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store, no-cache";
        return context.Response.WriteAsync(JsonSerializer.Serialize(Build(report, details), Json));
    };

    internal static Dictionary<string, object?> Build(HealthReport report, bool details)
    {
        var checks = new Dictionary<string, object?>();
        foreach (var (name, entry) in report.Entries)
        {
            if (!details)
            {
                checks[name] = new Dictionary<string, object?> { ["status"] = Status(entry.Status) };
                continue;
            }

            checks[name] = new Dictionary<string, object?>
            {
                ["status"] = Status(entry.Status),
                ["description"] = entry.Description,
                ["duration_ms"] = Math.Round(entry.Duration.TotalMilliseconds, 2),
                ["data"] = entry.Data.Count == 0 ? null : entry.Data,
                ["error"] = entry.Exception?.Message,
            };
        }

        var body = new Dictionary<string, object?>
        {
            ["status"] = Status(report.Status),
            ["checks"] = checks,
        };

        if (details)
        {
            body["version"] = StartupBanner.Version;
            body["duration_ms"] = Math.Round(report.TotalDuration.TotalMilliseconds, 2);
        }

        return body;
    }

    internal static string Status(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };
}
