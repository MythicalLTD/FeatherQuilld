using System.Globalization;

namespace FeatherQuilld.Utils.Sftp;

/// <summary>
/// Best-effort lookup of which local process is listening on a TCP port (Linux /proc only).
/// Used to turn "address already in use" into an actionable message.
/// </summary>
internal static class TcpPortOwner
{
    private const string ListenState = "0A";

    public static string Describe(int port)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return "";

            var inodes = FindListeningInodes(port);
            if (inodes.Count == 0)
                return "";

            foreach (var pidDir in Directory.EnumerateDirectories("/proc"))
            {
                var pidName = Path.GetFileName(pidDir);
                if (!int.TryParse(pidName, out var pid))
                    continue;

                if (!OwnsAnySocket(pidDir, inodes))
                    continue;

                var comm = TryRead(Path.Combine(pidDir, "comm"))?.Trim();
                var self = pid == Environment.ProcessId ? ", this FeatherQuilld process" : "";
                return $"held by pid {pid} ({(string.IsNullOrEmpty(comm) ? "unknown" : comm)}{self})";
            }

            return "held by a process this user cannot inspect";
        }
        catch
        {
            return "";
        }
    }

    private static HashSet<string> FindListeningInodes(int port)
    {
        var portHex = port.ToString("X4", CultureInfo.InvariantCulture);
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            string[] lines;
            try { lines = File.ReadAllLines(table); }
            catch { continue; }

            // Columns: sl local_address rem_address st tx:rx tr:tm retrnsmt uid timeout inode ...
            foreach (var line in lines.Skip(1))
            {
                var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 10 || cols[3] != ListenState)
                    continue;

                var local = cols[1];
                var colon = local.LastIndexOf(':');
                if (colon < 0 || !string.Equals(local[(colon + 1)..], portHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (cols[9] != "0")
                    result.Add(cols[9]);
            }
        }

        return result;
    }

    private static bool OwnsAnySocket(string pidDir, HashSet<string> inodes)
    {
        IEnumerable<string> fds;
        try { fds = Directory.EnumerateFileSystemEntries(Path.Combine(pidDir, "fd")); }
        catch { return false; }

        try
        {
            foreach (var fd in fds)
            {
                string? target;
                try { target = new FileInfo(fd).LinkTarget; }
                catch { continue; }

                // Format: socket:[12345]
                if (target is { Length: > 9 } && target.StartsWith("socket:[", StringComparison.Ordinal)
                    && inodes.Contains(target[8..^1]))
                {
                    return true;
                }
            }
        }
        catch { /* process exited mid-scan */ }

        return false;
    }

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }
}
