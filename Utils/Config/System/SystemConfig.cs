namespace FeatherQuilld.Utils.Config.System;

public class SystemConfig
{
    public string RootDirectory { get; set; } = "/var/lib/featherquilld";
    public string LogDirectory { get; set; } = "/var/log/featherquilld";
    public string Data { get; set; } = "/var/lib/featherquilld/volumes";
    public string ArchiveDirectory { get; set; } = "/var/lib/featherquilld/archives";
    public string BackupDirectory { get; set; } = "/var/lib/featherquilld/backups";
    public string TmpDirectory { get; set; } = "/tmp/featherquilld";
    public string Username { get; set; } = "featherquilld";
    public string Timezone { get; set; } = "UTC";
    public SystemUserConfig User { get; set; } = new();
    public MachineIdConfig MachineId { get; set; } = new();
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

public class MachineIdConfig
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "/run/featherquilld/machine-id";
}

public class QuotasConfig
{
    public bool Enabled { get; set; }
}
