namespace ProjectManagerBot.Models;

public sealed class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ulong ChannelId { get; set; }
    public ulong BugChannelId { get; set; }
    public ulong StandupChannelId { get; set; }
    public ulong? GitHubCommitsChannelId { get; set; }
    public ulong? GlobalNotificationChannelId { get; set; }
    public ulong? DashboardMessageId { get; set; }
    public ulong? DailySummaryMessageId { get; set; }

    public DateTime? LastStandupDateLocal { get; set; }

    public ICollection<Sprint> Sprints { get; set; } = new List<Sprint>();
    public ICollection<TaskItem> TaskItems { get; set; } = new List<TaskItem>();
}
