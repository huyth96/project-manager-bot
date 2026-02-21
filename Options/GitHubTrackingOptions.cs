namespace ProjectManagerBot.Options;

public sealed class GitHubTrackingOptions
{
    public bool Enabled { get; set; } = true;
    public string Token { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
    public int MaxCommitsPerPoll { get; set; } = 10;
}
