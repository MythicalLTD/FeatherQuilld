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

        The daemon will not start until this machine is joined to FeatherPanel.
        That writes /etc/featherquilld/config.yml (root required):

          sudo quilld configure
          sudo systemctl enable --now featherquilld

        Or start the binary on a TTY — it opens the setup wizard automatically.
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
