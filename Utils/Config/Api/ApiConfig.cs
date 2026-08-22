using YamlDotNet.Serialization;

namespace FeatherQuilld.Utils.Config.Api;

public class ApiConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8989;
    public ApiDocsConfig Docs { get; set; } = new();
    public ApiSslConfig Ssl { get; set; } = new();
    public bool DisableRemoteDownload { get; set; }
    public RemoteDownloadConfig RemoteDownload { get; set; } = new();
    /// <summary>Max upload size in megabytes.</summary>
    public int UploadLimit { get; set; } = 100;
    public List<string> TrustedProxies { get; set; } = [];
    public bool IgnoreCertificateErrors { get; set; }
    public List<string> AllowedOrigins { get; set; } = ["http://localhost:3000", "http://localhost:3001"];

    [YamlIgnore]
    public long UploadLimitBytes => (long)UploadLimit * 1024 * 1024;
}

public class ApiDocsConfig
{
    public bool Enabled { get; set; } = true;
}

public class ApiSslConfig
{
    public bool Enabled { get; set; }
    public string Cert { get; set; } = "cert.pem";
    public string Key { get; set; } = "key.pem";
}

public class RemoteDownloadConfig
{
    public int MaxRedirects { get; set; } = 10;
}
