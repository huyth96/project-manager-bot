namespace ProjectManagerBot.Models;

public sealed class Sprint
{
    public int Id { get; set; }
    public int ProjectId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime? StartDateLocal { get; set; }
    public DateTime? EndDateLocal { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }

    public Project? Project { get; set; }
    public ICollection<TaskItem> TaskItems { get; set; } = new List<TaskItem>();
}
