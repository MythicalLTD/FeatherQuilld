namespace FeatherQuilld.Utils.Config.Docker;

public class DockerConfig
{
    public DockerNetworkConfig Network { get; set; } = new();
    public string Domainname { get; set; } = "";
    public Dictionary<string, object> Registries { get; set; } = [];
    public int TmpfsSize { get; set; } = 100;
    public int ContainerPidLimit { get; set; } = 512;
    public DockerInstallerLimitsConfig InstallerLimits { get; set; } = new();
    public DockerOverheadConfig Overhead { get; set; } = new();
    public bool UsePerformantInspect { get; set; } = true;
    public DockerRuntimeReconciliationConfig RuntimeReconciliation { get; set; } = new();
    public string UsernsMode { get; set; } = "";
    public List<string> SystemIps { get; set; } = [];
    public bool EnableNativeKvm { get; set; } = true;
    public DockerLogConfig LogConfig { get; set; } = new();

    /// <summary>Docker engine Unix socket (or <c>unix:///path</c>).</summary>
    public string Socket { get; set; } = "/var/run/docker.sock";
}

public class DockerNetworkConfig
{
    public string Interface { get; set; } = "172.19.0.1";
    public List<string> Dns { get; set; } = ["1.1.1.1", "1.0.0.1"];
    public string Name { get; set; } = "featherquilld_nw";
    public bool Ispn { get; set; }
    public bool Ipv6 { get; set; } = true;
    public string Driver { get; set; } = "bridge";
    public string NetworkMode { get; set; } = "featherquilld_nw";
    public bool IsInternal { get; set; }
    public bool EnableIcc { get; set; } = true;
    public int NetworkMtu { get; set; } = 1500;
    public DockerNetworkInterfacesConfig Interfaces { get; set; } = new();
}

public class DockerNetworkInterfacesConfig
{
    public DockerNetworkInterfaceConfig V4 { get; set; } = new()
    {
        Subnet = "172.19.0.0/16",
        Gateway = "172.19.0.1",
    };

    public DockerNetworkInterfaceConfig V6 { get; set; } = new()
    {
        Subnet = "fdba:17c8:6c94::/64",
        Gateway = "fdba:17c8:6c94::1011",
    };
}

public class DockerNetworkInterfaceConfig
{
    public string Subnet { get; set; } = "";
    public string Gateway { get; set; } = "";
}

public class DockerInstallerLimitsConfig
{
    public int Memory { get; set; } = 6144;
    public int Cpu { get; set; } = 200;
}

public class DockerOverheadConfig
{
    public bool Override { get; set; }
    public double DefaultMultiplier { get; set; } = 1.05;
    public Dictionary<string, double> Multipliers { get; set; } = [];
}

public class DockerRuntimeReconciliationConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public int InspectTimeoutSeconds { get; set; } = 5;
    public int StuckStoppingSeconds { get; set; } = 720;
    public int StuckStartingSeconds { get; set; } = 300;
    public int UnresponsiveThreshold { get; set; } = 2;
    public int RecoveryCooldownSeconds { get; set; } = 300;
}

public class DockerLogConfig
{
    public string Type { get; set; } = "local";

    public Dictionary<string, string> Config { get; set; } = new()
    {
        ["compress"] = "false",
        ["max-file"] = "1",
        ["max-size"] = "5m",
        ["mode"] = "non-blocking",
    };
}
