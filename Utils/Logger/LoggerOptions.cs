namespace FeatherQuilld.Utils.Logger;

public sealed class LoggerOptions
{
    public const string SectionName = "FeatherQuilld:Logging";

    /// <summary>Directory for <c>latest.log</c> and archived <c>*.log.gz</c> files.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>When false, <see cref="LoggerLevel.Debug"/> messages are skipped.</summary>
    public bool Debug { get; set; }

    /// <summary>Maximum number of gzipped archives to keep after rotation.</summary>
    public int MaxArchives { get; set; } = 20;
}
