using System.Runtime.InteropServices;

namespace FeatherQuilld.Utils.SystemInfo;

/// <summary>Live host CPU / memory / disk / load samples (Linux-first, Wings-compatible).</summary>
public sealed class HostMetricsSampler
{
    private readonly object _gate = new();
    private CpuSample? _previousCpu;
    private DateTimeOffset _previousCpuAt = DateTimeOffset.MinValue;

    public HostSnapshot Capture(string? primaryDiskPath = null)
    {
        var memory = ReadMemory();
        var cpu = ReadCpu();
        var load = ReadLoad();
        var disk = ReadDisk(primaryDiskPath);

        return new HostSnapshot(
            Architecture: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Os: RuntimeInformation.OSDescription,
            OsType: OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "unknown",
            KernelVersion: ReadKernelVersion(),
            CpuCount: Environment.ProcessorCount,
            CpuModel: ReadCpuModel(),
            CpuPercent: cpu,
            MemoryTotalBytes: memory.Total,
            MemoryUsedBytes: memory.Used,
            MemoryFreeBytes: memory.Free,
            SwapTotalBytes: memory.SwapTotal,
            SwapUsedBytes: memory.SwapUsed,
            DiskTotalBytes: disk.Total,
            DiskUsedBytes: disk.Used,
            DiskPath: disk.Path,
            Load1: load.One,
            Load5: load.Five,
            Load15: load.Fifteen,
            SampledAt: DateTimeOffset.UtcNow);
    }

    private double ReadCpu()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/stat"))
            return 0;

        var sample = ReadCpuSample();
        if (sample is null)
            return 0;

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            double percent = 0;
            if (_previousCpu is { } prev && (now - _previousCpuAt).TotalMilliseconds >= 100)
            {
                var idleDelta = sample.Idle - prev.Idle;
                var totalDelta = sample.Total - prev.Total;
                if (totalDelta > 0)
                    percent = Math.Clamp(100.0 * (1.0 - idleDelta / totalDelta), 0, 100);
            }

            _previousCpu = sample;
            _previousCpuAt = now;
            return Math.Round(percent, 2);
        }
    }

    private static CpuSample? ReadCpuSample()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
                return null;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // cpu user nice system idle iowait irq softirq steal
            if (parts.Length < 5)
                return null;

            ulong user = ulong.Parse(parts[1]);
            ulong nice = ulong.Parse(parts[2]);
            ulong system = ulong.Parse(parts[3]);
            ulong idle = ulong.Parse(parts[4]);
            ulong iowait = parts.Length > 5 ? ulong.Parse(parts[5]) : 0;
            ulong irq = parts.Length > 6 ? ulong.Parse(parts[6]) : 0;
            ulong softirq = parts.Length > 7 ? ulong.Parse(parts[7]) : 0;
            ulong steal = parts.Length > 8 ? ulong.Parse(parts[8]) : 0;

            var idleAll = idle + iowait;
            var total = user + nice + system + idleAll + irq + softirq + steal;
            return new CpuSample(idleAll, total);
        }
        catch
        {
            return null;
        }
    }

    private static (ulong Total, ulong Used, ulong Free, ulong SwapTotal, ulong SwapUsed) ReadMemory()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/meminfo"))
        {
            var gc = GC.GetGCMemoryInfo();
            var total = (ulong)Math.Max(gc.TotalAvailableMemoryBytes, 0);
            var used = (ulong)Math.Max(GC.GetTotalMemory(false), 0);
            return (total, used, total > used ? total - used : 0, 0, 0);
        }

        try
        {
            ulong memTotal = 0, memAvailable = 0, memFree = 0, buffers = 0, cached = 0;
            ulong swapTotal = 0, swapFree = 0;

            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    memTotal = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    memAvailable = ParseKb(line);
                else if (line.StartsWith("MemFree:", StringComparison.Ordinal))
                    memFree = ParseKb(line);
                else if (line.StartsWith("Buffers:", StringComparison.Ordinal))
                    buffers = ParseKb(line);
                else if (line.StartsWith("Cached:", StringComparison.Ordinal))
                    cached = ParseKb(line);
                else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
                    swapTotal = ParseKb(line);
                else if (line.StartsWith("SwapFree:", StringComparison.Ordinal))
                    swapFree = ParseKb(line);
            }

            var free = memAvailable > 0 ? memAvailable : memFree + buffers + cached;
            var used = memTotal > free ? memTotal - free : 0;
            var swapUsed = swapTotal > swapFree ? swapTotal - swapFree : 0;
            return (memTotal, used, free, swapTotal, swapUsed);
        }
        catch
        {
            return (0, 0, 0, 0, 0);
        }
    }

    private static (ulong Total, ulong Used, string Path) ReadDisk(string? path)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(path) ? "/" : path;
            var root = Path.GetPathRoot(Path.GetFullPath(target)) ?? "/";
            var info = new DriveInfo(root);
            if (!info.IsReady)
                return (0, 0, root);

            var total = (ulong)Math.Max(info.TotalSize, 0);
            var free = (ulong)Math.Max(info.AvailableFreeSpace, 0);
            var used = total > free ? total - free : 0;
            return (total, used, info.Name);
        }
        catch
        {
            return (0, 0, path ?? "/");
        }
    }

    private static (double One, double Five, double Fifteen) ReadLoad()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/loadavg"))
            return (0, 0, 0);

        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return (0, 0, 0);
            return (
                double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static string ReadKernelVersion()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/version"))
        {
            try
            {
                var text = File.ReadAllText("/proc/version");
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    return parts[2];
            }
            catch
            {
                // fall through
            }
        }

        return Environment.OSVersion.VersionString;
    }

    private static string ReadCpuModel()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/cpuinfo"))
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";

        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0)
                        return line[(idx + 1)..].Trim();
                }
            }
        }
        catch
        {
            // ignore
        }

        return "unknown";
    }

    private static ulong ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ulong.TryParse(parts[1], out var kb))
            return 0;
        return kb * 1024UL;
    }

    private sealed record CpuSample(ulong Idle, ulong Total);
}

public sealed record HostSnapshot(
    string Architecture,
    string Os,
    string OsType,
    string KernelVersion,
    int CpuCount,
    string CpuModel,
    double CpuPercent,
    ulong MemoryTotalBytes,
    ulong MemoryUsedBytes,
    ulong MemoryFreeBytes,
    ulong SwapTotalBytes,
    ulong SwapUsedBytes,
    ulong DiskTotalBytes,
    ulong DiskUsedBytes,
    string DiskPath,
    double Load1,
    double Load5,
    double Load15,
    DateTimeOffset SampledAt);
