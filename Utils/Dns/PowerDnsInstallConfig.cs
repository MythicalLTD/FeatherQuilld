using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FeatherQuilld.Utils.Dns;

/// <summary>Pure helpers for the PowerDNS host package install (config text, port 53 conflicts, schema lookup).</summary>
public static class PowerDnsInstallConfig
{
    public const string DropInDir = "/etc/powerdns/pdns.d";
    public const string DatabasePath = "/var/lib/powerdns/pdns.sqlite3";

    /// <summary>Drop-ins shipped by distro packages that launch a second backend (Debian/Ubuntu ship bind.conf).</summary>
    public static readonly string[] ConflictingDropIns = ["bind.conf"];

    /// <summary>Where Debian/Ubuntu and RPM packages ship the gsqlite3 schema, most specific first.</summary>
    public static readonly string[] SqliteSchemaCandidates =
    [
        "/usr/share/pdns-backend-sqlite3/schema/schema.sqlite3.sql",
        "/usr/share/doc/pdns-backend-sqlite3/schema.sqlite3.sql",
        "/usr/share/doc/pdns/schema.sqlite3.sql",
        "/usr/share/doc/pdns-backend-sqlite/schema.sqlite3.sql",
    ];

    public static string? FindSqliteSchema(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        return SqliteSchemaCandidates.FirstOrDefault(exists);
    }

    public static string BuildConfig(string apiKey, IReadOnlyList<string>? localAddresses)
    {
        var explicitBind = localAddresses is { Count: > 0 };
        var lines = new List<string>
        {
            "api=yes",
            $"api-key={apiKey}",
            "webserver=yes",
            "webserver-address=127.0.0.1",
            "webserver-port=8081",
            "webserver-allow-from=127.0.0.1,::1",
            "launch=gsqlite3",
            $"gsqlite3-database={DatabasePath}",
            $"local-address={(explicitBind ? string.Join(",", localAddresses!) : "0.0.0.0,::")}",
        };
        if (explicitBind)
        {
            // Don't refuse to start when an address (e.g. a DHCP or floating IP) isn't up yet.
            lines.Add("local-address-nonexist-fail=no");
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Returns the loopback IPv4 addresses that already listen on port 53, from the contents of
    /// /proc/net/udp and /proc/net/tcp (e.g. systemd-resolved's stub on 127.0.0.53 / 127.0.0.54).
    /// When any exist, binding the wildcard 0.0.0.0:53 fails, so pdns must bind explicit addresses.
    /// </summary>
    public static IReadOnlyList<IPAddress> FindLoopbackPort53Listeners(params string?[] procNetTables)
    {
        var found = new List<IPAddress>();
        foreach (var table in procNetTables)
        {
            if (string.IsNullOrEmpty(table))
                continue;
            foreach (var raw in table.Split('\n'))
            {
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].EndsWith(':'))
                    continue;
                var local = parts[1].Split(':');
                if (local.Length != 2 || local[0].Length != 8)
                    continue;
                if (!int.TryParse(local[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port) || port != 53)
                    continue;
                if (!uint.TryParse(local[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hostOrder))
                    continue;
                // /proc/net prints the address in host byte order (little-endian on x86_64/arm64).
                var bytes = BitConverter.GetBytes(hostOrder);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                var ip = new IPAddress(bytes);
                if (IPAddress.IsLoopback(ip) && !found.Contains(ip))
                    found.Add(ip);
            }
        }

        return found;
    }

    /// <summary>
    /// Addresses pdns should listen on when a loopback resolver stub owns port 53: every public/LAN
    /// address on the host plus 127.0.0.1 and ::1 (unless already taken).
    /// </summary>
    public static IReadOnlyList<string> BuildLocalAddresses(IEnumerable<IPAddress> hostAddresses, IReadOnlyList<IPAddress> loopbackListeners)
    {
        var result = new List<string>();
        foreach (var ip in hostAddresses)
        {
            if (IPAddress.IsLoopback(ip))
                continue;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast))
                continue;
            if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                continue;
            var text = ip.ToString();
            if (!result.Contains(text))
                result.Add(text);
        }

        if (!loopbackListeners.Contains(IPAddress.Loopback))
            result.Add("127.0.0.1");
        result.Add("::1");
        return result;
    }

    /// <summary>Unicast addresses of up, non-virtual interfaces (skips docker/veth/bridge links).</summary>
    public static IEnumerable<IPAddress> HostUnicastAddresses()
    {
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            yield break;
        }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var name = nic.Name;
            if (name.StartsWith("docker", StringComparison.Ordinal) || name.StartsWith("veth", StringComparison.Ordinal)
                || name.StartsWith("br-", StringComparison.Ordinal) || name.StartsWith("virbr", StringComparison.Ordinal))
                continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                yield return addr.Address;
        }
    }
}
