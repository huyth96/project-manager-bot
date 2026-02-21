namespace ProjectManagerBot.Models;

public sealed class TaskItem
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int? SprintId { get; set; }

    public TaskItemType Type { get; set; }
    public TaskItemStatus Status { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public int Points { get; set; } = 1;
    public ulong? AssigneeId { get; set; }
    public ulong CreatedById { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTime? LastOverdueReminderDateLocal { get; set; }

    public Project? Project { get; set; }
    public Sprint? Sprint { get; set; }
}
