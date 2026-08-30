using YamlDotNet.Serialization;

namespace FeatherQuilld.Utils.Config.System;

public class SystemConfig
{
    public const string DefaultRootDirectory = "/var/lib/featherquilld";
    public const string DefaultLogDirectory = "/var/log/featherquilld";
    public const string DefaultPluginsDirectory = "/var/lib/featherquilld/plugins";

    public string RootDirectory { get; set; } = DefaultRootDirectory;
    public string LogDirectory { get; set; } = DefaultLogDirectory;
    public string Data { get; set; } = "/var/lib/featherquilld/volumes";
    public string Websites { get; set; } = "/var/lib/featherquilld/websites";
    public string ArchiveDirectory { get; set; } = "/var/lib/featherquilld/archives";

    [YamlMember(Alias = "backup_directory")]
    public string BackupDirectory { get; set; } = "/var/lib/featherquilld/backups";

    public BackupsConfig Backups { get; set; } = new();

    public string TmpDirectory { get; set; } = "/tmp/featherquilld";
    public string EggsDirectory { get; set; } = "/var/lib/featherquilld/eggs";
    public string VmountDirectory { get; set; } = "/var/lib/featherquilld/vmounts";
    public string FusequotaPath { get; set; } = "fusequota";

    /// <summary><c>none</c> or <c>fuse_quota</c>. Default is FuseQuota; empty/none + quotas.enabled → fuse_quota.</summary>
    public string DiskLimiterMode { get; set; } = "fuse_quota";

    public ProxyConfig Proxy { get; set; } = new();
    public DnsConfig Dns { get; set; } = new();
    public MailConfig Mail { get; set; } = new();
    public string Username { get; set; } = "featherquilld";
    public string Timezone { get; set; } = "UTC";
    public SystemUserConfig User { get; set; } = new();
    public int DiskCheckInterval { get; set; } = 150;
    public QuotasConfig Quotas { get; set; } = new();

    /// <summary>
    /// Effective limiter: FuseQuota by default; explicit <c>none</c> disables unless quotas.enabled.
    /// </summary>
    [YamlIgnore]
    public DiskLimiterModeKind EffectiveDiskLimiterMode
    {
        get
        {
            var mode = (DiskLimiterMode ?? "fuse_quota").Trim().ToLowerInvariant().Replace('-', '_');
            if (mode is "fuse_quota" or "fusequota" or "")
                return DiskLimiterModeKind.FuseQuota;
            if (mode is "none" or "off" or "disabled")
                return Quotas.Enabled ? DiskLimiterModeKind.FuseQuota : DiskLimiterModeKind.None;
            if (Quotas.Enabled)
                return DiskLimiterModeKind.FuseQuota;
            return DiskLimiterModeKind.None;
        }
    }
}

public enum DiskLimiterModeKind
{
    None,
    FuseQuota,
}

public class ProxyConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary><c>caddy</c>, <c>nginx</c>, or <c>traefik</c>.</summary>
    public string Provider { get; set; } = "caddy";
    public string ConfigPath { get; set; } = "";
    /// <summary>Operator ACME fallback. Per-WebSpace owner email is preferred when present.</summary>
    public string AcmeEmail { get; set; } = "";

    /// <summary>Use Let's Encrypt staging directory (safer for tests).</summary>
    [YamlMember(Alias = "acme_staging")]
    public bool AcmeStaging { get; set; }

    /// <summary>Inclusive low end of loopback backend ports for Docker runtimes.</summary>
    public int BackendPortMin { get; set; } = 20000;

    /// <summary>Inclusive high end of loopback backend ports for Docker runtimes.</summary>
    public int BackendPortMax { get; set; } = 29999;

    /// <summary>
    /// Upstream address written into generated proxy configs (Caddy/nginx/Traefik).
    /// Default <c>127.0.0.1</c> when proxy runs on the same host as FeatherQuilld.
    /// </summary>
    [YamlMember(Alias = "backend_host")]
    public string BackendHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Host/interface where WebSpace container ports and static backends are published.
    /// Default <c>127.0.0.1</c> (not reachable off-box). Use <c>0.0.0.0</c> or the node IP
    /// when an external reverse proxy must reach backends on this machine.
    /// </summary>
    [YamlMember(Alias = "backend_bind_host")]
    public string BackendBindHost { get; set; } = "127.0.0.1";
}

public class DnsConfig
{
    /// <summary>PowerDNS Authoritative HTTP API base URL (localhost only).</summary>
    [YamlMember(Alias = "powerdns_api_url")]
    public string PowerDnsApiUrl { get; set; } = "http://127.0.0.1:8081";

    /// <summary>API key for PowerDNS webserver/API. Generated on install if empty.</summary>
    [YamlMember(Alias = "powerdns_api_key")]
    public string PowerDnsApiKey { get; set; } = "";
}

public class MailConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Public mail hostname for MX records (defaults to mail.{domain}).</summary>
    public string Hostname { get; set; } = "";

    [YamlMember(Alias = "data_path")]
    public string DataPath { get; set; } = "";

    [YamlMember(Alias = "smtp_port")]
    public int SmtpPort { get; set; } = 587;

    [YamlMember(Alias = "imap_port")]
    public int ImapPort { get; set; } = 993;

    [YamlMember(Alias = "dkim_selector")]
    public string DkimSelector { get; set; } = "mail";
}

public class SystemUserConfig
{
    public RootlessConfig Rootless { get; set; } = new();
    public int Uid { get; set; }
    public int Gid { get; set; }
    public bool MountPasswd { get; set; } = true;
    public string PasswdFile { get; set; } = "/etc/featherquilld/passwd";
}

public class RootlessConfig
{
    public bool Enabled { get; set; }
    public int ContainerUid { get; set; }
    public int ContainerGid { get; set; }
}

public class QuotasConfig
{
    /// <summary>Hard disk quotas via FuseQuota. On by default for WebSpaces.</summary>
    public bool Enabled { get; set; } = true;
}

public class BackupsConfig
{
    /// <summary><c>local</c>, <c>s3</c>, <c>restic</c>, or <c>pbs</c>.</summary>
    public string Provider { get; set; } = "local";

    public BackupS3Config S3 { get; set; } = new();
    public BackupResticConfig Restic { get; set; } = new();
    public BackupPbsConfig Pbs { get; set; } = new();
}

public class BackupS3Config
{
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "featherquilld-backups";

    [YamlMember(Alias = "access_key")]
    public string AccessKey { get; set; } = "";

    [YamlMember(Alias = "secret_key")]
    public string SecretKey { get; set; } = "";

    public string Prefix { get; set; } = "webspaces/";

    [YamlMember(Alias = "force_path_style")]
    public bool ForcePathStyle { get; set; }
}


public class BackupResticConfig
{
    /// <summary>restic repository URL (e.g. s3:s3.amazonaws.com/bucket or /var/backups/restic).</summary>
    public string Repository { get; set; } = "";

    /// <summary>Password for the repository (RESTIC_PASSWORD).</summary>
    public string Password { get; set; } = "";

    /// <summary>Optional path to restic binary (default: restic on PATH).</summary>
    public string Binary { get; set; } = "restic";
}

public class BackupPbsConfig
{
    /// <summary>Proxmox Backup Server repository, e.g. user@pbs@host:datastore.</summary>
    public string Repository { get; set; } = "";

    /// <summary>API token / password for PBS.</summary>
    public string Password { get; set; } = "";

    /// <summary>Optional fingerprint for TLS verification.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Optional path to proxmox-backup-client (default on PATH).</summary>
    public string Binary { get; set; } = "proxmox-backup-client";
}
