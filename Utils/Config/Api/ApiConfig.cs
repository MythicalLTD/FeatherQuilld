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

    /// <summary>Prometheus <c>/metrics</c> endpoint (scrape access, dedicated listener, allowlist).</summary>
    public ApiMetricsConfig Metrics { get; set; } = new();

    /// <summary>Kubernetes-style <c>/health/live</c> and <c>/health/ready</c> probes.</summary>
    public ApiHealthConfig Health { get; set; } = new();

    [YamlIgnore]
    public long UploadLimitBytes => (long)UploadLimit * 1024 * 1024;
}

public class ApiDocsConfig
{
    /// <summary>OpenAPI/Scalar docs. Off by default for production hardening.</summary>
    public bool Enabled { get; set; }
}

public class ApiSslConfig
{
    public bool Enabled { get; set; }
    public string Cert { get; set; } = "cert.pem";
    public string Key { get; set; } = "key.pem";

    /// <summary>Optional password for PKCS#12 (.pfx / .p12) certificates.</summary>
    public string Password { get; set; } = "";
}

public class RemoteDownloadConfig
{
    public int MaxRedirects { get; set; } = 10;
}

public class ApiMetricsConfig
{
    /// <summary>Serve the Prometheus endpoint. Every scrape still needs a token unless <see cref="AllowAnonymous"/> is set.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path of the scrape endpoint.</summary>
    public string Path { get; set; } = "/metrics";

    /// <summary>
    /// Dedicated plain-HTTP listener for metrics and health probes. 0 = serve on the main API port.
    /// When set, <see cref="Path"/> is no longer answered on the main API port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>Bind address of the dedicated listener (only used when <see cref="Port"/> is set).</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// Scrape-only bearer token, so Prometheus never needs the node token. The node token is accepted as well.
    /// Empty = only the node token works.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>Skip the token check. Only sensible together with <see cref="AllowedIps"/> or a loopback-only listener.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>IP addresses or CIDR ranges allowed to scrape. Empty = any address (token still required).</summary>
    public List<string> AllowedIps { get; set; } = [];

    /// <summary>Export one series set per WebSpace (labelled by uuid). Turn off on nodes with very many WebSpaces.</summary>
    public bool PerWebspace { get; set; } = true;
}

public class ApiHealthConfig
{
    /// <summary>Serve <c>/health/live</c> and <c>/health/ready</c> (no auth; the legacy <c>/api/system/health</c> is unchanged).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Include per-check descriptions and data for unauthenticated callers. Off by default:
    /// anonymous callers only see each check's name and status; send the node or metrics token for details.
    /// </summary>
    public bool PublicDetails { get; set; }

    /// <summary>Readiness is degraded below this much free space on the data disk (percent).</summary>
    public double DiskWarnPercent { get; set; } = 10;

    /// <summary>Readiness is unhealthy below this much free space on the data disk (percent).</summary>
    public double DiskCriticalPercent { get; set; } = 3;
}
