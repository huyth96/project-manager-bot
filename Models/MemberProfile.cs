namespace ProjectManagerBot.Models;

public sealed class MemberProfile
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public ulong DiscordUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string RoleSummary { get; set; } = string.Empty;
    public string SkillKeywordsJson { get; set; } = "[]";
    public string DominantTopicsJson { get; set; } = "[]";
    public string ActiveChannelsJson { get; set; } = "[]";
    public int TotalMessageCount { get; set; }
    public int TotalStandupReports { get; set; }
    public int TotalTaskEvents { get; set; }
    public int MissingStandupDays { get; set; }
    public int LateStandupRatePercent { get; set; }
    public int? AverageLateMinutes { get; set; }
    public int BlockerDays { get; set; }
    public int CompletedTasksRecent { get; set; }
    public int FixedBugsRecent { get; set; }
    public int OpenTaskCount { get; set; }
    public int OpenBugCount { get; set; }
    public int OpenPoints { get; set; }
    public int ReliabilityScore { get; set; }
    public int ConfidencePercent { get; set; }
    public string StandupSummary { get; set; } = string.Empty;
    public string CurrentFocusSummary { get; set; } = string.Empty;
    public string RecentOutputSummary { get; set; } = string.Empty;
    public string RiskSummary { get; set; } = string.Empty;
    public string EvidenceSummary { get; set; } = string.Empty;
    public DateTime? LastSignalDate { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
}
