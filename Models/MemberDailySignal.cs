namespace ProjectManagerBot.Models;

public sealed class MemberDailySignal
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public ulong DiscordUserId { get; set; }
    public DateTime LocalDate { get; set; }
    public bool ExpectedStandup { get; set; }
    public bool SubmittedStandup { get; set; }
    public bool WasLate { get; set; }
    public int? LateMinutes { get; set; }
    public bool HasBlocker { get; set; }
    public int CompletedTasks { get; set; }
    public int FixedBugs { get; set; }
    public int ActivityCount { get; set; }
    public int OpenTaskCount { get; set; }
    public int OpenBugCount { get; set; }
    public int OpenPoints { get; set; }
    public int ReliabilityScore { get; set; }
    public string EvidenceJson { get; set; } = "[]";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
}
