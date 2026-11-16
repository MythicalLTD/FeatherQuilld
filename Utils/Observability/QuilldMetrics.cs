using System.Diagnostics;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.SystemInfo;
using FeatherQuilld.Utils.WebSpaces;
using Prometheus;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Observability;

/// <summary>
/// FeatherQuilld domain metrics (prefix <c>featherquilld_</c>). Counters are bumped where the work happens;
/// gauges are refreshed right before every scrape from the live daemon state.
/// </summary>
public static class QuilldMetrics
{
    private const string Prefix = "featherquilld_";

    // ---- counters / histograms (event driven) ---------------------------------------------------------------------

    public static readonly Counter ProxyReloads = Metrics.CreateCounter(
        Prefix + "proxy_reloads_total",
        "Reverse proxy reload / start commands run by the daemon, by provider, action and result.",
        "provider", "action", "result");

    public static readonly Histogram ProxyReloadDuration = Metrics.CreateHistogram(
        Prefix + "proxy_reload_duration_seconds",
        "Duration of reverse proxy reload / start commands.",
        new HistogramConfiguration
        {
            LabelNames = ["provider", "action"],
            Buckets = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15],
        });

    public static readonly Gauge ProxyLastReloadSuccess = Metrics.CreateGauge(
        Prefix + "proxy_last_reload_success",
        "1 when the most recent reverse proxy reload succeeded, 0 when it failed.",
        "provider");

    public static readonly Gauge ProxyLastReloadTimestamp = Metrics.CreateGauge(
        Prefix + "proxy_last_reload_timestamp_seconds",
        "Unix time of the most recent reverse proxy reload attempt.",
        "provider");

    public static readonly Counter ProxyRebuilds = Metrics.CreateCounter(
        Prefix + "proxy_config_rebuilds_total",
        "Reverse proxy config regenerations (every WebSpace / domain / firewall change), by result.",
        "provider", "result");

    public static readonly Counter WafEvents = Metrics.CreateCounter(
        Prefix + "waf_events_total",
        "Layer-7 firewall events recorded since the daemon started, by WebSpace, category and action (blocked / detected).",
        "webspace", "category", "action");

    public static readonly Counter SftpAuthFailures = Metrics.CreateCounter(
        Prefix + "sftp_auth_failures_total",
        "Failed SFTP logins.");

    public static readonly Counter SftpLockouts = Metrics.CreateCounter(
        Prefix + "sftp_lockouts_total",
        "SFTP usernames locked out by the brute-force guard.");

    // ---- gauges (refreshed per scrape) ------------------------------------------------------------------------------

    private static readonly Gauge BuildInfo = Metrics.CreateGauge(
        Prefix + "build_info", "Always 1; labels carry the daemon version and node uuid.", "version", "node");
    private static readonly Gauge Uptime = Metrics.CreateGauge(
        Prefix + "uptime_seconds", "Seconds since the daemon started.");
    private static readonly Gauge Maintenance = Metrics.CreateGauge(
        Prefix + "maintenance_mode", "1 when the panel has put the node in maintenance mode.");
    private static readonly Gauge PanelReachable = Metrics.CreateGauge(
        Prefix + "panel_reachable", "1 when the last panel sync succeeded.");

    private static readonly Gauge Webspaces = Metrics.CreateGauge(
        Prefix + "webspaces", "WebSpaces on this node.");
    private static readonly Gauge WebspacesByState = Metrics.CreateGauge(
        Prefix + "webspaces_by_state", "WebSpaces by runtime state.", "state");
    private static readonly Gauge WebspacesByStatus = Metrics.CreateGauge(
        Prefix + "webspaces_by_status", "WebSpaces by install status.", "status");
    private static readonly Gauge WebspacesSuspended = Metrics.CreateGauge(
        Prefix + "webspaces_suspended", "Suspended WebSpaces.");
    private static readonly Gauge WebspacesWaf = Metrics.CreateGauge(
        Prefix + "webspaces_waf_enabled", "WebSpaces with the layer-7 firewall turned on.");
    private static readonly Gauge Domains = Metrics.CreateGauge(
        Prefix + "domains", "Domains routed by the reverse proxy across all WebSpaces.");

    private static readonly Gauge WsInfo = Metrics.CreateGauge(
        Prefix + "webspace_info", "Always 1; labels describe the WebSpace.", "webspace", "name", "runtime", "state", "status");
    private static readonly Gauge WsUp = Metrics.CreateGauge(
        Prefix + "webspace_up", "1 when the WebSpace is running.", "webspace");
    private static readonly Gauge WsSuspended = Metrics.CreateGauge(
        Prefix + "webspace_suspended", "1 when the WebSpace is suspended.", "webspace");
    private static readonly Gauge WsWaf = Metrics.CreateGauge(
        Prefix + "webspace_waf_enabled", "1 when the WebSpace firewall is on.", "webspace");
    private static readonly Gauge WsDomains = Metrics.CreateGauge(
        Prefix + "webspace_domains", "Domains attached to the WebSpace.", "webspace");
    private static readonly Gauge WsDiskLimit = Metrics.CreateGauge(
        Prefix + "webspace_disk_limit_bytes", "Disk limit (0 = unlimited).", "webspace");
    private static readonly Gauge WsCpuLimit = Metrics.CreateGauge(
        Prefix + "webspace_cpu_limit", "CPU limit in percent of one core (0 = unlimited).", "webspace");
    private static readonly Gauge WsMemoryLimit = Metrics.CreateGauge(
        Prefix + "webspace_memory_limit_bytes", "Memory limit (0 = unlimited).", "webspace");
    private static readonly Gauge WsBandwidthUsed = Metrics.CreateGauge(
        Prefix + "webspace_bandwidth_used_bytes", "Bandwidth used in the current period.", "webspace");
    private static readonly Gauge WsBandwidthLimit = Metrics.CreateGauge(
        Prefix + "webspace_bandwidth_limit_bytes", "Bandwidth limit per period (0 = unlimited).", "webspace");

    private static readonly Gauge ProxyEnabled = Metrics.CreateGauge(
        Prefix + "proxy_enabled", "1 when the daemon manages a reverse proxy.", "provider");
    private static readonly Gauge ProxyRunning = Metrics.CreateGauge(
        Prefix + "proxy_running", "1 when a reverse proxy process is running.", "provider");

    private static readonly Gauge HostCpu = Metrics.CreateGauge(
        Prefix + "host_cpu_percent", "Host CPU usage in percent.");
    private static readonly Gauge HostCpuCount = Metrics.CreateGauge(
        Prefix + "host_cpu_threads", "Host CPU threads.");
    private static readonly Gauge HostMemTotal = Metrics.CreateGauge(
        Prefix + "host_memory_total_bytes", "Host memory.");
    private static readonly Gauge HostMemUsed = Metrics.CreateGauge(
        Prefix + "host_memory_used_bytes", "Host memory in use.");
    private static readonly Gauge HostSwapTotal = Metrics.CreateGauge(
        Prefix + "host_swap_total_bytes", "Host swap.");
    private static readonly Gauge HostSwapUsed = Metrics.CreateGauge(
        Prefix + "host_swap_used_bytes", "Host swap in use.");
    private static readonly Gauge HostLoad = Metrics.CreateGauge(
        Prefix + "host_load", "Host load average.", "window");
    private static readonly Gauge DataDiskTotal = Metrics.CreateGauge(
        Prefix + "data_disk_total_bytes", "Size of the disk holding WebSpace data.", "path");
    private static readonly Gauge DataDiskFree = Metrics.CreateGauge(
        Prefix + "data_disk_free_bytes", "Free space on the disk holding WebSpace data.", "path");

    private static readonly object Gate = new();
    private static readonly HashSet<string> SeenWebspaces = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> InfoLabels = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SeenStates = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SeenStatuses = new(StringComparer.Ordinal);
    private static int _registered;

    /// <summary>Registers the before-scrape refresh once per process.</summary>
    public static void Register(IServiceProvider services, AppConfig config)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        Metrics.DefaultRegistry.AddBeforeCollectCallback(() =>
        {
            try
            {
                Refresh(services, config);
            }
            catch
            {
                // A broken refresh must never fail the scrape; the event counters are still exported.
            }
        });
    }

    internal static void Refresh(IServiceProvider services, AppConfig config)
    {
        var state = services.GetService<DaemonState>();
        var spaces = services.GetService<WebSpaceStore>()?.List() ?? [];
        var sampler = services.GetService<HostMetricsSampler>();

        BuildInfo.WithLabels(StartupBanner.Version, config.Uuid.ToString()).Set(1);
        if (state is not null)
        {
            Uptime.Set(state.UptimeSeconds);
            Maintenance.Set(state.MaintenanceMode ? 1 : 0);
            PanelReachable.Set(state.PanelReachable ? 1 : 0);
        }

        RefreshWebspaces(spaces, config.Api.Metrics.PerWebspace);

        var provider = ProxyProbe.NormalizeProvider(config.System.Proxy.Provider);
        ProxyEnabled.WithLabels(provider).Set(config.System.Proxy.Enabled ? 1 : 0);
        ProxyRunning.WithLabels(provider).Set(config.System.Proxy.Enabled && CountProcesses(provider) > 0 ? 1 : 0);

        if (sampler is not null)
        {
            var snap = sampler.Capture(config.System.Data);
            // The shared sampler reports 0 when sampled again within 100 ms (e.g. right after /api/system/health);
            // keep the previous value instead of exporting a false idle reading.
            if (snap.CpuPercent > 0)
                HostCpu.Set(snap.CpuPercent);
            HostCpuCount.Set(snap.CpuCount);
            HostMemTotal.Set(snap.MemoryTotalBytes);
            HostMemUsed.Set(snap.MemoryUsedBytes);
            HostSwapTotal.Set(snap.SwapTotalBytes);
            HostSwapUsed.Set(snap.SwapUsedBytes);
            HostLoad.WithLabels("1m").Set(snap.Load1);
            HostLoad.WithLabels("5m").Set(snap.Load5);
            HostLoad.WithLabels("15m").Set(snap.Load15);
        }

        if (DataDisk.Read(config.System.Data) is { } disk)
        {
            DataDiskTotal.WithLabels(disk.Path).Set(disk.Total);
            DataDiskFree.WithLabels(disk.Path).Set(disk.Free);
        }
    }

    internal static void RefreshWebspaces(IReadOnlyList<WebSpace> spaces, bool perWebspace)
    {
        lock (Gate)
        {
            Webspaces.Set(spaces.Count);
            WebspacesSuspended.Set(spaces.Count(s => s.Suspended));
            WebspacesWaf.Set(spaces.Count(s => s.WafEnabled));
            Domains.Set(spaces.Sum(s => s.Domains.Count));

            SetGrouped(WebspacesByState, SeenStates, spaces.GroupBy(s => Norm(s.State)),
                [WebSpaceState.Running, WebSpaceState.Stopped, WebSpaceState.Starting, WebSpaceState.Stopping]);
            SetGrouped(WebspacesByStatus, SeenStatuses, spaces.GroupBy(s => Norm(s.Status)),
                [WebSpaceStatus.Installed, WebSpaceStatus.Installing, WebSpaceStatus.Reinstalling, WebSpaceStatus.Failed]);

            var current = new HashSet<string>(StringComparer.Ordinal);
            if (perWebspace)
            {
                foreach (var space in spaces)
                {
                    var id = space.Uuid.ToString();
                    current.Add(id);

                    string[] info = [id, space.Name ?? "", space.Runtime ?? "", Norm(space.State), Norm(space.Status)];
                    if (InfoLabels.TryGetValue(id, out var old) && !old.SequenceEqual(info))
                        WsInfo.RemoveLabelled(old);
                    InfoLabels[id] = info;
                    WsInfo.WithLabels(info).Set(1);

                    WsUp.WithLabels(id).Set(Norm(space.State) == WebSpaceState.Running ? 1 : 0);
                    WsSuspended.WithLabels(id).Set(space.Suspended ? 1 : 0);
                    WsWaf.WithLabels(id).Set(space.WafEnabled ? 1 : 0);
                    WsDomains.WithLabels(id).Set(space.Domains.Count);
                    WsDiskLimit.WithLabels(id).Set(space.DiskLimitBytes);
                    WsCpuLimit.WithLabels(id).Set(space.CpuLimit);
                    WsMemoryLimit.WithLabels(id).Set(space.MemoryLimitMiB * 1024d * 1024d);
                    WsBandwidthUsed.WithLabels(id).Set(space.BandwidthUsedBytes);
                    WsBandwidthLimit.WithLabels(id).Set(space.BandwidthLimitBytes);
                }
            }

            foreach (var gone in SeenWebspaces.Where(id => !current.Contains(id)).ToList())
            {
                if (InfoLabels.Remove(gone, out var old))
                    WsInfo.RemoveLabelled(old);
                foreach (var g in new[] { WsUp, WsSuspended, WsWaf, WsDomains, WsDiskLimit, WsCpuLimit, WsMemoryLimit, WsBandwidthUsed, WsBandwidthLimit })
                    g.RemoveLabelled(gone);
                SeenWebspaces.Remove(gone);
            }

            SeenWebspaces.UnionWith(current);
        }
    }

    private static void SetGrouped(Gauge gauge, HashSet<string> seen, IEnumerable<IGrouping<string, WebSpace>> groups, string[] always)
    {
        var counts = groups.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var key in always)
            counts.TryAdd(key, 0);
        foreach (var key in seen.Where(k => !counts.ContainsKey(k)).ToList())
            counts[key] = 0;
        foreach (var (key, n) in counts)
        {
            gauge.WithLabels(key).Set(n);
            seen.Add(key);
        }
    }

    private static string Norm(string? value) => (value ?? "").Trim().ToLowerInvariant();

    // ---- helpers used by the instrumented code ----------------------------------------------------------------------

    /// <summary>Records one reload / start command. <paramref name="label"/> is the daemon's own label ("caddy", "caddy start", "nginx").</summary>
    public static void RecordProxyCommand(string label, bool ok, TimeSpan elapsed)
    {
        var parts = (label ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var provider = parts.Length > 0 ? ProxyProbe.NormalizeProvider(parts[0]) : "caddy";
        var action = parts.Length > 1 ? parts[1] : "reload";
        ProxyReloads.WithLabels(provider, action, ok ? "success" : "failure").Inc();
        ProxyReloadDuration.WithLabels(provider, action).Observe(elapsed.TotalSeconds);
        ProxyLastReloadSuccess.WithLabels(provider).Set(ok ? 1 : 0);
        ProxyLastReloadTimestamp.WithLabels(provider).SetToCurrentTimeUtc();
        ProxyCommandTracker.Record(provider, ok);
    }

    public static void RecordWafEvents(Guid webspace, IEnumerable<(string Category, string Action)> events)
    {
        var id = webspace.ToString();
        foreach (var group in events.GroupBy(e => (Category: string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category, Action: string.IsNullOrWhiteSpace(e.Action) ? "detected" : e.Action)))
            WafEvents.WithLabels(id, group.Key.Category, group.Key.Action).Inc(group.Count());
    }

    internal static int CountProcesses(string provider)
    {
        try
        {
            var procs = Process.GetProcessesByName(ProxyProbe.BinaryName(provider));
            var n = procs.Length;
            foreach (var p in procs)
                p.Dispose();
            return n;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>Last proxy reload outcome, for the readiness check.</summary>
public static class ProxyCommandTracker
{
    private static readonly object Gate = new();

    private static (string Provider, bool Ok, DateTimeOffset At)? _last;

    public static (string Provider, bool Ok, DateTimeOffset At)? Last
    {
        get { lock (Gate) return _last; }
    }

    public static void Record(string provider, bool ok)
    {
        lock (Gate)
            _last = (provider, ok, DateTimeOffset.UtcNow);
    }

    internal static void Reset()
    {
        lock (Gate)
            _last = null;
    }
}

/// <summary>Free / total space of the filesystem holding a path (walks up to the nearest existing directory).</summary>
public static class DataDisk
{
    public sealed record Usage(string Path, long Total, long Free)
    {
        public double FreePercent => Total <= 0 ? 100 : 100.0 * Free / Total;
    }

    public static Usage? Read(string? path)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(path) ? "/" : Path.GetFullPath(path);
            while (!Directory.Exists(dir))
            {
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    return null;
                dir = parent;
            }

            var drive = new DriveInfo(dir);
            return new Usage(path ?? dir, drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch
        {
            return null;
        }
    }
}
