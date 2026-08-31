using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Proxy;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Meters monthly HTTP egress from proxy access logs and enforces quota at the edge.</summary>
public sealed class WebSpaceBandwidthMeter
{
    private readonly AppConfig _config;
    private readonly WebSpaceStore _spaces;
    private readonly ReverseProxyManager _proxy;
    private readonly AppLogger? _logger;
    private readonly object _gate = new();

    public WebSpaceBandwidthMeter(
        AppConfig config,
        WebSpaceStore spaces,
        ReverseProxyManager proxy,
        AppLogger? logger = null)
    {
        _config = config;
        _spaces = spaces;
        _proxy = proxy;
        _logger = logger;
    }

    public void SyncAll()
    {
        foreach (var space in _spaces.List())
        {
            try
            {
                Sync(space.Uuid);
            }
            catch (Exception ex)
            {
                _logger?.Debug(LoggerTypes.Proxy, $"bandwidth meter {space.Uuid}: {ex.Message}");
            }
        }
    }

    public void Sync(Guid uuid)
    {
        lock (_gate)
        {
            var space = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
            var periodStart = CurrentPeriodStart();
            if (!DateOnly.TryParse(space.BandwidthPeriodStart, out var storedStart) || storedStart != periodStart)
            {
                space.BandwidthPeriodStart = periodStart.ToString("yyyy-MM-dd");
                space.BandwidthUsedBytes = 0;
            }

            var used = ComputePeriodBytes(_config.System.RootDirectory, space, periodStart);
            var wasOver = space.IsBandwidthOverQuota();
            space.BandwidthUsedBytes = used;
            space.UpdatedAt = DateTimeOffset.UtcNow;
            _spaces.PersistPublic(space);

            var isOver = space.IsBandwidthOverQuota();
            if (wasOver != isOver)
            {
                _logger?.Info(LoggerTypes.Proxy,
                    $"WebSpace {uuid} bandwidth quota {(isOver ? "exceeded" : "restored")} ({used}/{space.BandwidthLimitBytes} bytes)");
                _proxy.Rebuild(_spaces.List());
            }
        }
    }

    public static DateOnly CurrentPeriodStart()
    {
        var now = DateTime.UtcNow;
        return new DateOnly(now.Year, now.Month, 1);
    }

    public static long ComputePeriodBytes(string rootDirectory, WebSpace space, DateOnly periodStart)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        long total = 0;
        var domains = space.Domains
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var domain in domains)
        {
            for (var day = periodStart; day <= today; day = day.AddDays(1))
            {
                if (day == today)
                {
                    var accessPath = ProxyAccessLogs.AccessLogPath(rootDirectory, space.Uuid, domain);
                    if (!File.Exists(accessPath))
                        continue;

                    try
                    {
                        total += ProxyAccessLogs.SumLineBytes(File.ReadAllLines(accessPath));
                    }
                    catch
                    {
                        // ignore unreadable live log
                    }

                    continue;
                }

                total += ProxyAccessLogs.LoadSummaryBytes(
                    ProxyAccessLogs.SummaryPath(rootDirectory, space.Uuid, domain, day));
            }
        }

        return total;
    }
}
