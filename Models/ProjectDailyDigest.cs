namespace ProjectManagerBot.Models;

public sealed class ProjectDailyDigest
{
    public int Id { get; set; }
    public int ProjectId { get; set; }

    public DateTime LocalDate { get; set; }
    public int MessageCount { get; set; }
    public int DistinctAuthorCount { get; set; }
    public int UserMessageCount { get; set; }
    public int BotMessageCount { get; set; }
    public int StandupReportCount { get; set; }
    public int BlockerCount { get; set; }

    public string Summary { get; set; } = string.Empty;
    public string KeywordsJson { get; set; } = "[]";
    public string ActiveChannelsJson { get; set; } = "[]";
    public string HighlightsJson { get; set; } = "[]";

    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Project? Project { get; set; }
}
