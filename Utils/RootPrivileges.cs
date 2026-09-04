using System.Runtime.InteropServices;

namespace FeatherQuilld.Utils;

/// <summary>
/// FeatherWings-style privilege check. Production uses Docker, bind-mounts,
/// fusequota, and system paths under /etc and /var that requires root.
/// </summary>
public static class RootPrivileges
{
    public const string Hint =
        """
        FeatherQuilld must run as root (same as FeatherWings).

        It talks to the Docker daemon, bind-mounts WebSpace volumes, and writes
        /etc/featherquilld, /var/lib/featherquilld, and /var/log/featherquilld.

          sudo quilld configure
          sudo systemctl enable --now featherquilld

        Do not run the node as a regular user.
        """;

    public static bool IsRoot()
    {
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            return GetEffectiveUserId() == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequiresRoot(string? configPath)
    {
        if (OperatingSystem.IsWindows())
            return false;

        if (string.IsNullOrWhiteSpace(configPath))
            return true;

        return global::FeatherQuilld.Utils.Config.Config.IsSystemDefaultPath(configPath);
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    private static extern uint GetEffectiveUserId();
}
