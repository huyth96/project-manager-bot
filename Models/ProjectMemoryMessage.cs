namespace ProjectManagerBot.Models;

public sealed class ProjectMemoryMessage
{
    public int Id { get; set; }
    public int ProjectId { get; set; }

    public ulong MessageId { get; set; }
    public ulong ChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public ulong? ThreadId { get; set; }
    public string? ThreadName { get; set; }

    public ulong AuthorId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public bool IsBot { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTime LocalDate { get; set; }

    public string Content { get; set; } = string.Empty;
    public string NormalizedContent { get; set; } = string.Empty;

    public Project? Project { get; set; }
}
