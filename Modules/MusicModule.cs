using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ProjectManagerBot.Services;

namespace ProjectManagerBot.Modules;

[Group("music", "Phát nhạc YouTube trong voice channel.")]
[RequireContext(ContextType.Guild)]
public sealed class MusicModule(
    YouTubeMusicService musicService) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;
    private readonly YouTubeMusicService _musicService = musicService;

    [SlashCommand("play", "Phát nhạc YouTube trong voice channel bạn đang tham gia.")]
    public async Task PlayAsync(
        [Summary("video", "URL hoặc video ID YouTube")]
        string video)
    {
        if (Context.User is not SocketGuildUser guildUser || guildUser.VoiceChannel is not IVoiceChannel voiceChannel)
        {
            await RespondAsync("Bạn cần vào voice channel trước khi dùng `/music play`.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        try
        {
            var result = await _musicService.PlayAsync(Context.Guild.Id, voiceChannel, video);
            await FollowupAsync(
                $"Đang phát `{result.Title}` trong <#{result.VoiceChannelId}>.\n{result.VideoUrl}",
                ephemeral: true);
        }
        catch (InvalidOperationException ex)
        {
            await FollowupAsync(ex.Message, ephemeral: true);
        }
    }

    [SlashCommand("now", "Xem trạng thái phát nhạc hiện tại của bot.")]
    public async Task NowAsync()
    {
        var status = _musicService.GetStatus(Context.Guild.Id);
        if (!status.IsConnected)
        {
            await RespondAsync("Bot chưa kết nối voice channel nào.", ephemeral: true);
            return;
        }

        if (!status.IsPlaying)
        {
            var channelText = status.VoiceChannelId.HasValue ? $"<#{status.VoiceChannelId.Value}>" : "voice channel hiện tại";
            await RespondAsync($"Bot đang ở {channelText} nhưng chưa phát bài nào.", ephemeral: true);
            return;
        }

        var title = status.CurrentTitle ?? "Không rõ tiêu đề";
        var voiceChannelText = status.VoiceChannelId.HasValue ? $"<#{status.VoiceChannelId.Value}>" : "voice channel hiện tại";
        await RespondAsync($"Đang phát `{title}` trong {voiceChannelText}.", ephemeral: true);
    }

    [SlashCommand("stop", "Dừng bài đang phát nhưng vẫn ở lại voice channel.")]
    public async Task StopAsync()
    {
        var stopped = await _musicService.StopAsync(Context.Guild.Id);
        await RespondAsync(
            stopped ? "Đã dừng phát nhạc." : "Hiện không có bài nào đang phát.",
            ephemeral: true);
    }

    [SlashCommand("leave", "Dừng nhạc và rời khỏi voice channel.")]
    public async Task LeaveAsync()
    {
        var disconnected = await _musicService.LeaveAsync(Context.Guild.Id);
        await RespondAsync(
            disconnected ? "Đã dừng nhạc và rời voice channel." : "Bot chưa ở voice channel nào.",
            ephemeral: true);
    }

    private async Task RespondAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = false)
    {
        await base.RespondAsync(
            text: text,
            embed: embed,
            components: components,
            ephemeral: ephemeral);

        if (ephemeral)
        {
            _ = DeleteOriginalResponseAfterDelayAsync(TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
        }
    }

    private async Task FollowupAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = false)
    {
        var message = await base.FollowupAsync(
            text: text,
            embed: embed,
            components: components,
            ephemeral: ephemeral);

        if (ephemeral)
        {
            _ = DeleteFollowupAfterDelayAsync(message, TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
        }
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
