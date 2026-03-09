namespace ProjectManagerBot.Models;

public sealed class ProjectRiskSnapshot
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime LocalDate { get; set; }
    public int RiskScore { get; set; }
    public int OpenRiskCount { get; set; }
    public int OverdueTaskCount { get; set; }
    public int StalledTaskCount { get; set; }
    public int MissingStandupCount { get; set; }
    public int OpenBugCount { get; set; }
    public int BlockerCount { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
}
