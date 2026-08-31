using System.Text.Json.Serialization;

namespace FeatherQuilld.Utils.Remote;

/// <summary>Panel WebSpace settings pulled by the daemon (Wings-style).</summary>
public sealed class PanelWebSpaceConfig
{
    public Guid Uuid { get; set; }
    public string Name { get; set; } = "";
    public PanelWebPlateRef? Webplate { get; set; }
    public PanelWebSpaceBuild? Build { get; set; }
    public List<string> Domains { get; set; } = [];

    [JsonPropertyName("domain_routes")]
    public List<PanelDomainRoute> DomainRoutes { get; set; } = [];

    public bool Ssl { get; set; }

    [JsonPropertyName("ssl_mode")]
    public string SslMode { get; set; } = "acme";

    /// <summary>Site owner's account email for ACME. Empty = node <c>system.proxy.acme_email</c> fallback.</summary>
    [JsonPropertyName("acme_email")]
    public string AcmeEmail { get; set; } = "";

    [JsonPropertyName("waf_enabled")]
    public bool WafEnabled { get; set; }

    [JsonPropertyName("waf_deny_ips")]
    public List<string> WafDenyIps { get; set; } = [];

    [JsonPropertyName("waf_deny_paths")]
    public List<string> WafDenyPaths { get; set; } = [];

    [JsonPropertyName("backend_port")]
    public int BackendPort { get; set; }

    [JsonPropertyName("backend_host")]
    public string BackendHost { get; set; } = "";

    public bool Suspended { get; set; }

    public PanelWebSpaceMeta? Meta { get; set; }
    public List<PanelWebSpaceSchedule> Schedules { get; set; } = [];
}

public sealed class PanelWebSpaceSchedule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [JsonPropertyName("cron_minute")]
    public string CronMinute { get; set; } = "*";

    [JsonPropertyName("cron_hour")]
    public string CronHour { get; set; } = "*";

    [JsonPropertyName("cron_day_of_month")]
    public string CronDayOfMonth { get; set; } = "*";

    [JsonPropertyName("cron_month")]
    public string CronMonth { get; set; } = "*";

    [JsonPropertyName("cron_day_of_week")]
    public string CronDayOfWeek { get; set; } = "*";

    public string Timezone { get; set; } = "UTC";

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    public List<PanelWebSpaceScheduleTask> Tasks { get; set; } = [];
}

public sealed class PanelWebSpaceScheduleTask
{
    public int Id { get; set; }

    [JsonPropertyName("sequence_id")]
    public int SequenceId { get; set; }

    public string Action { get; set; } = "";
    public string Payload { get; set; } = "";

    [JsonPropertyName("time_offset")]
    public int TimeOffset { get; set; }

    [JsonPropertyName("continue_on_failure")]
    public bool ContinueOnFailure { get; set; }
}

/// <summary>WebPlate identity from the panel (not a Spell/egg).</summary>
public sealed class PanelWebPlateRef
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string Runtime { get; set; } = "static";

    [JsonPropertyName("container_port")]
    public int ContainerPort { get; set; }

    public string? Startup { get; set; }

    [JsonPropertyName("docker_image")]
    public string? DockerImage { get; set; }
}

public sealed class PanelDomainRoute
{
    public string Domain { get; set; } = "";
    public string Type { get; set; } = "primary";

    [JsonPropertyName("redirect_target")]
    public string? RedirectTarget { get; set; }

    [JsonPropertyName("document_root")]
    public string DocumentRoot { get; set; } = "";
}

public sealed class PanelWebSpaceBuild
{
    /// <summary>Disk limit in MiB (Wings-style).</summary>
    [JsonPropertyName("disk_space")]
    public long DiskSpace { get; set; }

    /// <summary>CPU limit in cores (e.g. 0.5, 1.0). 0 = unlimited.</summary>
    [JsonPropertyName("cpu_limit")]
    public double CpuLimit { get; set; }

    /// <summary>Memory limit in MiB. 0 = unlimited.</summary>
    [JsonPropertyName("memory_limit")]
    public long MemoryLimit { get; set; }

    /// <summary>Monthly HTTP egress limit in GiB. 0 = unlimited.</summary>
    [JsonPropertyName("bandwidth_limit_gb")]
    public int BandwidthLimitGb { get; set; }
}

public sealed class PanelWebSpaceMeta
{
    [JsonPropertyName("document_root")]
    public string DocumentRoot { get; set; } = "";
}

public sealed class PanelInstallScript
{
    [JsonPropertyName("container_image")]
    public string ContainerImage { get; set; } = "";

    public string Entrypoint { get; set; } = "bash";
    public string Script { get; set; } = "";
}

public sealed class PanelApiEnvelope<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }
}
