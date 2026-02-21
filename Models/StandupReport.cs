namespace ProjectManagerBot.Models;

public sealed class StandupReport
{
    public int Id { get; set; }
    public int ProjectId { get; set; }

    public ulong DiscordUserId { get; set; }
    public DateTime LocalDate { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Yesterday { get; set; } = string.Empty;
    public string Today { get; set; } = string.Empty;
    public string Blockers { get; set; } = string.Empty;

    public Project? Project { get; set; }
}
