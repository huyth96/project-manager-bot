namespace ProjectManagerBot.Models;

public sealed class GitHubRepoBinding
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string RepoFullName { get; set; } = string.Empty; // owner/repo
    public string Branch { get; set; } = "main";
    public string? LastSeenCommitSha { get; set; }
    public bool IsEnabled { get; set; } = true;

    public Project? Project { get; set; }
}
