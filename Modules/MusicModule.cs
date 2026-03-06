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

        var voiceChannel = await ValidateControllerVoiceChannelAsync(requireVoiceChannel: true);
        if (voiceChannel is null)
        {
            return;
        }

        try
        {
            var result = await _musicService.PlayAsync(Context.Guild.Id, voiceChannel, video, Context.User);
            await SendInteractionMessageAsync(BuildPlayResultMessage(result), ephemeral: true);
        }
        catch (InvalidOperationException ex)
        {
            await SendInteractionMessageAsync(ex.Message, ephemeral: true);
        }
    }

    [SlashCommand("panel", "Tao hoac lam moi panel dieu khien music.")]
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
            await SendInteractionMessageAsync("Khong the tao panel music trong kenh hien tai.", ephemeral: true);
            return;
        }

        await SendInteractionMessageAsync(
            $"Da dong bo panel music tai <#{panel.ChannelId}>.",
            ephemeral: true);
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
            await SendInteractionMessageAsync("Bot chua ket noi voice channel nao.", ephemeral: true);
            return;
        }

        var voiceChannelText = status.VoiceChannelId.HasValue
            ? $"<#{status.VoiceChannelId.Value}>"
            : "voice channel hien tai";
        var panelText = status.PanelChannelId.HasValue
            ? $"\nPanel: <#{status.PanelChannelId.Value}>"
            : string.Empty;
        var title = status.CurrentTitle ?? "Khong co bai dang phat";
        var stateText = status.IsPaused ? "Tam dung" : status.IsPlaying ? "Dang phat" : "San sang";

        await SendInteractionMessageAsync(
            $"Trang thai: `{stateText}`\n" +
            $"Bai hien tai: `{title}`\n" +
            $"Voice: {voiceChannelText}\n" +
            $"Hang doi: `{status.QueueCount}`\n" +
            $"Am luong: `{status.Volume:0}%`" +
            panelText,
            ephemeral: true);
    }

    [SlashCommand("stop", "Dung bai dang phat nhung van o lai voice channel.")]
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
            stopped ? "Da dung phat nhac va xoa hang doi." : "Hien khong co bai nao dang phat.",
            ephemeral: true);
    }

    [SlashCommand("leave", "Dung nhac va roi khoi voice channel.")]
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
            disconnected ? "Da dung nhac va roi voice channel." : "Bot chua o voice channel nao.",
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
            var result = await _musicService.PlayAsync(Context.Guild.Id, voiceChannel, modal.VideoReference, Context.User);
            await SendInteractionMessageAsync(BuildPlayResultMessage(result), ephemeral: true);
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
            changed ? "Da cap nhat trang thai phat nhac." : "Khong co player dang hoat dong.",
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
            skipped ? "Da skip bai hien tai." : "Khong co bai nao de skip.",
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
            stopped ? "Da dung phat nhac va xoa hang doi." : "Hien khong co bai nao dang phat.",
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
            disconnected ? "Da roi voice channel." : "Bot chua ket noi voice channel nao.",
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
        await SendInteractionMessageAsync("Da lam moi panel music.", ephemeral: true);
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
            volume.HasValue ? $"Da cap nhat am luong: `{volume.Value:0}%`." : "Khong co player dang hoat dong.",
            ephemeral: true);
    }

    private async Task<IVoiceChannel?> ValidateControllerVoiceChannelAsync(bool requireVoiceChannel)
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            await SendInteractionMessageAsync("Khong xac dinh duoc thanh vien guild hien tai.", ephemeral: true);
            return null;
        }

        var userVoiceChannel = guildUser.VoiceChannel;
        if (requireVoiceChannel && userVoiceChannel is null)
        {
            await SendInteractionMessageAsync("Ban can vao voice channel truoc khi dieu khien music.", ephemeral: true);
            return null;
        }

        var status = _musicService.GetStatus(Context.Guild.Id);
        if (status.VoiceChannelId.HasValue && userVoiceChannel?.Id != status.VoiceChannelId.Value)
        {
            await SendInteractionMessageAsync(
                $"Ban can vao cung voice channel voi bot (<#{status.VoiceChannelId.Value}>) de dieu khien nhac.",
                ephemeral: true);
            return null;
        }

        return userVoiceChannel;
    }

    private static string BuildPlayResultMessage(MusicPlayResult result)
    {
        if (result.AddedCount > 1)
        {
            return $"Da them `{result.AddedCount}` bai vao hang doi. Bai dau: `{result.Title}`.";
        }

        if (result.StartedImmediately)
        {
            return $"Dang phat `{result.Title}` trong <#{result.VoiceChannelId}>.\n{result.VideoUrl}";
        }

        return $"Da them `{result.Title}` vao hang doi (vi tri `{result.QueuePosition}`).";
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
    public string Title => "Them Bai Nhac";

    [InputLabel("URL hoac video ID")]
    [ModalTextInput(
        "music_video_reference",
        TextInputStyle.Short,
        placeholder: "https://www.youtube.com/watch?v=... hoac video ID",
        maxLength: 500)]
    public string VideoReference { get; set; } = string.Empty;
}
