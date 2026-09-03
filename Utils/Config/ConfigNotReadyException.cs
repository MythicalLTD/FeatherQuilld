namespace FeatherQuilld.Utils.Config;

/// <summary>
/// Thrown when the daemon cannot use the system layout (typically a non-root user
/// with no install). Handled in <c>Program.Main</c> so the process exits cleanly
/// instead of dumping core.
/// </summary>
public sealed class ConfigNotReadyException : Exception
{
    public const string Hint =
        """
        FeatherQuilld is not set up for this user.

        The daemon will not start until this machine is joined to FeatherPanel
        as root (Docker, bind-mounts, and system paths):

          sudo quilld configure
          sudo systemctl enable --now featherquilld
        """;

    public ConfigNotReadyException()
        : base(Hint)
    {
    }

    public ConfigNotReadyException(string message)
        : base(message)
    {
    }

    public ConfigNotReadyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
