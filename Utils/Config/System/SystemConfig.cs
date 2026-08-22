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

    [YamlMember(Alias = "backups")]
    public string BackupDirectory { get; set; } = "/var/lib/featherquilld/backups";

    public string TmpDirectory { get; set; } = "/tmp/featherquilld";
    public string Username { get; set; } = "featherquilld";
    public string Timezone { get; set; } = "UTC";
    public SystemUserConfig User { get; set; } = new();
    public int DiskCheckInterval { get; set; } = 150;
    public QuotasConfig Quotas { get; set; } = new();
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
    public bool Enabled { get; set; }
}
