namespace ProjectManagerBot.Models;

public sealed class SprintDailySnapshot
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int SprintId { get; set; }
    public DateTime LocalDate { get; set; }
    public int TotalTasks { get; set; }
    public int DoneTasks { get; set; }
    public int InProgressTasks { get; set; }
    public int BacklogTasksInSprint { get; set; }
    public int OpenBugCount { get; set; }
    public int TotalPoints { get; set; }
    public int DonePoints { get; set; }
    public int InProgressPoints { get; set; }
    public int DeliveryProgressPercent { get; set; }
    public int? ScheduleProgressPercent { get; set; }
    public int StalledTaskCount { get; set; }
    public int OverdueTaskCount { get; set; }
    public string HealthLabel { get; set; } = string.Empty;
    public int? HealthDeltaPercent { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
}
