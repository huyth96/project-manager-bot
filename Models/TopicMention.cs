namespace ProjectManagerBot.Models;

public sealed class TopicMention
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime LocalDate { get; set; }
    public string TopicKey { get; set; } = string.Empty;
    public int MentionCount { get; set; }
    public int DistinctAuthorCount { get; set; }
    public string TopChannelsJson { get; set; } = "[]";
    public string TopAuthorsJson { get; set; } = "[]";
    public string SourceSummary { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "[]";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
}
