using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using ProjectManagerBot.Services;

namespace ProjectManagerBot.Modules;

[Group("music", "Phát nhạc YouTube trong voice channel.")]
[RequireContext(ContextType.Guild)]
public sealed class MusicModule(
    YouTubeMusicService musicService,
    ILogger<MusicModule> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;

    private readonly YouTubeMusicService _musicService = musicService;
    private readonly ILogger<MusicModule> _logger = logger;

    [SlashCommand("play", "Phát nhạc YouTube trong voice channel bạn đang tham gia.")]
    public async Task PlayAsync(
        [Summary("video", "URL hoặc video ID YouTube")]
        string video)
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        try
        {
            var result = await _musicService.PlayAsync(Context.Guild.Id, voiceChannel, video);
            await SendInteractionMessageAsync(
                $"Đang phát `{result.Title}` trong <#{result.VoiceChannelId}>.\n{result.VideoUrl}",
                ephemeral: true);
        }
        catch (InvalidOperationException ex)
        {
            await SendInteractionMessageAsync(ex.Message, ephemeral: true);
        }
    }

    [SlashCommand("panel", "Tạo hoặc làm mới panel điều khiển music.")]
    public async Task PanelAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var preferredChannel = Context.Channel as ITextChannel;
        var panel = await _musicService.EnsurePanelAsync(Context.Guild, preferredChannel);

        if (panel is null)
        {
            await SendInteractionMessageAsync("Không thể tạo panel music trong kênh hiện tại.", ephemeral: true);
            return;
        }

        await SendInteractionMessageAsync(
            $"Đã đồng bộ panel music tại <#{panel.ChannelId}>.",
            ephemeral: true);
    }

    [SlashCommand("now", "Xem trạng thái phát nhạc hiện tại của bot.")]
    public async Task NowAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var status = _musicService.GetStatus(Context.Guild.Id);
        if (!status.IsConnected)
        {
            await SendInteractionMessageAsync("Bot chưa kết nối voice channel nào.", ephemeral: true);
            return;
        }

        var voiceChannelText = status.VoiceChannelId.HasValue
            ? $"<#{status.VoiceChannelId.Value}>"
            : "voice channel hiện tại";
        var panelText = status.PanelChannelId.HasValue
            ? $"\nPanel: <#{status.PanelChannelId.Value}>"
            : string.Empty;
        var title = status.CurrentTitle ?? "Không có bài đang phát";
        var stateText = status.IsPaused ? "Tạm dừng" : status.IsPlaying ? "Đang phát" : "Sẵn sàng";

        await SendInteractionMessageAsync(
            $"Trạng thái: `{stateText}`\n" +
            $"Bài hiện tại: `{title}`\n" +
            $"Voice: {voiceChannelText}\n" +
            $"Âm lượng: `{status.Volume:0}%`" +
            panelText,
            ephemeral: true);
    }

    [SlashCommand("stop", "Dừng bài đang phát nhưng vẫn ở lại voice channel.")]
    public async Task StopAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        var stopped = await _musicService.StopAsync(Context.Guild.Id);
        await SendInteractionMessageAsync(
            stopped ? "Đã dừng phát nhạc." : "Hiện không có bài nào đang phát.",
            ephemeral: true);
    }

    [SlashCommand("leave", "Dừng nhạc và rời khỏi voice channel.")]
    public async Task LeaveAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        var disconnected = await _musicService.LeaveAsync(Context.Guild.Id);
        await SendInteractionMessageAsync(
            disconnected ? "Đã dừng nhạc và rời voice channel." : "Bot chưa ở voice channel nào.",
            ephemeral: true);
    }

    [ComponentInteraction(MusicPanelConstants.AddTrackButtonId, true)]
    public async Task AddTrackButtonAsync()
    {
        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        await RespondWithModalAsync<MusicAddTrackModal>(MusicPanelConstants.AddTrackModalId);
    }

    [ModalInteraction(MusicPanelConstants.AddTrackModalId, true)]
    public async Task AddTrackModalAsync(MusicAddTrackModal modal)
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        try
        {
            var result = await _musicService.PlayAsync(Context.Guild.Id, voiceChannel, modal.VideoReference);
            await SendInteractionMessageAsync(
                $"Đang phát `{result.Title}` trong <#{result.VoiceChannelId}>.\n{result.VideoUrl}",
                ephemeral: true);
        }
        catch (InvalidOperationException ex)
        {
            await SendInteractionMessageAsync(ex.Message, ephemeral: true);
        }
    }

    [ComponentInteraction(MusicPanelConstants.PauseResumeButtonId, true)]
    public async Task PauseResumeButtonAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        var changed = await _musicService.PauseOrResumeAsync(Context.Guild.Id);
        await SendInteractionMessageAsync(
            changed ? "Đã cập nhật trạng thái phát nhạc." : "Không có player đang hoạt động.",
            ephemeral: true);
    }

    [ComponentInteraction(MusicPanelConstants.SkipButtonId, true)]
    public async Task SkipButtonAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        var skipped = await _musicService.SkipAsync(Context.Guild.Id);
        await SendInteractionMessageAsync(
            skipped ? "Đã bỏ qua bài hiện tại." : "Không có bài nào để bỏ qua.",
            ephemeral: true);
    }

    [ComponentInteraction(MusicPanelConstants.StopButtonId, true)]
    public async Task StopButtonAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        var stopped = await _musicService.StopAsync(Context.Guild.Id);
        await SendInteractionMessageAsync(
            stopped ? "Đã dừng phát nhạc." : "Hiện không có bài nào đang phát.",
            ephemeral: true);
    }

    [ComponentInteraction(MusicPanelConstants.LeaveButtonId, true)]
    public async Task LeaveButtonAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        var disconnected = await _musicService.LeaveAsync(Context.Guild.Id);
        await SendInteractionMessageAsync(
            disconnected ? "Đã rời voice channel." : "Bot chưa kết nối voice channel nào.",
            ephemeral: true);
    }

    [ComponentInteraction(MusicPanelConstants.VolumeDownButtonId, true)]
    public async Task VolumeDownButtonAsync()
    {
        await ChangeVolumeAsync(-10F);
    }

    [ComponentInteraction(MusicPanelConstants.VolumeUpButtonId, true)]
    public async Task VolumeUpButtonAsync()
    {
        await ChangeVolumeAsync(10F);
    }

    [ComponentInteraction(MusicPanelConstants.RefreshButtonId, true)]
    public async Task RefreshButtonAsync()
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        await _musicService.RefreshPanelAsync(Context.Guild.Id);
        await SendInteractionMessageAsync("Đã làm mới panel music.", ephemeral: true);
    }

    private async Task ChangeVolumeAsync(float delta)
    {
        if (!await TryAcknowledgeAsync(ephemeral: true))
        {
            return;
        }

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        var volume = await _musicService.AdjustVolumeAsync(Context.Guild.Id, delta);
        await SendInteractionMessageAsync(
            volume.HasValue ? $"Đã cập nhật âm lượng: `{volume.Value:0}%`." : "Không có player đang hoạt động.",
            ephemeral: true);
    }

    private async Task<IVoiceChannel?> ValidateControllerVoiceChannelAsync(bool requireVoiceChannel)
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            await SendInteractionMessageAsync("Không xác định được thành viên guild hiện tại.", ephemeral: true);
            return null;
        }

        var userVoiceChannel = guildUser.VoiceChannel;
        if (requireVoiceChannel && userVoiceChannel is null)
        {
            await SendInteractionMessageAsync("Bạn cần vào voice channel trước khi điều khiển music.", ephemeral: true);
            return null;
        }

        var status = _musicService.GetStatus(Context.Guild.Id);
        if (status.VoiceChannelId.HasValue && userVoiceChannel?.Id != status.VoiceChannelId.Value)
        {
            await SendInteractionMessageAsync(
                $"Bạn cần vào cùng voice channel với bot (<#{status.VoiceChannelId.Value}>) để điều khiển nhạc.",
                ephemeral: true);
            return null;
        }

        return userVoiceChannel;
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

public sealed class MusicAddTrackModal : IModal
{
    public string Title => "Thêm Bài Nhạc";

    [InputLabel("URL hoặc video ID")]
    [ModalTextInput(
        "music_video_reference",
        TextInputStyle.Short,
        placeholder: "https://www.youtube.com/watch?v=... hoặc video ID",
        maxLength: 500)]
    public string VideoReference { get; set; } = string.Empty;
}
