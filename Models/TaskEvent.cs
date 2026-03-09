namespace ProjectManagerBot.Models;

public sealed class TaskEvent
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int TaskItemId { get; set; }
    public TaskItemType TaskType { get; set; }
    public TaskEventType EventType { get; set; }
    public ulong? ActorDiscordId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LocalDate { get; set; }
    public string TitleSnapshot { get; set; } = string.Empty;
    public string? DescriptionSnapshot { get; set; }
    public TaskItemStatus? FromStatus { get; set; }
    public TaskItemStatus? ToStatus { get; set; }
    public ulong? FromAssigneeId { get; set; }
    public ulong? ToAssigneeId { get; set; }
    public int? FromSprintId { get; set; }
    public int? ToSprintId { get; set; }
    public int? FromPoints { get; set; }
    public int? ToPoints { get; set; }
    public string? Summary { get; set; }
    public string? Source { get; set; }

    public Project? Project { get; set; }
}
