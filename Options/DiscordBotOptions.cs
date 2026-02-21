namespace ProjectManagerBot.Options;

public sealed class DiscordBotOptions
{
    public string Token { get; set; } = string.Empty;
    public ulong GuildId { get; set; }
    public bool RegisterCommandsGlobally { get; set; }
    public string TimeZoneId { get; set; } = "SE Asia Standard Time";
}
