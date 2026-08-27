namespace FeatherQuilld.Utils.WebSpaces.Schedules;

public sealed class WebSpaceScheduleDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CronMinute { get; set; } = "*";
    public string CronHour { get; set; } = "*";
    public string CronDayOfMonth { get; set; } = "*";
    public string CronMonth { get; set; } = "*";
    public string CronDayOfWeek { get; set; } = "*";
    public string Timezone { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
    public List<WebSpaceScheduleTaskDefinition> Tasks { get; set; } = [];
}
