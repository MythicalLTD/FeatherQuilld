namespace FeatherQuilld.Utils.WebSpaces.Schedules;

public sealed class WebSpaceScheduleTaskDefinition
{
    public int Id { get; set; }
    public int SequenceId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int TimeOffset { get; set; }
    public bool ContinueOnFailure { get; set; }
}
