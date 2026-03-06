using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.DiscordNet;
using Lavalink4NET.Events.Players;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;

namespace ProjectManagerBot.Services;

public sealed class YouTubeMusicService
{
    private const float DefaultVolume = 50F;
    private const float VolumeStep = 10F;
    private const float MinVolume = 10F;
    private const float MaxVolume = 100F;
    private const int QueuePreviewLimit = 5;

    private static readonly Regex YouTubeIdRegex = new("^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PathVideoIdRegex = new(@"^[a-zA-Z0-9_-]{11}", RegexOptions.Compiled);

    private readonly IAudioService _audioService;
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<YouTubeMusicService> _logger;
    private readonly ConcurrentDictionary<ulong, GuildMusicSession> _sessions = new();

    public YouTubeMusicService(
        IAudioService audioService,
        DiscordSocketClient discordClient,
        ILogger<YouTubeMusicService> logger)
    {
        _audioService = audioService;
        _discordClient = discordClient;
        _logger = logger;
        _audioService.TrackStarted += OnTrackStartedAsync;
        _audioService.TrackEnded += OnTrackEndedAsync;
        _audioService.TrackException += OnTrackExceptionAsync;
    }

    public async Task<MusicPlayResult> PlayAsync(
        ulong guildId,
        IVoiceChannel targetChannel,
        string videoReference,
        IUser requestedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoReference))
        {
            throw new InvalidOperationException("Hãy nhập URL hoặc video ID YouTube hợp lệ.");
        }

        var normalizedReference = NormalizeVideoReference(videoReference);
        var loadResult = await LoadTracksAsync(normalizedReference, cancellationToken);
        var tracks = loadResult.Tracks;

        if (tracks.Length == 0)
        {
            throw new InvalidOperationException("Không tìm thấy bài nào hợp lệ để phát.");
        }

        var player = await JoinPlayerAsync(guildId, targetChannel, cancellationToken);
        var requesterDisplayName = requestedBy is SocketGuildUser guildUser ? guildUser.DisplayName : requestedBy.Username;

        var addedCount = 0;
        var firstQueuePosition = -1;
        LavalinkTrack? firstTrack = null;

        foreach (var track in tracks)
        {
            var queueItem = new RequestedTrackQueueItem(track, requestedBy.Id, requesterDisplayName);
            var queuePosition = await player.PlayAsync(queueItem, enqueue: true, cancellationToken: cancellationToken);

            if (firstTrack is null)
            {
                firstTrack = track;
                firstQueuePosition = queuePosition;
            }

            addedCount++;
        }

        await RefreshPanelAsync(guildId, cancellationToken);

        var primaryTrack = firstTrack ?? tracks[0];
        var videoUrl = primaryTrack.Uri?.ToString() ?? normalizedReference;

        _logger.LogInformation(
            "Queued playback via Lavalink for guild {GuildId}: {TrackTitle} ({VideoUrl}), {AddedCount} track(s), queue position {QueuePosition}, voice bitrate {VoiceBitrate}.",
            guildId,
            primaryTrack.Title,
            videoUrl,
            addedCount,
            firstQueuePosition,
            targetChannel.Bitrate);

        return new MusicPlayResult(
            Title: primaryTrack.Title,
            VideoUrl: videoUrl,
            VoiceChannelId: targetChannel.Id,
            AddedCount: addedCount,
            QueuePosition: firstQueuePosition,
            StartedImmediately: firstQueuePosition == 0);
    }

    public async Task<bool> PauseOrResumeAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        if (player.State is PlayerState.Paused)
        {
            await player.ResumeAsync(cancellationToken);
        }
        else
        {
            await player.PauseAsync(cancellationToken);
        }

        await RefreshPanelAsync(guildId, cancellationToken);
        return true;
    }

    public async Task<bool> SkipAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        var hasTrack = player.CurrentTrack is not null;
        if (!hasTrack)
        {
            return false;
        }

        await player.SkipAsync(cancellationToken: cancellationToken);
        await RefreshPanelAsync(guildId, cancellationToken);
        return true;
    }

    public async Task<float?> AdjustVolumeAsync(ulong guildId, float delta, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return null;
        }

        var targetVolume = Math.Clamp(player.Volume + delta, MinVolume, MaxVolume);
        await player.SetVolumeAsync(targetVolume, cancellationToken);

        var session = GetOrCreateSession(guildId);
        session.Volume = targetVolume;

        await RefreshPanelAsync(guildId, cancellationToken);
        return targetVolume;
    }

    public async Task<bool> StopAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        var hadPlayback = player.State is PlayerState.Playing or PlayerState.Paused || player.CurrentTrack is not null || player.Queue.Count > 0;
        await player.StopAsync(cancellationToken);
        await RefreshPanelAsync(guildId, cancellationToken);
        return hadPlayback;
    }

    public async Task<bool> LeaveAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        await player.DisconnectAsync(cancellationToken);
        await RefreshPanelAsync(guildId, cancellationToken);
        return true;
    }

    public MusicPlaybackStatus GetStatus(ulong guildId)
    {
        var session = GetOrCreateSession(guildId);
        var discordVoiceChannelId = TryGetBotVoiceChannelId(guildId);

        if (!TryGetPlayer(guildId, out var player))
        {
            return new MusicPlaybackStatus(
                IsConnected: discordVoiceChannelId.HasValue,
                IsPlaying: false,
                IsPaused: false,
                CurrentTitle: null,
                VoiceChannelId: discordVoiceChannelId,
                QueueCount: 0,
                Volume: session.Volume,
                PanelChannelId: session.PanelChannelId);
        }

        var isConnected = player.ConnectionState.IsConnected || discordVoiceChannelId.HasValue;
        var hasTrack = player.CurrentTrack is not null;
        var isPaused = player.State is PlayerState.Paused;
        var isPlaying = player.State is PlayerState.Playing or PlayerState.Paused || (isConnected && hasTrack);
        var voiceChannelId = player.ConnectionState.IsConnected ? player.VoiceChannelId : discordVoiceChannelId;

        return new MusicPlaybackStatus(
            IsConnected: isConnected,
            IsPlaying: isPlaying,
            IsPaused: isPaused,
            CurrentTitle: player.CurrentTrack?.Title,
            VoiceChannelId: voiceChannelId,
            QueueCount: player.Queue.Count,
            Volume: player.Volume,
            PanelChannelId: session.PanelChannelId);
    }

    public async Task<MusicPanelResult?> EnsurePanelAsync(
        SocketGuild guild,
        ITextChannel? preferredChannel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guild);

        var session = GetOrCreateSession(guild.Id);
        var channel = ResolvePanelChannel(guild, session, preferredChannel);
        if (channel is null)
        {
            return null;
        }

        session.PanelChannelId = channel.Id;

        await session.SyncRoot.WaitAsync(cancellationToken);
        try
        {
            var message = await ResolveOrCreatePanelMessageAsync(guild, channel, session, cancellationToken);
            var status = GetStatus(guild.Id);
            var embed = BuildPanelEmbed(guild, status, TryGetPlayer(guild.Id, out var player) ? player : null);
            var components = BuildPanelComponents(status);

            await message.ModifyAsync(props =>
            {
                props.Embed = embed;
                props.Components = components;
            });

            session.PanelMessageId = message.Id;
            return new MusicPanelResult(channel.Id, message.Id);
        }
        finally
        {
            session.SyncRoot.Release();
        }
    }

    public async Task RefreshPanelAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var guild = _discordClient.GetGuild(guildId);
        if (guild is null)
        {
            return;
        }

        await EnsurePanelAsync(guild, cancellationToken: cancellationToken);
    }

    private async Task OnTrackStartedAsync(object sender, TrackStartedEventArgs args)
    {
        await RefreshPanelAsync(args.Player.GuildId);
    }

    private async Task OnTrackEndedAsync(object sender, TrackEndedEventArgs args)
    {
        await RefreshPanelAsync(args.Player.GuildId);
    }

    private async Task OnTrackExceptionAsync(object sender, TrackExceptionEventArgs args)
    {
        _logger.LogWarning(
            "Track exception in guild {GuildId}: {TrackTitle} - {Message}",
            args.Player.GuildId,
            args.Track.Title,
            args.Exception.Message);

        await NotifyPanelChannelAsync(args.Player.GuildId, $"Không thể phát `{args.Track.Title}`. Bot sẽ bỏ qua bài này nếu có hàng đợi tiếp theo.");
        await RefreshPanelAsync(args.Player.GuildId);
    }

    private async Task<QueuedLavalinkPlayer> JoinPlayerAsync(
        ulong guildId,
        IVoiceChannel targetChannel,
        CancellationToken cancellationToken)
    {
        await EnsureLavalinkReadyAsync(cancellationToken);

        var session = GetOrCreateSession(guildId);
        var initialVolume = session.Volume;

        try
        {
            var player = await _audioService.Players.JoinAsync(
                targetChannel,
                PlayerFactory.Queued,
                new QueuedLavalinkPlayerOptions
                {
                    SelfDeaf = true,
                    InitialVolume = initialVolume,
                    ClearQueueOnStop = true,
                    EnableAutoPlay = true
                },
                cancellationToken);

            if (Math.Abs(player.Volume - initialVolume) > 0.1F)
            {
                await player.SetVolumeAsync(initialVolume, cancellationToken);
            }

            return player;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Cannot join voice channel via Lavalink. GuildId={GuildId}, VoiceChannelId={VoiceChannelId}",
                guildId,
                targetChannel.Id);

            throw new InvalidOperationException("Không thể kết nối voice/Lavalink. Hãy kiểm tra node Lavalink và thử lại.");
        }
    }

    private async Task<TrackLoadResult> LoadTracksAsync(string normalizedReference, CancellationToken cancellationToken)
    {
        try
        {
            var loadResult = await _audioService.Tracks.LoadTracksAsync(
                normalizedReference,
                loadOptions: default,
                cancellationToken: cancellationToken);
            if (!loadResult.HasMatches)
            {
                throw new InvalidOperationException("Không tìm thấy bài nào hợp lệ để phát.");
            }

            return loadResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lavalink track load failed for reference {Reference}.",
                ToSafeReference(normalizedReference));

            throw new InvalidOperationException("Không thể phân giải bài YouTube qua Lavalink. Hãy thử URL/video khác.");
        }
    }

    private async Task<IUserMessage> ResolveOrCreatePanelMessageAsync(
        SocketGuild guild,
        ITextChannel channel,
        GuildMusicSession session,
        CancellationToken cancellationToken)
    {
        if (session.PanelMessageId.HasValue)
        {
            var existing = await channel.GetMessageAsync(session.PanelMessageId.Value, CacheMode.AllowDownload, default(RequestOptions));
            if (existing is IUserMessage cachedMessage)
            {
                return cachedMessage;
            }
        }

        var existingPanel = (await channel.GetMessagesAsync(30).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == MusicPanelConstants.PanelTitle);

        if (existingPanel is not null)
        {
            session.PanelMessageId = existingPanel.Id;
            return existingPanel;
        }

        var status = GetStatus(guild.Id);
        var embed = BuildPanelEmbed(guild, status, TryGetPlayer(guild.Id, out var player) ? player : null);
        var components = BuildPanelComponents(status);
        var createdMessage = await channel.SendMessageAsync(embed: embed, components: components, options: default(RequestOptions));

        try
        {
            await createdMessage.PinAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot pin music panel message in guild {GuildId}.", guild.Id);
        }

        session.PanelMessageId = createdMessage.Id;
        return createdMessage;
    }

    private Embed BuildPanelEmbed(SocketGuild guild, MusicPlaybackStatus status, QueuedLavalinkPlayer? player)
    {
        var currentTrack = player?.CurrentTrack;
        var currentItem = player?.CurrentItem;
        var requestedTrack = currentItem as RequestedTrackQueueItem;

        var embed = new EmbedBuilder()
            .WithTitle(MusicPanelConstants.PanelTitle)
            .WithColor(status.IsPlaying ? Color.Green : status.IsConnected ? Color.Orange : Color.DarkGrey)
            .WithDescription(
                currentTrack is null
                    ? "Chưa có bài nào đang phát. Bấm `Thêm bài` hoặc dùng `/music play` để bắt đầu."
                    : $"[{currentTrack.Title}]({BuildTrackUrl(currentTrack)})");

        embed.AddField("Trạng thái", BuildPlaybackStateLabel(status), true);
        embed.AddField(
            "Kênh voice",
            status.VoiceChannelId.HasValue ? $"<#{status.VoiceChannelId.Value}>" : "Chưa kết nối",
            true);
        embed.AddField("Âm lượng", $"{status.Volume:0}%", true);

        if (currentTrack is not null)
        {
            embed.AddField("Tác giả", currentTrack.Author, true);
            embed.AddField("Độ dài", FormatDuration(currentTrack.Duration), true);
            embed.AddField(
                "Người yêu cầu",
                requestedTrack is null ? "Không rõ" : $"<@{requestedTrack.RequesterUserId}>",
                true);

            if (currentTrack.ArtworkUri is not null)
            {
                embed.WithThumbnailUrl(currentTrack.ArtworkUri.ToString());
            }
        }

        embed.AddField("Hàng đợi", BuildQueuePreview(player), false);
        embed.WithFooter($"Guild: {guild.Name}");

        return embed.Build();
    }

    private static MessageComponent BuildPanelComponents(MusicPlaybackStatus status)
    {
        var builder = new ComponentBuilder()
            .WithButton("Thêm bài", MusicPanelConstants.AddTrackButtonId, ButtonStyle.Success, row: 0)
            .WithButton(
                status.IsPaused ? "Tiếp tục" : "Tạm dừng",
                MusicPanelConstants.PauseResumeButtonId,
                ButtonStyle.Primary,
                disabled: !status.IsPlaying,
                row: 0)
            .WithButton("Bỏ qua", MusicPanelConstants.SkipButtonId, ButtonStyle.Secondary, disabled: !status.IsPlaying, row: 0)
            .WithButton("Dừng", MusicPanelConstants.StopButtonId, ButtonStyle.Danger, disabled: !status.IsConnected, row: 0)
            .WithButton("Rời kênh", MusicPanelConstants.LeaveButtonId, ButtonStyle.Danger, disabled: !status.IsConnected, row: 0)
            .WithButton("Âm lượng -", MusicPanelConstants.VolumeDownButtonId, ButtonStyle.Secondary, disabled: !status.IsConnected, row: 1)
            .WithButton("Âm lượng +", MusicPanelConstants.VolumeUpButtonId, ButtonStyle.Secondary, disabled: !status.IsConnected, row: 1)
            .WithButton("Làm mới", MusicPanelConstants.RefreshButtonId, ButtonStyle.Secondary, row: 1);

        return builder.Build();
    }

    private async Task NotifyPanelChannelAsync(ulong guildId, string text)
    {
        var guild = _discordClient.GetGuild(guildId);
        if (guild is null)
        {
            return;
        }

        var session = GetOrCreateSession(guildId);
        var channel = ResolvePanelChannel(guild, session, preferredChannel: null);
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.SendMessageAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot send music notification in guild {GuildId}.", guildId);
        }
    }

    private static string BuildPlaybackStateLabel(MusicPlaybackStatus status)
    {
        if (!status.IsConnected)
        {
            return "Chưa kết nối";
        }

        if (status.IsPaused)
        {
            return "Tạm dừng";
        }

        if (status.IsPlaying)
        {
            return "Đang phát";
        }

        return "Sẵn sàng";
    }

    private static string BuildQueuePreview(QueuedLavalinkPlayer? player)
    {
        if (player is null || player.Queue.Count == 0)
        {
            return "Hàng đợi đang trống.";
        }

        var builder = new StringBuilder();
        var previewItems = player.Queue.Take(QueuePreviewLimit).ToArray();
        for (var index = 0; index < previewItems.Length; index++)
        {
            var item = previewItems[index];
            var requestedTrack = item as RequestedTrackQueueItem;
            var title = item.Track?.Title ?? item.Identifier;

            builder.Append(index + 1)
                .Append(". ")
                .Append(title);

            if (requestedTrack is not null)
            {
                builder.Append(" • ")
                    .Append(requestedTrack.RequesterDisplayName);
            }

            if (index < previewItems.Length - 1)
            {
                builder.Append('\n');
            }
        }

        if (player.Queue.Count > previewItems.Length)
        {
            builder.Append('\n')
                .Append("... và ")
                .Append(player.Queue.Count - previewItems.Length)
                .Append(" bài nữa");
        }

        return builder.ToString();
    }

    private static string BuildTrackUrl(LavalinkTrack track)
    {
        if (track.Uri is not null)
        {
            return track.Uri.ToString();
        }

        return $"https://www.youtube.com/watch?v={track.Identifier}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "Live";
        }

        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"hh\:mm\:ss");
        }

        return duration.ToString(@"mm\:ss");
    }

    private ITextChannel? ResolvePanelChannel(SocketGuild guild, GuildMusicSession session, ITextChannel? preferredChannel)
    {
        if (session.PanelChannelId.HasValue && guild.GetTextChannel(session.PanelChannelId.Value) is ITextChannel cachedChannel)
        {
            return cachedChannel;
        }

        var dedicatedChannel = guild.TextChannels.FirstOrDefault(x =>
            x.Name.Equals(MusicPanelConstants.ChannelName, StringComparison.OrdinalIgnoreCase));
        if (dedicatedChannel is not null)
        {
            return dedicatedChannel;
        }

        return preferredChannel;
    }

    private GuildMusicSession GetOrCreateSession(ulong guildId)
    {
        return _sessions.GetOrAdd(guildId, static _ => new GuildMusicSession());
    }

    private bool TryGetPlayer(ulong guildId, out QueuedLavalinkPlayer player)
    {
        if (_audioService.Players.TryGetPlayer(guildId, out QueuedLavalinkPlayer? existingPlayer) && existingPlayer is not null)
        {
            player = existingPlayer;
            return true;
        }

        player = null!;
        return false;
    }

    private async Task EnsureLavalinkReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _audioService.WaitForReadyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lavalink is not ready.");
            throw new InvalidOperationException("Lavalink chưa sẵn sàng. Hãy kiểm tra node Lavalink trước khi phát nhạc.");
        }
    }

    private ulong? TryGetBotVoiceChannelId(ulong guildId)
    {
        var guild = _discordClient.GetGuild(guildId);
        if (guild is null || guild.CurrentUser is null)
        {
            return null;
        }

        return guild.CurrentUser.VoiceChannel?.Id;
    }

    private static string ToSafeReference(string value, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var compact = value.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        return compact.Length <= maxLength ? compact : $"{compact[..maxLength]}...";
    }

    private static string NormalizeVideoReference(string videoReference)
    {
        var trimmed = videoReference.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Hãy nhập URL hoặc video ID YouTube hợp lệ.");
        }

        if (TryExtractYouTubeVideoId(trimmed, out var videoId))
        {
            return $"https://www.youtube.com/watch?v={videoId}";
        }

        var urlMatch = UrlRegex.Match(trimmed);
        if (urlMatch.Success)
        {
            var candidateUrl = TrimTrailingUrlPunctuation(urlMatch.Value);
            if (TryExtractYouTubeVideoId(candidateUrl, out videoId))
            {
                return $"https://www.youtube.com/watch?v={videoId}";
            }
        }

        throw new InvalidOperationException("Hãy nhập URL hoặc video ID YouTube hợp lệ.");
    }

    private static string TrimTrailingUrlPunctuation(string value)
    {
        return value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '"', '\'');
    }

    private static bool TryExtractYouTubeVideoId(string value, out string videoId)
    {
        var trimmed = value.Trim();
        if (YouTubeIdRegex.IsMatch(trimmed))
        {
            videoId = trimmed;
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            videoId = string.Empty;
            return false;
        }

        return TryExtractYouTubeVideoId(absoluteUri, out videoId);
    }

    private static bool TryExtractYouTubeVideoId(Uri uri, out string videoId)
    {
        var host = uri.Host.ToLowerInvariant();

        if (host == "youtu.be")
        {
            return TryReadVideoId(uri.AbsolutePath.Trim('/'), out videoId);
        }

        if (host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(kv[0]);
                if (!key.Equals("v", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawValue = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                if (TryReadVideoId(rawValue, out videoId))
                {
                    return true;
                }
            }

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && (segments[0] is "shorts" or "embed" or "live"))
            {
                if (TryReadVideoId(segments[1], out videoId))
                {
                    return true;
                }
            }
        }

        videoId = string.Empty;
        return false;
    }

    private static bool TryReadVideoId(string rawValue, out string videoId)
    {
        var match = PathVideoIdRegex.Match(rawValue.Trim());
        if (!match.Success)
        {
            videoId = string.Empty;
            return false;
        }

        var candidate = match.Value;
        if (!YouTubeIdRegex.IsMatch(candidate))
        {
            videoId = string.Empty;
            return false;
        }

        videoId = candidate;
        return true;
    }

    private sealed class GuildMusicSession
    {
        public SemaphoreSlim SyncRoot { get; } = new(1, 1);

        public ulong? PanelChannelId { get; set; }

        public ulong? PanelMessageId { get; set; }

        public float Volume { get; set; } = DefaultVolume;
    }

    private sealed record RequestedTrackQueueItem : TrackQueueItem
    {
        public RequestedTrackQueueItem(LavalinkTrack track, ulong requesterUserId, string requesterDisplayName)
            : base(track)
        {
            RequesterUserId = requesterUserId;
            RequesterDisplayName = requesterDisplayName;
        }

        public ulong RequesterUserId { get; }

        public string RequesterDisplayName { get; }
    }
}

public sealed record MusicPlayResult(
    string Title,
    string VideoUrl,
    ulong VoiceChannelId,
    int AddedCount,
    int QueuePosition,
    bool StartedImmediately);

public sealed record MusicPlaybackStatus(
    bool IsConnected,
    bool IsPlaying,
    bool IsPaused,
    string? CurrentTitle,
    ulong? VoiceChannelId,
    int QueueCount,
    float Volume,
    ulong? PanelChannelId);

public sealed record MusicPanelResult(
    ulong ChannelId,
    ulong MessageId);
