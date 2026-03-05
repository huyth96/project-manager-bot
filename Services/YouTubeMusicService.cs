using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.DiscordNet;
using Lavalink4NET.Players;

namespace ProjectManagerBot.Services;

public sealed class YouTubeMusicService(
    IAudioService audioService,
    DiscordSocketClient discordClient,
    ILogger<YouTubeMusicService> logger)
{
    private static readonly Regex YouTubeIdRegex = new("^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PathVideoIdRegex = new(@"^[a-zA-Z0-9_-]{11}", RegexOptions.Compiled);

    private readonly IAudioService _audioService = audioService;
    private readonly DiscordSocketClient _discordClient = discordClient;
    private readonly ILogger<YouTubeMusicService> _logger = logger;

    public async Task<MusicPlayResult> PlayAsync(
        ulong guildId,
        IVoiceChannel targetChannel,
        string videoReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoReference))
        {
            throw new InvalidOperationException("Hay nhap URL hoac video ID YouTube hop le.");
        }

        var normalizedReference = NormalizeVideoReference(videoReference);
        _logger.LogDebug(
            "PlayAsync started for guild {GuildId}. VoiceChannelId={VoiceChannelId}, VideoReference={VideoReference}",
            guildId,
            targetChannel.Id,
            ToSafeReference(normalizedReference));

        await EnsureLavalinkReadyAsync(cancellationToken);

        LavalinkPlayer player;
        try
        {
            player = await _audioService.Players.JoinAsync(targetChannel, cancellationToken);
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

            throw new InvalidOperationException("Khong the ket noi voice/Lavalink. Hay kiem tra node Lavalink va thu lai.");
        }

        try
        {
            await player.PlayAsync(normalizedReference, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lavalink playback failed. GuildId={GuildId}, VideoReference={VideoReference}",
                guildId,
                ToSafeReference(normalizedReference));

            throw new InvalidOperationException("Khong the phat video YouTube qua Lavalink. Hay thu URL/video khac.");
        }

        var track = player.CurrentTrack;
        var title = track?.Title ?? normalizedReference;
        var videoUrl = track?.Uri?.ToString() ?? normalizedReference;

        _logger.LogInformation(
            "Playback started via Lavalink for guild {GuildId}: {TrackTitle} ({VideoUrl}) in voice channel {VoiceChannelId}.",
            guildId,
            title,
            videoUrl,
            targetChannel.Id);

        return new MusicPlayResult(
            Title: title,
            VideoUrl: videoUrl,
            VoiceChannelId: targetChannel.Id);
    }

    public async Task<bool> StopAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        var hadPlayback = player.State is PlayerState.Playing or PlayerState.Paused;

        try
        {
            await player.StopAsync(cancellationToken);
            return hadPlayback;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot stop Lavalink playback for guild {GuildId}", guildId);
            throw new InvalidOperationException("Khong the dung phat nhac luc nay.");
        }
    }

    public async Task<bool> LeaveAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!TryGetPlayer(guildId, out var player))
        {
            return false;
        }

        try
        {
            await player.DisconnectAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot disconnect Lavalink player for guild {GuildId}", guildId);
            throw new InvalidOperationException("Khong the roi voice channel luc nay.");
        }
    }

    public MusicPlaybackStatus GetStatus(ulong guildId)
    {
        var discordVoiceChannelId = TryGetBotVoiceChannelId(guildId);

        if (!TryGetPlayer(guildId, out var player))
        {
            return new MusicPlaybackStatus(
                IsConnected: discordVoiceChannelId.HasValue,
                IsPlaying: false,
                CurrentTitle: null,
                VoiceChannelId: discordVoiceChannelId);
        }

        var isConnected = player.ConnectionState.IsConnected || discordVoiceChannelId.HasValue;
        var hasTrack = player.CurrentTrack is not null;
        var isPlaying = player.State is PlayerState.Playing or PlayerState.Paused || (isConnected && hasTrack);
        var voiceChannelId = player.ConnectionState.IsConnected ? player.VoiceChannelId : discordVoiceChannelId;

        return new MusicPlaybackStatus(
            IsConnected: isConnected,
            IsPlaying: isPlaying,
            CurrentTitle: player.CurrentTrack?.Title,
            VoiceChannelId: voiceChannelId);
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
            throw new InvalidOperationException("Lavalink chua san sang. Hay kiem tra node Lavalink truoc khi phat nhac.");
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
            throw new InvalidOperationException("Hay nhap URL hoac video ID YouTube hop le.");
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

        throw new InvalidOperationException("Hay nhap URL hoac video ID YouTube hop le.");
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
}

public sealed record MusicPlayResult(
    string Title,
    string VideoUrl,
    ulong VoiceChannelId);

public sealed record MusicPlaybackStatus(
    bool IsConnected,
    bool IsPlaying,
    string? CurrentTitle,
    ulong? VoiceChannelId);
