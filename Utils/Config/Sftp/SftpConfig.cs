namespace FeatherQuilld.Utils.Config.Sftp;

public class SftpConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Listen port for the SFTP (SSH) server.</summary>
    public int Port { get; set; } = 2222;

    public string KeyAlgorithm { get; set; } = "ssh-ed25519";
    public bool DisablePasswordAuth { get; set; }

    /// <summary>Max entries returned by readdir; 0 means unlimited.</summary>
    public int DirectoryEntryLimit { get; set; } = 20_000;

    /// <summary>Entries sent per readdir call (chunk size).</summary>
    public int DirectoryEntrySendAmount { get; set; } = 500;

    public SftpLimitsConfig Limits { get; set; } = new();
}

public class SftpLimitsConfig
{
    public int AuthenticationPasswordAttempts { get; set; } = 3;
    public int AuthenticationPubkeyAttempts { get; set; } = 20;

    /// <summary>Cooldown in seconds after max auth attempts (0 disables cooldown).</summary>
    public int AuthenticationCooldown { get; set; } = 60;

    public int MaxConnectionsPerUser { get; set; } = 10;
    public int MaxChannelsPerConnection { get; set; } = 10;
    public int MaxHandlesPerChannel { get; set; } = 32;
    public int MaxHandlesTotal { get; set; } = 1024;
}
