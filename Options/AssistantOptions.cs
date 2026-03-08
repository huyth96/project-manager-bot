namespace ProjectManagerBot.Options;

public sealed class AssistantOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string Model { get; set; } = "gpt-4.1-mini";
    public double Temperature { get; set; } = 0.2;
    public int MaxRecentMessages { get; set; } = 12;
    public int MaxStandupDays { get; set; } = 3;
    public int MaxAttentionItems { get; set; } = 8;
}
