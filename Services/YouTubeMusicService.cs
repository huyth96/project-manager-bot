using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using Discord;
using Discord.Audio;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace ProjectManagerBot.Services;

public sealed class YouTubeMusicService(ILogger<YouTubeMusicService> logger)
{
    private readonly YoutubeClient _youtubeClient = new();
    private readonly ILogger<YouTubeMusicService> _logger = logger;
    private readonly ConcurrentDictionary<ulong, GuildMusicSession> _sessions = new();

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

        var trimmedReference = videoReference.Trim();
        _logger.LogDebug(
            "PlayAsync started for guild {GuildId}. VoiceChannelId={VoiceChannelId}, VideoReference={VideoReference}",
            guildId,
            targetChannel.Id,
            ToSafeReference(trimmedReference));

        var resolvedTrack = await ResolveTrackAsync(trimmedReference);
        _logger.LogDebug(
            "Resolved track for guild {GuildId}: Title={TrackTitle}, VideoUrl={VideoUrl}",
            guildId,
            resolvedTrack.Title,
            resolvedTrack.VideoUrl);
        var session = _sessions.GetOrAdd(guildId, _ => new GuildMusicSession());

        await session.CommandGate.WaitAsync(cancellationToken);
        try
        {
            var detachedPlayback = DetachPlayback(session);
            if (detachedPlayback.HasPlayback)
            {
                _logger.LogDebug("Existing playback detected for guild {GuildId}. Cancelling previous playback first.", guildId);
            }

            await CancelPlaybackAsync(detachedPlayback);

            var audioClient = await EnsureConnectedAsync(session, targetChannel);
            var ffmpeg = StartFfmpeg(resolvedTrack.StreamUrl);

            var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (session.StateGate)
            {
                session.PlaybackCts = playbackCts;
                session.CurrentTitle = resolvedTrack.Title;
                session.CurrentUrl = resolvedTrack.VideoUrl;
            }

            var playbackTask = Task.Run(
                () => RunPlaybackAsync(
                    guildId,
                    session,
                    audioClient,
                    resolvedTrack.Title,
                    resolvedTrack.VideoUrl,
                    ffmpeg,
                    playbackCts),
                CancellationToken.None);

            lock (session.StateGate)
            {
                if (ReferenceEquals(session.PlaybackCts, playbackCts))
                {
                    session.ActivePlaybackTask = playbackTask;
                }
            }

            _logger.LogInformation(
                "Playback task started for guild {GuildId}: {TrackTitle} ({VideoUrl}) in voice channel {VoiceChannelId}.",
                guildId,
                resolvedTrack.Title,
                resolvedTrack.VideoUrl,
                targetChannel.Id);

            return new MusicPlayResult(
                Title: resolvedTrack.Title,
                VideoUrl: resolvedTrack.VideoUrl,
                VoiceChannelId: targetChannel.Id);
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    public async Task<bool> StopAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(guildId, out var session))
        {
            return false;
        }

        await session.CommandGate.WaitAsync(cancellationToken);
        try
        {
            var detachedPlayback = DetachPlayback(session);
            await CancelPlaybackAsync(detachedPlayback);
            return detachedPlayback.HasPlayback;
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    public async Task<bool> LeaveAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(guildId, out var session))
        {
            return false;
        }

        await session.CommandGate.WaitAsync(cancellationToken);
        try
        {
            var detachedPlayback = DetachPlayback(session);
            var audioClient = DetachAudioClient(session);

            await CancelPlaybackAsync(detachedPlayback);

            if (audioClient is not null)
            {
                await SafeStopAudioClientAsync(audioClient);
            }

            _sessions.TryRemove(guildId, out _);
            return audioClient is not null;
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    public MusicPlaybackStatus GetStatus(ulong guildId)
    {
        if (!_sessions.TryGetValue(guildId, out var session))
        {
            return new MusicPlaybackStatus(false, false, null, null);
        }

        lock (session.StateGate)
        {
            var isConnected = session.AudioClient is not null;
            var isPlaying = session.PlaybackCts is not null && session.ActivePlaybackTask is { IsCompleted: false };

            return new MusicPlaybackStatus(
                IsConnected: isConnected,
                IsPlaying: isPlaying,
                CurrentTitle: session.CurrentTitle,
                VoiceChannelId: session.VoiceChannelId);
        }
    }

    private async Task<ResolvedTrack> ResolveTrackAsync(string videoReference)
    {
        _logger.LogDebug("Resolving YouTube video reference: {VideoReference}", ToSafeReference(videoReference));

        try
        {
            var video = await _youtubeClient.Videos.GetAsync(videoReference);
            _logger.LogDebug("Fetched video metadata. VideoId={VideoId}, Title={TrackTitle}", video.Id, video.Title);

            var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(video.Id);
            var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (audioStream is null)
            {
                throw new InvalidOperationException("Video này không có luồng audio phù hợp để phát.");
            }

            return new ResolvedTrack(
                Title: video.Title,
                VideoUrl: $"https://www.youtube.com/watch?v={video.Id}",
                StreamUrl: audioStream.Url);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                ex,
                "Invalid YouTube reference format. VideoReference={VideoReference}",
                ToSafeReference(videoReference));

            throw new InvalidOperationException("Không thể đọc video YouTube. Hãy dùng URL hoặc video ID hợp lệ.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Network/HTTP error while resolving YouTube reference. VideoReference={VideoReference}",
                ToSafeReference(videoReference));

            throw new InvalidOperationException("Không thể truy cập YouTube lúc này. Hãy thử lại sau.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Timeout while resolving YouTube reference. VideoReference={VideoReference}",
                ToSafeReference(videoReference));

            throw new InvalidOperationException("Hết thời gian chờ khi truy cập YouTube. Hãy thử lại.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unexpected error while resolving YouTube reference. ExceptionType={ExceptionType}, VideoReference={VideoReference}",
                ex.GetType().FullName,
                ToSafeReference(videoReference));

            throw new InvalidOperationException("Không thể đọc video YouTube. Hãy dùng URL hoặc video ID hợp lệ.");
        }
    }

    private async Task<IAudioClient> EnsureConnectedAsync(GuildMusicSession session, IVoiceChannel targetChannel)
    {
        IAudioClient? currentClient;
        IAudioClient? clientToStop = null;

        lock (session.StateGate)
        {
            currentClient = session.AudioClient;
            if (currentClient is not null && session.VoiceChannelId != targetChannel.Id)
            {
                clientToStop = currentClient;
                session.AudioClient = null;
                session.VoiceChannelId = null;
                currentClient = null;
            }
        }

        if (clientToStop is not null)
        {
            _logger.LogDebug("Switching voice channel to {VoiceChannelId}. Stopping previous audio client first.", targetChannel.Id);
            await SafeStopAudioClientAsync(clientToStop);
        }

        if (currentClient is not null)
        {
            _logger.LogDebug("Reusing existing audio client for voice channel {VoiceChannelId}.", targetChannel.Id);
            return currentClient;
        }

        _logger.LogDebug("Connecting bot to voice channel {VoiceChannelId}.", targetChannel.Id);
        var connectedClient = await targetChannel.ConnectAsync(selfDeaf: true);
        lock (session.StateGate)
        {
            session.AudioClient = connectedClient;
            session.VoiceChannelId = targetChannel.Id;
        }

        return connectedClient;
    }

    private static DetachedPlayback DetachPlayback(GuildMusicSession session)
    {
        lock (session.StateGate)
        {
            var detached = new DetachedPlayback(
                PlaybackCts: session.PlaybackCts,
                PlaybackTask: session.ActivePlaybackTask);

            session.PlaybackCts = null;
            session.ActivePlaybackTask = null;
            session.CurrentTitle = null;
            session.CurrentUrl = null;

            return detached;
        }
    }

    private static IAudioClient? DetachAudioClient(GuildMusicSession session)
    {
        lock (session.StateGate)
        {
            var audioClient = session.AudioClient;
            session.AudioClient = null;
            session.VoiceChannelId = null;
            return audioClient;
        }
    }

    private static async Task CancelPlaybackAsync(DetachedPlayback detachedPlayback)
    {
        detachedPlayback.PlaybackCts?.Cancel();

        if (detachedPlayback.PlaybackTask is null)
        {
            return;
        }

        try
        {
            await detachedPlayback.PlaybackTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunPlaybackAsync(
        ulong guildId,
        GuildMusicSession session,
        IAudioClient audioClient,
        string trackTitle,
        string videoUrl,
        Process ffmpeg,
        CancellationTokenSource playbackCts)
    {
        var token = playbackCts.Token;
        var wasCancelled = false;
        string? standardError = null;
        var standardErrorTask = ffmpeg.StandardError.ReadToEndAsync();

        _logger.LogDebug(
            "RunPlaybackAsync started for guild {GuildId}: TrackTitle={TrackTitle}, VideoUrl={VideoUrl}",
            guildId,
            trackTitle,
            videoUrl);

        try
        {
            using var discordStream = audioClient.CreatePCMStream(AudioApplication.Music);
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(discordStream, 81920, token);
            await discordStream.FlushAsync(token);
            await ffmpeg.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            wasCancelled = true;
            _logger.LogDebug("Playback cancelled for guild {GuildId}: {TrackTitle}", guildId, trackTitle);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi phát nhạc YouTube cho guild {GuildId}: {TrackTitle}", guildId, trackTitle);
        }
        finally
        {
            if (!ffmpeg.HasExited)
            {
                TryKillProcess(ffmpeg);
            }

            try
            {
                await ffmpeg.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
            }

            try
            {
                standardError = await standardErrorTask;
            }
            catch
            {
            }

            if (!wasCancelled && ffmpeg.ExitCode != 0)
            {
                _logger.LogWarning(
                    "FFmpeg exited with code {ExitCode} while playing {TrackTitle} ({VideoUrl}). stderr: {StandardError}",
                    ffmpeg.ExitCode,
                    trackTitle,
                    videoUrl,
                    string.IsNullOrWhiteSpace(standardError) ? "<empty>" : TruncateForLog(standardError));
            }
            else
            {
                _logger.LogDebug(
                    "RunPlaybackAsync finished for guild {GuildId}: ExitCode={ExitCode}, WasCancelled={WasCancelled}, TrackTitle={TrackTitle}",
                    guildId,
                    ffmpeg.ExitCode,
                    wasCancelled,
                    trackTitle);
            }

            lock (session.StateGate)
            {
                if (ReferenceEquals(session.PlaybackCts, playbackCts))
                {
                    session.PlaybackCts = null;
                    session.ActivePlaybackTask = null;
                    session.CurrentTitle = null;
                    session.CurrentUrl = null;
                }
            }

            playbackCts.Dispose();
            ffmpeg.Dispose();
        }
    }

    private Process StartFfmpeg(string inputUrl)
    {
        var executable = ResolveFfmpegExecutable();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-reconnect");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-reconnect_streamed");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-reconnect_delay_max");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputUrl);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            _logger.LogDebug("Starting FFmpeg. Executable={Executable}, InputUrl={InputUrl}", executable, ToSafeReference(inputUrl));
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Không thể khởi chạy FFmpeg để phát nhạc.");
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "FFmpeg not found at current configuration.");
            throw new InvalidOperationException(
                "Không tìm thấy FFmpeg. Hãy cài FFmpeg vào PATH hoặc đặt biến môi trường FFMPEG_PATH.");
        }
    }

    private async Task SafeStopAudioClientAsync(IAudioClient audioClient)
    {
        try
        {
            await audioClient.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Không thể ngắt voice client sạch sẽ");
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string ResolveFfmpegExecutable()
    {
        var configuredPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        return string.IsNullOrWhiteSpace(configuredPath) ? "ffmpeg" : configuredPath.Trim();
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

    private static string TruncateForLog(string value, int maxLength = 600)
        => value.Length <= maxLength ? value : $"{value[..maxLength]}...";

    private sealed class GuildMusicSession
    {
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
        public object StateGate { get; } = new();
        public IAudioClient? AudioClient { get; set; }
        public ulong? VoiceChannelId { get; set; }
        public CancellationTokenSource? PlaybackCts { get; set; }
        public Task? ActivePlaybackTask { get; set; }
        public string? CurrentTitle { get; set; }
        public string? CurrentUrl { get; set; }
    }

    private sealed record ResolvedTrack(
        string Title,
        string VideoUrl,
        string StreamUrl);

    private sealed record DetachedPlayback(
        CancellationTokenSource? PlaybackCts,
        Task? PlaybackTask)
    {
        public bool HasPlayback => PlaybackCts is not null || PlaybackTask is not null;
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
