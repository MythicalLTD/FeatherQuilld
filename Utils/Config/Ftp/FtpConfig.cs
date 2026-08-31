namespace FeatherQuilld.Utils.Config.Ftp;

public class FtpConfig
{
    public bool Enabled { get; set; }

    /// <summary>Listen port for classic FTP (control channel).</summary>
    public int Port { get; set; } = 21;

    /// <summary>Public hostname/IP advertised in PASV responses. Falls back to bind address when empty.</summary>
    public string PassiveHost { get; set; } = "";

    public int PassivePortMin { get; set; } = 50_000;
    public int PassivePortMax { get; set; } = 50_100;

    public bool DisablePasswordAuth { get; set; }
}
