using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using ProjectManagerBot.Services;

namespace ProjectManagerBot.Modules;

[Group("music", "Phat nhac YouTube trong voice channel.")]
[RequireContext(ContextType.Guild)]
public sealed class MusicModule(
    YouTubeMusicService musicService,
    ILogger<MusicModule> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;
    private readonly YouTubeMusicService _musicService = musicService;
    private readonly ILogger<MusicModule> _logger = logger;

    [SlashCommand("play", "Phat nhac YouTube trong voice channel ban dang tham gia.")]
    public async Task PlayAsync(
        [Summary("video", "URL hoac video ID YouTube")]
        string video)
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        if (Context.User is not SocketGuildUser guildUser || guildUser.VoiceChannel is not IVoiceChannel voiceChannel)
        {
            await SendFollowupAsync("Ban can vao voice channel truoc khi dung `/music play`.", ephemeral: true);
            return;
        }

        try
        {
            var result = await _musicService.PlayAsync(Context.Guild.Id, voiceChannel, video);
            await SendFollowupAsync(
                $"Dang phat `{result.Title}` trong <#{result.VoiceChannelId}>.\n{result.VideoUrl}",
                ephemeral: true);
        }
        catch (InvalidOperationException ex)
        {
            await SendFollowupAsync(ex.Message, ephemeral: true);
        }
    }

    [SlashCommand("now", "Xem trang thai phat nhac hien tai cua bot.")]
    public async Task NowAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var status = _musicService.GetStatus(Context.Guild.Id);
        if (!status.IsConnected)
        {
            await SendFollowupAsync("Bot chua ket noi voice channel nao.", ephemeral: true);
            return;
        }

        if (!status.IsPlaying)
        {
            var channelText = status.VoiceChannelId.HasValue ? $"<#{status.VoiceChannelId.Value}>" : "voice channel hien tai";
            await SendFollowupAsync($"Bot dang o {channelText} nhung chua phat bai nao.", ephemeral: true);
            return;
        }

        var title = status.CurrentTitle ?? "Khong ro tieu de";
        var voiceChannelText = status.VoiceChannelId.HasValue ? $"<#{status.VoiceChannelId.Value}>" : "voice channel hien tai";
        await SendFollowupAsync($"Dang phat `{title}` trong {voiceChannelText}.", ephemeral: true);
    }

    [SlashCommand("stop", "Dung bai dang phat nhung van o lai voice channel.")]
    public async Task StopAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var stopped = await _musicService.StopAsync(Context.Guild.Id);
        await SendFollowupAsync(
            stopped ? "Da dung phat nhac." : "Hien khong co bai nao dang phat.",
            ephemeral: true);
    }

    [SlashCommand("leave", "Dung nhac va roi khoi voice channel.")]
    public async Task LeaveAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var disconnected = await _musicService.LeaveAsync(Context.Guild.Id);
        await SendFollowupAsync(
            disconnected ? "Da dung nhac va roi voice channel." : "Bot chua o voice channel nao.",
            ephemeral: true);
    }

    private async Task<bool> TryAcknowledgeAsync(bool ephemeral)
    {
        if (Context.Interaction.HasResponded)
        {
            return true;
        }

        try
        {
            await DeferAsync(ephemeral: ephemeral);
            return true;
        }
        catch (HttpException ex) when (IsInteractionAlreadyAcknowledged(ex))
        {
            return true;
        }
        catch (HttpException ex) when (IsUnknownInteraction(ex))
        {
            _logger.LogWarning(ex, "Interaction expired before defer for /music command.");
            return false;
        }
    }

    private async Task SendInteractionMessageAsync(string text, bool ephemeral)
    {
        if (Context.Interaction.HasResponded)
        {
            await SendFollowupAsync(text, ephemeral);
            return;
        }

        try
        {
            await base.RespondAsync(text: text, ephemeral: ephemeral);
            if (ephemeral)
            {
                _ = DeleteOriginalResponseAfterDelayAsync(TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
            }
        }
        catch (HttpException ex) when (IsInteractionAlreadyAcknowledged(ex))
        {
            await SendFollowupAsync(text, ephemeral);
        }
        catch (HttpException ex) when (IsUnknownInteraction(ex))
        {
            _logger.LogWarning(ex, "Interaction expired before initial response for /music command.");
        }
    }

    private async Task SendFollowupAsync(string text, bool ephemeral)
    {
        try
        {
            var message = await base.FollowupAsync(text: text, ephemeral: ephemeral);
            if (ephemeral)
            {
                _ = DeleteFollowupAfterDelayAsync(message, TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
            }
        }
        catch (HttpException ex) when (IsUnknownInteraction(ex))
        {
            _logger.LogWarning(ex, "Interaction expired before follow-up response for /music command.");
        }
    }

    private static bool IsInteractionAlreadyAcknowledged(HttpException exception)
    {
        return exception.DiscordCode.HasValue && (int)exception.DiscordCode.Value == 40060;
    }

    private static bool IsUnknownInteraction(HttpException exception)
    {
        return exception.DiscordCode.HasValue && (int)exception.DiscordCode.Value == 10062;
    }

    private async Task DeleteOriginalResponseAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await Context.Interaction.DeleteOriginalResponseAsync();
        }
        catch
        {
        }
    }

    private static async Task DeleteFollowupAfterDelayAsync(IUserMessage message, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await message.DeleteAsync();
        }
        catch
        {
        }
    }
}
