using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.DiscordNet;
using Lavalink4NET.Events.Players;
using Lavalink4NET.Players;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Options;
using ProjectManagerBot.Options;

namespace ProjectManagerBot.Services;

public sealed class YouTubeMusicService
{
    private const float DefaultVolume = 1F;
    private const int HistoryLimit = 10;
    private const int QueuePreviewLimit = 5;
    private const int RecentPreviewLimit = 5;
    private static readonly TimeSpan PendingPlaylistChoiceLifetime = TimeSpan.FromMinutes(10);
    private static readonly IEmote AddTrackButtonEmote = Emote.Parse(MusicPanelConstants.MusicEmoji);
    private static readonly IEmote PauseButtonEmote = Emote.Parse(MusicPanelConstants.PauseEmoji);
    private static readonly IEmote RefreshButtonEmote = Emote.Parse(MusicPanelConstants.EqualizerEmoji);

    private static readonly Regex YouTubeIdRegex = new("^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PathVideoIdRegex = new(@"^[a-zA-Z0-9_-]{11}", RegexOptions.Compiled);

    private readonly IAudioService _audioService;
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<YouTubeMusicService> _logger;
    private readonly ConcurrentDictionary<ulong, GuildMusicSession> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingPlaylistChoice> _pendingPlaylistChoices = new();
    private readonly HttpClient _lavalinkHttpClient;

    public YouTubeMusicService(
        IAudioService audioService,
        DiscordSocketClient discordClient,
        ILogger<YouTubeMusicService> logger,
        IOptions<LavalinkOptions> lavalinkOptions)
    {
        _audioService = audioService;
        _discordClient = discordClient;
        _logger = logger;
        var options = lavalinkOptions.Value;
        _lavalinkHttpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseAddress),
        };

        _lavalinkHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", options.Passphrase);
        _audioService.TrackStarted += OnTrackStartedAsync;
        _audioService.TrackEnded += OnTrackEndedAsync;
        _audioService.TrackException += OnTrackExceptionAsync;
    }

    public async Task<MusicPlayResult> PlayAsync(
        ulong guildId,
        IVoiceChannel targetChannel,
        string videoReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoReference))
        {
            throw new InvalidOperationException("Hãy nhập URL hoặc video ID YouTube hợp lệ.");
        }

        var normalizedReference = NormalizeVideoReference(videoReference);
        var session = GetOrCreateSession(guildId);

        _logger.LogDebug(
            "PlayAsync started for guild {GuildId}. VoiceChannelId={VoiceChannelId}, VideoReference={VideoReference}",
            guildId,
            targetChannel.Id,
            ToSafeReference(normalizedReference));

        var player = await JoinPlayerAsync(guildId, targetChannel, cancellationToken);

        MusicTrackEntry? entryToPlay = null;
        MusicTrackEntry? queuedEntry = null;
        var queuePosition = 0;

        lock (session.StateGate)
        {
            if (HasActivePlayback(player, session))
            {
                queuedEntry = new MusicTrackEntry(normalizedReference);
                session.Queue.Add(queuedEntry);
                queuePosition = session.Queue.Count;
            }
            else
            {
                entryToPlay = new MusicTrackEntry(normalizedReference);
                session.CurrentTrack = entryToPlay;
                session.ForwardTracks.Clear();
            }
        }

        if (queuedEntry is not null)
        {
            await RefreshPanelAsync(guildId, cancellationToken);

            return new MusicPlayResult(
                Title: queuedEntry.Title,
                VideoUrl: queuedEntry.VideoUrl,
                VoiceChannelId: targetChannel.Id,
                AddedToQueue: true,
                QueuePosition: queuePosition);
        }

        try
        {
            await player.PlayAsync(normalizedReference, cancellationToken: cancellationToken);
            entryToPlay!.UpdateFromTrack(player.CurrentTrack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (session.StateGate)
            {
                if (ReferenceEquals(session.CurrentTrack, entryToPlay))
                {
                    session.CurrentTrack = null;
                }
            }

            _logger.LogWarning(
                ex,
                "Lavalink playback failed. GuildId={GuildId}, VideoReference={VideoReference}",
                guildId,
                ToSafeReference(normalizedReference));

            throw new InvalidOperationException("Không thể phát video YouTube qua Lavalink. Hãy thử URL/video khác.");
        }

        await RefreshPanelAsync(guildId, cancellationToken);

        _logger.LogInformation(
            "Playback started via Lavalink for guild {GuildId}: {TrackTitle} ({VideoUrl}) in voice channel {VoiceChannelId}.",
            guildId,
            entryToPlay!.Title,
            entryToPlay.VideoUrl,
            targetChannel.Id);

        return new MusicPlayResult(
            Title: entryToPlay.Title,
            VideoUrl: entryToPlay.VideoUrl,
            VoiceChannelId: targetChannel.Id,
            AddedToQueue: false,
            QueuePosition: 0);
    }

    public MusicPlaylistChoicePrompt? CreatePlaylistChoicePrompt(ulong guildId, ulong userId, string videoReference)
    {
        CleanupExpiredPlaylistChoices();

        if (!TryCreatePlaylistChoiceRequest(videoReference, out var request))
        {
            return null;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        _pendingPlaylistChoices[token] = new PendingPlaylistChoice(
            guildId,
            userId,
            request.CurrentTrackReference,
            request.PlaylistReference,
            request.SelectedIndex,
            DateTimeOffset.UtcNow);

        return new MusicPlaylistChoicePrompt(token, request.SelectedIndex);
    }

    public bool CancelPlaylistChoice(ulong guildId, ulong userId, string token)
    {
        if (!TryGetValidPlaylistChoice(token, out var pendingChoice) ||
            pendingChoice.GuildId != guildId ||
            pendingChoice.UserId != userId)
        {
            return false;
        }

        return _pendingPlaylistChoices.TryRemove(token, out _);
    }

    public async Task<MusicPlayResult> PlayPendingCurrentTrackAsync(
        ulong guildId,
        ulong userId,
        string token,
        IVoiceChannel targetChannel,
        CancellationToken cancellationToken = default)
    {
        var pendingChoice = TakePendingPlaylistChoice(guildId, userId, token);
        return await PlayAsync(guildId, targetChannel, pendingChoice.CurrentTrackReference, cancellationToken);
    }

    public async Task<MusicPlaylistPlayResult> PlayPendingPlaylistAsync(
        ulong guildId,
        ulong userId,
        string token,
        IVoiceChannel targetChannel,
        CancellationToken cancellationToken = default)
    {
        var pendingChoice = TakePendingPlaylistChoice(guildId, userId, token);
        return await PlayPlaylistAsync(
            guildId,
            targetChannel,
            pendingChoice.PlaylistReference,
            pendingChoice.SelectedIndex,
            cancellationToken);
    }

    public async Task<MusicPlaylistPlayResult> PlayPlaylistAsync(
        ulong guildId,
        IVoiceChannel targetChannel,
        string playlistReference,
        int? selectedIndex = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistReference))
        {
            throw new InvalidOperationException("Hãy nhập URL playlist YouTube hợp lệ.");
        }

        var playlistLoad = await LoadPlaylistEntriesAsync(playlistReference, selectedIndex, cancellationToken);
        if (playlistLoad.Tracks.Count == 0)
        {
            throw new InvalidOperationException("Không tìm thấy bài nào hợp lệ trong playlist này.");
        }

        var session = GetOrCreateSession(guildId);
        var player = await JoinPlayerAsync(guildId, targetChannel, cancellationToken);
        MusicTrackEntry? entryToPlay = null;
        var queuePosition = 0;
        var queuedTailEntries = new List<MusicTrackEntry>();

        lock (session.StateGate)
        {
            if (HasActivePlayback(player, session))
            {
                queuePosition = session.Queue.Count + 1;
                session.Queue.AddRange(playlistLoad.Tracks);
            }
            else
            {
                entryToPlay = playlistLoad.Tracks[0];
                session.CurrentTrack = entryToPlay;
                session.ForwardTracks.Clear();

                if (playlistLoad.Tracks.Count > 1)
                {
                    queuedTailEntries.AddRange(playlistLoad.Tracks.Skip(1));
                    session.Queue.AddRange(queuedTailEntries);
                }
            }
        }

        if (entryToPlay is null)
        {
            await RefreshPanelAsync(guildId, cancellationToken);
            return new MusicPlaylistPlayResult(
                PlaylistName: playlistLoad.PlaylistName,
                FirstTrackTitle: playlistLoad.Tracks[0].Title,
                VoiceChannelId: targetChannel.Id,
                AddedToQueue: true,
                QueuePosition: queuePosition,
                TrackCount: playlistLoad.Tracks.Count,
                StartIndex: playlistLoad.StartIndex + 1);
        }

        try
        {
            await player.PlayAsync(entryToPlay.Reference, cancellationToken: cancellationToken);
            entryToPlay.UpdateFromTrack(player.CurrentTrack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (session.StateGate)
            {
                if (ReferenceEquals(session.CurrentTrack, entryToPlay))
                {
                    session.CurrentTrack = null;
                }

                foreach (var queuedTailEntry in queuedTailEntries)
                {
                    session.Queue.Remove(queuedTailEntry);
                }
            }

            _logger.LogWarning(
                ex,
                "Lavalink playlist playback failed. GuildId={GuildId}, PlaylistReference={PlaylistReference}",
                guildId,
                ToSafeReference(playlistReference));

            throw new InvalidOperationException("Không thể phát playlist YouTube này qua Lavalink. Hãy thử playlist khác.");
        }

        await RefreshPanelAsync(guildId, cancellationToken);

        return new MusicPlaylistPlayResult(
            PlaylistName: playlistLoad.PlaylistName,
            FirstTrackTitle: entryToPlay.Title,
            VoiceChannelId: targetChannel.Id,
            AddedToQueue: false,
            QueuePosition: 0,
            TrackCount: playlistLoad.Tracks.Count,
            StartIndex: playlistLoad.StartIndex + 1);
    }

    public async Task<MusicTrackResult?> PreviousAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return null;
        }

        var session = GetOrCreateSession(guildId);
        MusicTrackEntry? targetEntry;
        MusicTrackEntry? currentEntry;

        lock (session.StateGate)
        {
            targetEntry = PopLast(session.PreviousTracks);
            if (targetEntry is null)
            {
                return null;
            }

            currentEntry = session.CurrentTrack;
            if (currentEntry is not null)
            {
                PushHistory(session.ForwardTracks, currentEntry);
            }

            session.CurrentTrack = targetEntry;
            session.SuppressTrackEndAdvance = true;
        }

        try
        {
            await player.PlayAsync(targetEntry.Reference, cancellationToken: cancellationToken);
            targetEntry.UpdateFromTrack(player.CurrentTrack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (session.StateGate)
            {
                session.SuppressTrackEndAdvance = false;
                session.CurrentTrack = currentEntry;

                if (currentEntry is not null && IsLastEntry(session.ForwardTracks, currentEntry))
                {
                    session.ForwardTracks.RemoveAt(session.ForwardTracks.Count - 1);
                }

                if (targetEntry is not null)
                {
                    PushHistory(session.PreviousTracks, targetEntry);
                }
            }

            _logger.LogWarning(ex, "Cannot move to previous track for guild {GuildId}", guildId);
            throw new InvalidOperationException("Không thể quay lại bài trước lúc này.");
        }

        await RefreshPanelAsync(guildId, cancellationToken);
        return new MusicTrackResult(targetEntry.Title, targetEntry.VideoUrl, player.VoiceChannelId);
    }

    public async Task<MusicTrackResult?> NextAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return null;
        }

        var session = GetOrCreateSession(guildId);
        MusicTrackEntry? targetEntry;
        MusicTrackEntry? currentEntry;
        var poppedFromForward = false;

        lock (session.StateGate)
        {
            if (session.ForwardTracks.Count > 0)
            {
                targetEntry = PopLast(session.ForwardTracks);
                poppedFromForward = true;
            }
            else if (session.Queue.Count > 0)
            {
                targetEntry = DequeueFirst(session.Queue);
            }
            else
            {
                return null;
            }

            currentEntry = session.CurrentTrack;
            if (currentEntry is not null)
            {
                PushHistory(session.PreviousTracks, currentEntry);
            }

            session.CurrentTrack = targetEntry;
            session.SuppressTrackEndAdvance = true;
        }

        try
        {
            await player.PlayAsync(targetEntry!.Reference, cancellationToken: cancellationToken);
            targetEntry.UpdateFromTrack(player.CurrentTrack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (session.StateGate)
            {
                session.SuppressTrackEndAdvance = false;
                session.CurrentTrack = currentEntry;

                if (currentEntry is not null && IsLastEntry(session.PreviousTracks, currentEntry))
                {
                    session.PreviousTracks.RemoveAt(session.PreviousTracks.Count - 1);
                }

                if (targetEntry is not null)
                {
                    if (poppedFromForward)
                    {
                        PushHistory(session.ForwardTracks, targetEntry);
                    }
                    else
                    {
                        session.Queue.Insert(0, targetEntry);
                    }
                }
            }

            _logger.LogWarning(ex, "Cannot move to next track for guild {GuildId}", guildId);
            throw new InvalidOperationException("Không thể chuyển sang bài tiếp theo lúc này.");
        }

        await RefreshPanelAsync(guildId, cancellationToken);
        return new MusicTrackResult(targetEntry!.Title, targetEntry.VideoUrl, player.VoiceChannelId);
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
        return await NextAsync(guildId, cancellationToken) is not null;
    }

    public async Task<bool> StopAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        var session = GetOrCreateSession(guildId);
        var hadPlayback = false;

        lock (session.StateGate)
        {
            hadPlayback = player.State is PlayerState.Playing or PlayerState.Paused || player.CurrentTrack is not null || session.CurrentTrack is not null;

            if (session.CurrentTrack is not null)
            {
                PushHistory(session.PreviousTracks, session.CurrentTrack);
                session.CurrentTrack = null;
            }

            session.Queue.Clear();
            session.ForwardTracks.Clear();
            session.SuppressTrackEndAdvance = true;
        }

        try
        {
            await player.StopAsync(cancellationToken);
            await RefreshPanelAsync(guildId, cancellationToken);
            return hadPlayback;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (session.StateGate)
            {
                session.SuppressTrackEndAdvance = false;
            }

            _logger.LogWarning(ex, "Cannot stop Lavalink playback for guild {GuildId}", guildId);
            throw new InvalidOperationException("Không thể dừng phát nhạc lúc này.");
        }
    }

    public async Task<bool> LeaveAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        var session = GetOrCreateSession(guildId);
        lock (session.StateGate)
        {
            if (session.CurrentTrack is not null)
            {
                PushHistory(session.PreviousTracks, session.CurrentTrack);
            }

            session.CurrentTrack = null;
            session.Queue.Clear();
            session.ForwardTracks.Clear();
            session.SuppressTrackEndAdvance = true;
        }

        try
        {
            await player.DisconnectAsync(cancellationToken);
            await RefreshPanelAsync(guildId, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (session.StateGate)
            {
                session.SuppressTrackEndAdvance = false;
            }

            _logger.LogWarning(ex, "Cannot disconnect Lavalink player for guild {GuildId}", guildId);
            throw new InvalidOperationException("Không thể rời voice channel lúc này.");
        }
    }

    public MusicPlaybackStatus GetStatus(ulong guildId)
    {
        var session = GetOrCreateSession(guildId);
        var discordVoiceChannelId = TryGetBotVoiceChannelId(guildId);
        string? currentTitle;
        ulong? panelChannelId;
        int queueCount;
        int recentCount;
        bool hasPrevious;
        bool hasNext;

        lock (session.StateGate)
        {
            currentTitle = session.CurrentTrack?.Title;
            panelChannelId = session.PanelChannelId;
            queueCount = session.Queue.Count;
            recentCount = session.PreviousTracks.Count;
            hasPrevious = session.PreviousTracks.Count > 0;
            hasNext = session.ForwardTracks.Count > 0 || session.Queue.Count > 0;
        }

        if (!TryGetPlayer(guildId, out var player))
        {
            return new MusicPlaybackStatus(
                IsConnected: discordVoiceChannelId.HasValue,
                IsPlaying: false,
                IsPaused: false,
                CurrentTitle: currentTitle,
                VoiceChannelId: discordVoiceChannelId,
                PanelChannelId: panelChannelId,
                QueueCount: queueCount,
                RecentCount: recentCount,
                HasPrevious: hasPrevious,
                HasNext: hasNext);
        }

        var isConnected = player.ConnectionState.IsConnected || discordVoiceChannelId.HasValue;
        var playerTrack = player.CurrentTrack;
        var hasTrack = playerTrack is not null || currentTitle is not null;
        var isPaused = player.State is PlayerState.Paused;
        var isPlaying = player.State is PlayerState.Playing or PlayerState.Paused || (isConnected && hasTrack);
        var voiceChannelId = player.ConnectionState.IsConnected ? player.VoiceChannelId : discordVoiceChannelId;

        return new MusicPlaybackStatus(
            IsConnected: isConnected,
            IsPlaying: isPlaying,
            IsPaused: isPaused,
            CurrentTitle: playerTrack?.Title ?? currentTitle,
            VoiceChannelId: voiceChannelId,
            PanelChannelId: panelChannelId,
            QueueCount: queueCount,
            RecentCount: recentCount,
            HasPrevious: hasPrevious,
            HasNext: hasNext);
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

        lock (session.StateGate)
        {
            session.PanelChannelId = channel.Id;
        }

        await session.PanelSyncRoot.WaitAsync(cancellationToken);
        try
        {
            var message = await ResolveOrCreatePanelMessageAsync(guild, channel, session);
            var status = GetStatus(guild.Id);
            var player = TryGetPlayer(guild.Id, out var existingPlayer) ? existingPlayer : null;
            var embed = BuildPanelEmbed(guild, status, player, session);
            var components = BuildPanelComponents(status);

            await message.ModifyAsync(props =>
            {
                props.Embed = embed;
                props.Components = components;
            });

            lock (session.StateGate)
            {
                session.PanelMessageId = message.Id;
            }

            return new MusicPanelResult(channel.Id, message.Id);
        }
        finally
        {
            session.PanelSyncRoot.Release();
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
        var session = GetOrCreateSession(args.Player.GuildId);

        lock (session.StateGate)
        {
            session.CurrentTrack ??= new MusicTrackEntry(BuildTrackUrl(args.Track));
            session.CurrentTrack.UpdateFromTrack(args.Track);
        }

        await RefreshPanelAsync(args.Player.GuildId);
    }

    private async Task OnTrackEndedAsync(object sender, TrackEndedEventArgs args)
    {
        var player = GetPlayerForGuild(args.Player.GuildId);
        if (player is null)
        {
            await RefreshPanelAsync(args.Player.GuildId);
            return;
        }

        await AdvanceAfterTrackCompletionAsync(args.Player.GuildId, player, CancellationToken.None);
    }

    private async Task OnTrackExceptionAsync(object sender, TrackExceptionEventArgs args)
    {
        var trackTitle = args.Track?.Title ?? "bài hiện tại";

        _logger.LogWarning(
            "Track exception in guild {GuildId}: {TrackTitle} - {Message}",
            args.Player.GuildId,
            trackTitle,
            args.Exception.Message);

        await NotifyPanelChannelAsync(args.Player.GuildId, $"Không thể phát `{trackTitle}`. Bot sẽ thử bài tiếp theo nếu có.");
        await RefreshPanelAsync(args.Player.GuildId);
    }

    private async Task AdvanceAfterTrackCompletionAsync(ulong guildId, LavalinkPlayer player, CancellationToken cancellationToken)
    {
        var session = GetOrCreateSession(guildId);
        MusicTrackEntry? nextEntry;

        lock (session.StateGate)
        {
            if (session.SuppressTrackEndAdvance)
            {
                session.SuppressTrackEndAdvance = false;
                return;
            }

            if (session.CurrentTrack is not null)
            {
                PushHistory(session.PreviousTracks, session.CurrentTrack);
                session.CurrentTrack = null;
            }

            nextEntry = DequeueNextCandidate(session);
            if (nextEntry is not null)
            {
                session.CurrentTrack = nextEntry;
            }
        }

        if (nextEntry is null)
        {
            await RefreshPanelAsync(guildId, cancellationToken);
            return;
        }

        try
        {
            await player.PlayAsync(nextEntry.Reference, cancellationToken: cancellationToken);
            nextEntry.UpdateFromTrack(player.CurrentTrack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (session.StateGate)
            {
                if (ReferenceEquals(session.CurrentTrack, nextEntry))
                {
                    session.CurrentTrack = null;
                }
            }

            _logger.LogWarning(ex, "Cannot autoplay next track for guild {GuildId}", guildId);
            await NotifyPanelChannelAsync(guildId, $"Không thể phát `{nextEntry.Title}`. Bot sẽ thử bài kế tiếp.");
            await AdvanceAfterTrackCompletionAsync(guildId, player, cancellationToken);
            return;
        }

        await RefreshPanelAsync(guildId, cancellationToken);
    }

    private async Task<LavalinkPlayer> JoinPlayerAsync(
        ulong guildId,
        IVoiceChannel targetChannel,
        CancellationToken cancellationToken)
    {
        await EnsureLavalinkReadyAsync(cancellationToken);

        var session = GetOrCreateSession(guildId);

        try
        {
            var player = await _audioService.Players.JoinAsync(targetChannel, cancellationToken);

            float targetVolume;
            lock (session.StateGate)
            {
                targetVolume = session.Volume;
            }

            if (Math.Abs(player.Volume - targetVolume) > 0.1F)
            {
                await player.SetVolumeAsync(targetVolume, cancellationToken);
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

    private async Task<IUserMessage> ResolveOrCreatePanelMessageAsync(
        SocketGuild guild,
        ITextChannel channel,
        GuildMusicSession session)
    {
        ulong? panelMessageId;
        lock (session.StateGate)
        {
            panelMessageId = session.PanelMessageId;
        }

        if (panelMessageId.HasValue)
        {
            var existing = await channel.GetMessageAsync(panelMessageId.Value, CacheMode.AllowDownload, default(RequestOptions));
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
            lock (session.StateGate)
            {
                session.PanelMessageId = existingPanel.Id;
            }

            return existingPanel;
        }

        var status = GetStatus(guild.Id);
        var player = TryGetPlayer(guild.Id, out var existingPlayer) ? existingPlayer : null;
        var embed = BuildPanelEmbed(guild, status, player, session);
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

        lock (session.StateGate)
        {
            session.PanelMessageId = createdMessage.Id;
        }

        return createdMessage;
    }

    private Embed BuildPanelEmbed(SocketGuild guild, MusicPlaybackStatus status, LavalinkPlayer? player, GuildMusicSession session)
    {
        var currentTrack = player?.CurrentTrack;
        var currentTitle = currentTrack?.Title;
        var currentUrl = currentTrack?.Uri?.ToString();
        string upcomingPreview;
        string recentPreview;

        lock (session.StateGate)
        {
            if (string.IsNullOrWhiteSpace(currentTitle))
            {
                currentTitle = session.CurrentTrack?.Title;
                currentUrl = session.CurrentTrack?.VideoUrl;
            }

            upcomingPreview = BuildUpcomingPreview(session);
            recentPreview = BuildRecentPreview(session);
        }

        var embed = new EmbedBuilder()
            .WithTitle(MusicPanelConstants.PanelTitle)
            .WithColor(GetPanelColor(status))
            .WithAuthor(BuildPanelHeader(status), GetPanelStatusImageUrl(status))
            .WithDescription(BuildPanelDescription(status, currentTitle, currentUrl));

        embed.AddField($"{MusicPanelConstants.MusicEmoji} Trạng thái", BuildPlaybackStateLabel(status), true);
        embed.AddField(
            $"{MusicPanelConstants.HeadphonesEmoji} Kênh voice",
            status.VoiceChannelId.HasValue ? $"<#{status.VoiceChannelId.Value}>" : "Chưa kết nối",
            true);

        if (currentTrack is not null)
        {
            embed.AddField("🎤 Tác giả", currentTrack.Author, true);
            embed.AddField($"{MusicPanelConstants.TimerEmoji} Thời lượng", FormatDuration(currentTrack.Duration), true);

            if (currentTrack.ArtworkUri is not null)
            {
                embed.WithThumbnailUrl(currentTrack.ArtworkUri.ToString());
            }
        }
        else
        {
            embed.WithThumbnailUrl(GetPanelStatusImageUrl(status));
        }

        embed.AddField($"{MusicPanelConstants.VinylEmoji} Tiếp theo", upcomingPreview, false);
        embed.AddField($"{MusicPanelConstants.EqualizerEmoji} 10 bài gần nhất", recentPreview, false);
        embed.WithFooter($"{guild.Name} • /music play để thêm bài");

        return embed.Build();
    }

    private static MessageComponent BuildPanelComponents(MusicPlaybackStatus status)
    {
        var builder = new ComponentBuilder()
            .WithButton("Thêm bài", MusicPanelConstants.AddTrackButtonId, ButtonStyle.Success, emote: AddTrackButtonEmote, row: 0)
            .WithButton("⏮️ Lùi", MusicPanelConstants.PreviousButtonId, ButtonStyle.Secondary, row: 0, disabled: !status.HasPrevious)
            .WithButton(
                status.IsPaused ? "Tiếp tục" : "Tạm dừng",
                MusicPanelConstants.PauseResumeButtonId,
                ButtonStyle.Primary,
                emote: PauseButtonEmote,
                disabled: !status.IsPlaying,
                row: 0)
            .WithButton("⏭️ Tiếp", MusicPanelConstants.SkipButtonId, ButtonStyle.Secondary, row: 0, disabled: !status.HasNext)
            .WithButton("⏹️ Dừng", MusicPanelConstants.StopButtonId, ButtonStyle.Danger, row: 0, disabled: !status.IsConnected)
            .WithButton("🎧 Rời kênh", MusicPanelConstants.LeaveButtonId, ButtonStyle.Secondary, row: 1, disabled: !status.IsConnected)
            .WithButton("Làm mới", MusicPanelConstants.RefreshButtonId, ButtonStyle.Secondary, emote: RefreshButtonEmote, row: 1);

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
            return $"{MusicPanelConstants.TimerEmoji} Chưa kết nối";
        }

        if (status.IsPaused)
        {
            return $"{MusicPanelConstants.PauseEmoji} Tạm dừng";
        }

        if (status.IsPlaying)
        {
            return $"{MusicPanelConstants.EqualizerEmoji} Đang phát";
        }

        return $"{MusicPanelConstants.HeadphonesEmoji} Sẵn sàng";
    }

    private static string BuildUpcomingPreview(GuildMusicSession session)
    {
        var upcomingEntries = session.ForwardTracks.AsEnumerable().Reverse()
            .Concat(session.Queue)
            .Take(QueuePreviewLimit)
            .ToArray();

        if (upcomingEntries.Length == 0)
        {
            return "Hàng đợi đang trống.";
        }

        var builder = new StringBuilder();
        for (var index = 0; index < upcomingEntries.Length; index++)
        {
            builder.Append(index + 1)
                .Append(". ")
                .Append(upcomingEntries[index].Title);

            if (index < upcomingEntries.Length - 1)
            {
                builder.Append('\n');
            }
        }

        var remaining = session.ForwardTracks.Count + session.Queue.Count - upcomingEntries.Length;
        if (remaining > 0)
        {
            builder.Append('\n')
                .Append("... và ")
                .Append(remaining)
                .Append(" bài nữa");
        }

        return builder.ToString();
    }

    private static string BuildRecentPreview(GuildMusicSession session)
    {
        if (session.PreviousTracks.Count == 0)
        {
            return "Chưa có lịch sử gần đây.";
        }

        var recentEntries = session.PreviousTracks.TakeLast(RecentPreviewLimit).Reverse().ToArray();
        var builder = new StringBuilder();

        for (var index = 0; index < recentEntries.Length; index++)
        {
            builder.Append(index + 1)
                .Append(". ")
                .Append(recentEntries[index].Title);

            if (index < recentEntries.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static Color GetPanelColor(MusicPlaybackStatus status)
    {
        if (status.IsPaused)
        {
            return new Color(0xD79B2E);
        }

        if (status.IsPlaying)
        {
            return new Color(0x1E8E6E);
        }

        if (status.IsConnected)
        {
            return new Color(0x3A6EA5);
        }

        return new Color(0x4F5D75);
    }

    private static string BuildPanelHeader(MusicPlaybackStatus status)
    {
        if (status.IsPaused)
        {
            return "Deck đang tạm dừng";
        }

        if (status.IsPlaying)
        {
            return "Deck đang phát";
        }

        if (status.IsConnected)
        {
            return "Deck đã sẵn sàng";
        }

        return "Deck đang chờ bài mới";
    }

    private static string BuildPanelDescription(MusicPlaybackStatus status, string? currentTitle, string? currentUrl)
    {
        if (string.IsNullOrWhiteSpace(currentTitle))
        {
            return "**Máy nghe nhạc đang chờ bài mới.**\nBấm `Thêm bài` hoặc dùng `/music play` để bắt đầu.";
        }

        var stateText = status.IsPaused
            ? "Đang tạm dừng"
            : status.IsPlaying
                ? "Đang phát"
                : "Sẵn sàng";

        return $"{stateText}\n**[{currentTitle}]({currentUrl ?? "https://www.youtube.com"})**";
    }

    private static string GetPanelStatusImageUrl(MusicPlaybackStatus status)
    {
        if (status.IsPaused)
        {
            return MusicPanelConstants.PauseEmojiUrl;
        }

        if (status.IsPlaying)
        {
            return MusicPanelConstants.VinylEmojiUrl;
        }

        if (status.IsConnected)
        {
            return MusicPanelConstants.HeadphonesEmojiUrl;
        }

        return MusicPanelConstants.TimerEmojiUrl;
    }

    private static void PushHistory(List<MusicTrackEntry> list, MusicTrackEntry entry)
    {
        if (list.Count >= HistoryLimit)
        {
            list.RemoveAt(0);
        }

        list.Add(entry);
    }

    private static MusicTrackEntry? PopLast(List<MusicTrackEntry> list)
    {
        if (list.Count == 0)
        {
            return null;
        }

        var lastIndex = list.Count - 1;
        var entry = list[lastIndex];
        list.RemoveAt(lastIndex);
        return entry;
    }

    private static MusicTrackEntry DequeueFirst(List<MusicTrackEntry> list)
    {
        var entry = list[0];
        list.RemoveAt(0);
        return entry;
    }

    private static MusicTrackEntry? DequeueNextCandidate(GuildMusicSession session)
    {
        if (session.ForwardTracks.Count > 0)
        {
            return PopLast(session.ForwardTracks);
        }

        if (session.Queue.Count > 0)
        {
            return DequeueFirst(session.Queue);
        }

        return null;
    }

    private static bool HasActivePlayback(LavalinkPlayer player, GuildMusicSession session)
    {
        return player.State is PlayerState.Playing or PlayerState.Paused ||
               player.CurrentTrack is not null ||
               session.CurrentTrack is not null;
    }

    private static bool IsLastEntry(List<MusicTrackEntry> list, MusicTrackEntry entry)
    {
        return list.Count > 0 && ReferenceEquals(list[^1], entry);
    }

    private ITextChannel? ResolvePanelChannel(SocketGuild guild, GuildMusicSession session, ITextChannel? preferredChannel)
    {
        ulong? panelChannelId;
        lock (session.StateGate)
        {
            panelChannelId = session.PanelChannelId;
        }

        if (panelChannelId.HasValue && guild.GetTextChannel(panelChannelId.Value) is ITextChannel cachedChannel)
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

    private LavalinkPlayer? GetPlayerForGuild(ulong guildId)
    {
        return _audioService.Players.TryGetPlayer(guildId, out LavalinkPlayer? existingPlayer)
            ? existingPlayer
            : null;
    }

    private GuildMusicSession GetOrCreateSession(ulong guildId)
    {
        return _sessions.GetOrAdd(guildId, static _ => new GuildMusicSession());
    }

    private bool TryGetPlayer(ulong guildId, out LavalinkPlayer player)
    {
        if (_audioService.Players.TryGetPlayer(guildId, out LavalinkPlayer? existingPlayer) && existingPlayer is not null)
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

    private static string BuildPendingTitle(string reference)
    {
        return TryExtractYouTubeVideoId(reference, out var videoId)
            ? $"YouTube {videoId}"
            : ToSafeReference(reference, 60);
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

    private async Task<PlaylistLoadResult> LoadPlaylistEntriesAsync(
        string playlistReference,
        int? preferredIndex,
        CancellationToken cancellationToken)
    {
        using var response = await _lavalinkHttpClient.GetAsync(
            $"v4/loadtracks?identifier={Uri.EscapeDataString(playlistReference)}",
            cancellationToken);

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Không thể tải playlist từ Lavalink lúc này.");
        }

        var root = payload.RootElement;
        var loadType = root.TryGetProperty("loadType", out var loadTypeElement)
            ? loadTypeElement.GetString()
            : null;

        if (!string.Equals(loadType, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(loadType, "error", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("data", out var errorElement) &&
                errorElement.TryGetProperty("message", out var messageElement))
            {
                throw new InvalidOperationException(messageElement.GetString() ?? "Không thể tải playlist từ Lavalink.");
            }

            throw new InvalidOperationException("URL này không được Lavalink nhận là playlist YouTube hợp lệ.");
        }

        if (!root.TryGetProperty("data", out var dataElement))
        {
            throw new InvalidOperationException("Lavalink không trả về dữ liệu playlist hợp lệ.");
        }

        var playlistName = "Playlist YouTube";
        var selectedTrack = -1;

        if (dataElement.TryGetProperty("info", out var infoElement))
        {
            if (infoElement.TryGetProperty("name", out var playlistNameElement) &&
                !string.IsNullOrWhiteSpace(playlistNameElement.GetString()))
            {
                playlistName = playlistNameElement.GetString()!;
            }

            if (infoElement.TryGetProperty("selectedTrack", out var selectedTrackElement) &&
                selectedTrackElement.ValueKind is JsonValueKind.Number &&
                selectedTrackElement.TryGetInt32(out var resolvedSelectedTrack))
            {
                selectedTrack = resolvedSelectedTrack;
            }
        }

        if (!dataElement.TryGetProperty("tracks", out var tracksElement) ||
            tracksElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException("Playlist này không có track hợp lệ để phát.");
        }

        var tracks = new List<MusicTrackEntry>();
        foreach (var trackElement in tracksElement.EnumerateArray())
        {
            if (!trackElement.TryGetProperty("info", out var trackInfo) ||
                !trackInfo.TryGetProperty("uri", out var uriElement))
            {
                continue;
            }

            var rawUri = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(rawUri))
            {
                continue;
            }

            string normalizedReference;
            try
            {
                normalizedReference = NormalizeVideoReference(rawUri);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var entry = new MusicTrackEntry(normalizedReference);
            var title = trackInfo.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString()
                : null;

            entry.UpdateFromMetadata(title, rawUri);
            tracks.Add(entry);
        }

        if (tracks.Count == 0)
        {
            throw new InvalidOperationException("Không tìm thấy bài nào hợp lệ trong playlist này.");
        }

        var resolvedStartIndex = ResolvePlaylistStartIndex(selectedTrack, preferredIndex, tracks.Count);
        if (resolvedStartIndex > 0)
        {
            tracks = tracks.Skip(resolvedStartIndex).ToList();
        }

        return new PlaylistLoadResult(playlistName, tracks, resolvedStartIndex);
    }

    private static int ResolvePlaylistStartIndex(int selectedTrack, int? preferredIndex, int trackCount)
    {
        if (selectedTrack >= 0 && selectedTrack < trackCount)
        {
            return selectedTrack;
        }

        if (preferredIndex.HasValue)
        {
            var preferredZeroBasedIndex = Math.Max(0, preferredIndex.Value - 1);
            if (preferredZeroBasedIndex < trackCount)
            {
                return preferredZeroBasedIndex;
            }
        }

        return 0;
    }

    private void CleanupExpiredPlaylistChoices()
    {
        var expirationThreshold = DateTimeOffset.UtcNow - PendingPlaylistChoiceLifetime;
        foreach (var pendingChoice in _pendingPlaylistChoices)
        {
            if (pendingChoice.Value.CreatedAtUtc < expirationThreshold)
            {
                _pendingPlaylistChoices.TryRemove(pendingChoice.Key, out _);
            }
        }
    }

    private PendingPlaylistChoice TakePendingPlaylistChoice(ulong guildId, ulong userId, string token)
    {
        CleanupExpiredPlaylistChoices();

        if (!_pendingPlaylistChoices.TryRemove(token, out var pendingChoice))
        {
            throw new InvalidOperationException("Lựa chọn playlist đã hết hạn hoặc không còn hợp lệ.");
        }

        if (pendingChoice.GuildId != guildId || pendingChoice.UserId != userId)
        {
            throw new InvalidOperationException("Bạn không thể dùng lựa chọn playlist của người khác.");
        }

        return pendingChoice;
    }

    private bool TryGetValidPlaylistChoice(string token, out PendingPlaylistChoice pendingChoice)
    {
        CleanupExpiredPlaylistChoices();

        if (_pendingPlaylistChoices.TryGetValue(token, out pendingChoice!))
        {
            return true;
        }

        pendingChoice = default!;
        return false;
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

    private static bool TryCreatePlaylistChoiceRequest(string videoReference, out PlaylistChoiceRequest request)
    {
        var trimmed = videoReference.Trim();
        if (trimmed.Length == 0)
        {
            request = default!;
            return false;
        }

        var urlMatch = UrlRegex.Match(trimmed);
        if (!urlMatch.Success)
        {
            request = default!;
            return false;
        }

        var candidateUrl = TrimTrailingUrlPunctuation(urlMatch.Value);
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var absoluteUri))
        {
            request = default!;
            return false;
        }

        if (!TryExtractYouTubePlaylistId(absoluteUri, out var playlistId) ||
            !TryExtractYouTubeVideoId(absoluteUri, out var videoId))
        {
            request = default!;
            return false;
        }

        var selectedIndex = TryReadPlaylistIndex(absoluteUri);
        var playlistReference = $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(playlistId)}";

        request = new PlaylistChoiceRequest(
            CurrentTrackReference: $"https://www.youtube.com/watch?v={videoId}",
            PlaylistReference: playlistReference,
            SelectedIndex: selectedIndex);

        return true;
    }

    private static string TrimTrailingUrlPunctuation(string value)
    {
        return value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '"', '\'');
    }

    private static bool TryExtractYouTubePlaylistId(Uri uri, out string playlistId)
    {
        if (!IsYouTubeHost(uri.Host))
        {
            playlistId = string.Empty;
            return false;
        }

        return TryReadQueryValue(uri, "list", out playlistId) && !string.IsNullOrWhiteSpace(playlistId);
    }

    private static int? TryReadPlaylistIndex(Uri uri)
    {
        if (!TryReadQueryValue(uri, "index", out var rawIndex))
        {
            return null;
        }

        return int.TryParse(rawIndex, out var index) && index > 0 ? index : null;
    }

    private static bool TryReadQueryValue(Uri uri, string key, out string value)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0)
            {
                continue;
            }

            var currentKey = Uri.UnescapeDataString(kv[0]);
            if (!currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
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

        if (IsYouTubeHost(host))
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

    private static bool IsYouTubeHost(string host)
    {
        var normalizedHost = host.ToLowerInvariant();
        return normalizedHost == "youtube.com" || normalizedHost.EndsWith(".youtube.com", StringComparison.Ordinal);
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
        public object StateGate { get; } = new();

        public SemaphoreSlim PanelSyncRoot { get; } = new(1, 1);

        public ulong? PanelChannelId { get; set; }

        public ulong? PanelMessageId { get; set; }

        public float Volume { get; set; } = DefaultVolume;


        public MusicTrackEntry? CurrentTrack { get; set; }

        public List<MusicTrackEntry> Queue { get; } = new();

        public List<MusicTrackEntry> PreviousTracks { get; } = new();

        public List<MusicTrackEntry> ForwardTracks { get; } = new();

        public bool SuppressTrackEndAdvance { get; set; }
    }

    private sealed class MusicTrackEntry
    {
        public MusicTrackEntry(string reference)
        {
            Reference = reference;
            Title = BuildPendingTitle(reference);
            VideoUrl = reference;
        }

        public string Reference { get; }

        public string Title { get; private set; }

        public string VideoUrl { get; private set; }

        public void UpdateFromMetadata(string? title, string? videoUrl)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                Title = title;
            }

            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                VideoUrl = videoUrl;
            }
        }

        public void UpdateFromTrack(LavalinkTrack? track)
        {
            if (track is null)
            {
                return;
            }

            Title = string.IsNullOrWhiteSpace(track.Title) ? Title : track.Title;
            VideoUrl = track.Uri?.ToString() ?? Reference;
        }
    }

    private sealed record PlaylistChoiceRequest(
        string CurrentTrackReference,
        string PlaylistReference,
        int? SelectedIndex);

    private sealed record PendingPlaylistChoice(
        ulong GuildId,
        ulong UserId,
        string CurrentTrackReference,
        string PlaylistReference,
        int? SelectedIndex,
        DateTimeOffset CreatedAtUtc);

    private sealed record PlaylistLoadResult(
        string PlaylistName,
        List<MusicTrackEntry> Tracks,
        int StartIndex);
}

public sealed record MusicPlayResult(
    string Title,
    string VideoUrl,
    ulong VoiceChannelId,
    bool AddedToQueue,
    int QueuePosition);

public sealed record MusicPlaylistChoicePrompt(
    string Token,
    int? SelectedIndex);

public sealed record MusicPlaylistPlayResult(
    string PlaylistName,
    string FirstTrackTitle,
    ulong VoiceChannelId,
    bool AddedToQueue,
    int QueuePosition,
    int TrackCount,
    int StartIndex);

public sealed record MusicTrackResult(
    string Title,
    string VideoUrl,
    ulong? VoiceChannelId);

public sealed record MusicPlaybackStatus(
    bool IsConnected,
    bool IsPlaying,
    bool IsPaused,
    string? CurrentTitle,
    ulong? VoiceChannelId,
    ulong? PanelChannelId,
    int QueueCount,
    int RecentCount,
    bool HasPrevious,
    bool HasNext);

public sealed record MusicPanelResult(
    ulong ChannelId,
    ulong MessageId);
