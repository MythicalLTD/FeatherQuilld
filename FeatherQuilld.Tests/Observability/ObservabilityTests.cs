using System.Net;
using FeatherQuilld.Utils.Observability;
using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Observability;

public class ObservabilityAccessTests
{
    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("10.0.1.5", false)]
    [InlineData("192.168.1.10", true)]
    [InlineData("::ffff:10.0.0.9", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("2001:db9::1", false)]
    public void Allowlist_MatchesIpsAndCidrs(string ip, bool expected)
    {
        var allowed = new List<string> { "10.0.0.0/24", "192.168.1.10", "2001:db8::/32", "not-an-ip" };
        Assert.Equal(expected, ObservabilityAccess.IsAllowedIp(IPAddress.Parse(ip), allowed));
    }

    [Fact]
    public void EmptyAllowlist_AllowsAnyone() =>
        Assert.True(ObservabilityAccess.IsAllowedIp(IPAddress.Parse("8.8.8.8"), []));

    [Fact]
    public void Token_AcceptsScrapeAndNodeToken_RejectsOthers()
    {
        var config = new AppConfig();
        config.Api.Metrics.Token = "scrape-secret";

        Assert.True(ObservabilityAccess.HasValidToken(Request("Bearer scrape-secret"), config));
        Assert.True(ObservabilityAccess.HasValidToken(Request($"Bearer {config.BearerToken}"), config));
        Assert.False(ObservabilityAccess.HasValidToken(Request("Bearer nope"), config));
        Assert.False(ObservabilityAccess.HasValidToken(Request(null), config));
    }

    [Fact]
    public void EmptyScrapeToken_IsNeverAccepted()
    {
        var config = new AppConfig();
        config.Api.Metrics.Token = "";
        Assert.False(ObservabilityAccess.HasValidToken(Request("Bearer "), config));
    }

    [Theory]
    [InlineData("metrics", "/metrics")]
    [InlineData("/prom/", "/prom")]
    [InlineData("", "/metrics")]
    public void MetricsPath_IsNormalized(string configured, string expected)
    {
        var config = new AppConfig();
        config.Api.Metrics.Path = configured;
        Assert.Equal(expected, ObservabilityAccess.NormalizedMetricsPath(config.Api.Metrics));
    }

    private static HttpRequest Request(string? authorization)
    {
        var ctx = new DefaultHttpContext();
        if (authorization is not null)
            ctx.Request.Headers.Authorization = authorization;
        return ctx.Request;
    }
}

public class ObservabilityMiddlewareTests
{
    private static async Task<int> Run(AppConfig config, string path, string? auth = null, string ip = "127.0.0.1", int localPort = 8989)
    {
        var reached = false;
        var mw = new ObservabilityAccessMiddleware(_ => { reached = true; return Task.CompletedTask; }, config);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Connection.LocalPort = localPort;
        ctx.Response.Body = new MemoryStream();
        if (auth is not null)
            ctx.Request.Headers.Authorization = auth;
        await mw.InvokeAsync(ctx);
        return reached ? 200 : ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Metrics_RequiresToken()
    {
        var config = new AppConfig();
        config.Api.Metrics.Token = "t0k";
        Assert.Equal(401, await Run(config, "/metrics"));
        Assert.Equal(401, await Run(config, "/metrics", "Bearer wrong"));
        Assert.Equal(200, await Run(config, "/metrics", "Bearer t0k"));
    }

    [Theory]
    [InlineData("/metrics/")]
    [InlineData("/METRICS")]
    [InlineData("/metrics//")]
    public async Task Metrics_PathVariantsAreGuardedToo(string path)
    {
        var config = new AppConfig();
        config.Api.Metrics.Token = "t0k";
        Assert.Equal(401, await Run(config, path));
    }

    [Fact]
    public async Task Metrics_AllowlistIsCheckedEvenWithToken()
    {
        var config = new AppConfig();
        config.Api.Metrics.Token = "t0k";
        config.Api.Metrics.AllowedIps = ["10.0.0.0/8"];
        Assert.Equal(403, await Run(config, "/metrics", "Bearer t0k", ip: "203.0.113.4"));
        Assert.Equal(200, await Run(config, "/metrics", "Bearer t0k", ip: "10.1.2.3"));
    }

    [Fact]
    public async Task Metrics_AnonymousWhenConfigured()
    {
        var config = new AppConfig();
        config.Api.Metrics.AllowAnonymous = true;
        Assert.Equal(200, await Run(config, "/metrics"));
    }

    [Fact]
    public async Task DedicatedPort_ServesOnlyMetricsAndHealth()
    {
        var config = new AppConfig();
        config.Api.Port = 8989;
        config.Api.Metrics.Port = 9464;
        config.Api.Metrics.Token = "t0k";

        Assert.Equal(404, await Run(config, "/metrics", "Bearer t0k", localPort: 8989));
        Assert.Equal(200, await Run(config, "/metrics", "Bearer t0k", localPort: 9464));
        Assert.Equal(200, await Run(config, "/health/ready", localPort: 9464));
        Assert.Equal(404, await Run(config, "/api/system", "Bearer t0k", localPort: 9464));
        Assert.Equal(200, await Run(config, "/api/system", localPort: 8989));
    }

    [Fact]
    public async Task Disabled_MetricsPathIsNotGuarded()
    {
        var config = new AppConfig();
        config.Api.Metrics.Enabled = false;
        // Falls through to the normal pipeline (deny-by-default auth / 404).
        Assert.Equal(200, await Run(config, "/metrics"));
    }
}

[Collection("ProxyCommandTracker")]
public class HealthTests
{
    [Fact]
    public void Writer_HidesDetailsWithoutAuth()
    {
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["disk_space"] = new(HealthStatus.Degraded, "5% free", TimeSpan.FromMilliseconds(1), null,
                new Dictionary<string, object> { ["path"] = "/secret" }),
            ["docker"] = new(HealthStatus.Healthy, "ok", TimeSpan.Zero, null, null),
        }, TimeSpan.FromMilliseconds(2));

        var anon = HealthResponseWriter.Build(report, details: false);
        Assert.Equal("degraded", anon["status"]);
        var anonCheck = Assert.IsType<Dictionary<string, object?>>(((Dictionary<string, object?>)anon["checks"]!)["disk_space"]);
        Assert.Single(anonCheck);
        Assert.False(anon.ContainsKey("version"));

        var full = HealthResponseWriter.Build(report, details: true);
        var fullCheck = (Dictionary<string, object?>)((Dictionary<string, object?>)full["checks"]!)["disk_space"]!;
        Assert.Equal("5% free", fullCheck["description"]);
        Assert.NotNull(fullCheck["data"]);
    }

    [Theory]
    [InlineData(50, HealthStatus.Healthy)]
    [InlineData(5, HealthStatus.Degraded)]
    [InlineData(1, HealthStatus.Unhealthy)]
    public async Task DiskSpace_Thresholds(long freePercent, HealthStatus expected)
    {
        var config = new AppConfig();
        var check = new DiskSpaceHealthCheck(config) { Read = p => new DataDisk.Usage(p, 100, freePercent) };
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task ReverseProxy_UnhealthyWhenNotRunning_DegradedAfterFailedReload()
    {
        ProxyCommandTracker.Reset();
        var config = new AppConfig();
        config.System.Proxy.Enabled = true;
        config.System.Proxy.Provider = "traefik";

        var missing = new ReverseProxyHealthCheck(config) { BinaryOnPath = _ => false, RunningProcesses = _ => 0 };
        Assert.Equal(HealthStatus.Unhealthy, (await missing.CheckHealthAsync(new HealthCheckContext())).Status);

        var stopped = new ReverseProxyHealthCheck(config) { BinaryOnPath = _ => true, RunningProcesses = _ => 0 };
        Assert.Equal(HealthStatus.Unhealthy, (await stopped.CheckHealthAsync(new HealthCheckContext())).Status);

        var running = new ReverseProxyHealthCheck(config) { BinaryOnPath = _ => true, RunningProcesses = _ => 1 };
        Assert.Equal(HealthStatus.Healthy, (await running.CheckHealthAsync(new HealthCheckContext())).Status);

        ProxyCommandTracker.Record("traefik", ok: false);
        Assert.Equal(HealthStatus.Degraded, (await running.CheckHealthAsync(new HealthCheckContext())).Status);
        ProxyCommandTracker.Reset();
    }

    [Fact]
    public async Task ReverseProxy_HealthyWhenDisabled()
    {
        var config = new AppConfig();
        config.System.Proxy.Enabled = false;
        var check = new ReverseProxyHealthCheck(config) { BinaryOnPath = _ => false, RunningProcesses = _ => 0 };
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task DaemonState_MaintenanceIsUnhealthy_PanelDownIsDegraded()
    {
        var state = new FeatherQuilld.Utils.Services.DaemonState();
        var check = new DaemonStateHealthCheck(state);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        state.PanelReachable = false;
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        state.MaintenanceMode = true;
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }
}

[Collection("ProxyCommandTracker")]
public class QuilldMetricsTests
{
    private static async Task<string> Scrape()
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task Webspaces_AreExported_AndRemovedWhenGone()
    {
        var a = new WebSpace { Uuid = Guid.NewGuid(), Name = "alpha", Runtime = "static", State = WebSpaceState.Running, Domains = ["a.test"], WafEnabled = true };
        var b = new WebSpace { Uuid = Guid.NewGuid(), Name = "beta", Runtime = "php", State = WebSpaceState.Stopped, Suspended = true };

        QuilldMetrics.RefreshWebspaces([a, b], perWebspace: true);
        var text = await Scrape();
        Assert.Contains($"featherquilld_webspace_up{{webspace=\"{a.Uuid}\"}} 1", text);
        Assert.Contains($"featherquilld_webspace_up{{webspace=\"{b.Uuid}\"}} 0", text);
        Assert.Contains("featherquilld_webspaces 2", text);
        Assert.Contains("featherquilld_webspaces_by_state{state=\"running\"} 1", text);

        QuilldMetrics.RefreshWebspaces([a], perWebspace: true);
        text = await Scrape();
        Assert.DoesNotContain(b.Uuid.ToString(), text);
        Assert.Contains("featherquilld_webspaces_by_state{state=\"stopped\"} 0", text);

        QuilldMetrics.RefreshWebspaces([], perWebspace: true);
    }

    [Fact]
    public async Task ProxyCommand_CountsByProviderActionResult()
    {
        QuilldMetrics.RecordProxyCommand("caddy start", ok: false, TimeSpan.FromMilliseconds(30));
        var text = await Scrape();
        Assert.Contains("featherquilld_proxy_reloads_total{provider=\"caddy\",action=\"start\",result=\"failure\"}", text);
        ProxyCommandTracker.Reset();
    }
}
