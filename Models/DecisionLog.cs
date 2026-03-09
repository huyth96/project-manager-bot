namespace ProjectManagerBot.Models;

public sealed class DecisionLog
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime LocalDate { get; set; }
    public string TopicKey { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public int ConfidencePercent { get; set; }
    public ulong? SourceMessageId { get; set; }
    public string? SourceChannelName { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
}
