using System.Globalization;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;
using ProjectManagerBot.Options;

namespace ProjectManagerBot.Services;

public sealed class ProjectMemoryService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    DiscordSocketClient client,
    ProjectService projectService,
    StudioTimeService studioTime,
    IOptions<AssistantOptions> options,
    ILogger<ProjectMemoryService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> StopWords =
    [
        "ai", "anh", "bao", "ban", "banh", "banh?", "bao", "baoao", "bao_cao", "ban", "bieu", "bot", "can", "cho",
        "chua", "co", "cua", "da", "dang", "day", "de", "du", "dua", "duoc", "giai", "gi", "gio", "giu",
        "hello", "help", "hien", "hom", "hoi", "hoi", "khong", "khi", "la", "lam", "lien", "luc", "minh",
        "mot", "nay", "neu", "nguoi", "ngay", "nhom", "nho", "noi", "project", "roi", "sao", "sprint",
        "sau", "team", "the", "theo", "thi", "thoi", "thong", "toi", "tren", "trong", "tuan", "va", "van",
        "ve", "viec", "voi", "what", "who", "why", "with"
    ];
    private static readonly string[] ImportantTerms =
    [
        "blocker", "bug", "deadline", "deploy", "error", "fix", "issue", "keo", "ket", "loi", "overdue",
        "risk", "stuck", "task", "thiet ke", "tre", "ui", "api", "auth", "login", "payment", "merge"
    ];

    private const int MaxStoredContentLength = 1800;
    private const int MaxKeywordCount = 6;
    private const int MaxChannelCount = 3;
    private const int MaxHighlightCount = 3;
    private const int MaxRelevantCandidates = 2000;

    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly DiscordSocketClient _client = client;
    private readonly ProjectService _projectService = projectService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly AssistantOptions _options = options.Value;
    private readonly ILogger<ProjectMemoryService> _logger = logger;

    public async Task ArchiveMessageAsync(SocketMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ShouldArchive(message) || !IsGuildTextOrThread(message.Channel))
            {
                return;
            }

            var parentChannelId = message.Channel is SocketThreadChannel threadChannel
                ? threadChannel.ParentChannel?.Id
                : null;

            var project = await _projectService.GetProjectByChannelHierarchyAsync(
                message.Channel.Id,
                parentChannelId,
                cancellationToken);

            if (project is null)
            {
                return;
            }

            var content = BuildArchiveContent(message);
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var createdAtLocal = TimeZoneInfo.ConvertTime(message.Timestamp, _studioTime.TimeZone);
            var (channelId, channelName, threadId, threadName) = ResolveChannelInfo(message.Channel);

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var alreadyArchived = await db.ProjectMemoryMessages
                .AsNoTracking()
                .AnyAsync(x => x.MessageId == message.Id, cancellationToken);

            if (alreadyArchived)
            {
                return;
            }

            db.ProjectMemoryMessages.Add(new ProjectMemoryMessage
            {
                ProjectId = project.Id,
                MessageId = message.Id,
                ChannelId = channelId,
                ChannelName = channelName,
                ThreadId = threadId,
                ThreadName = threadName,
                AuthorId = message.Author.Id,
                AuthorName = GetAuthorDisplayName(message.Author),
                IsBot = message.Author.IsBot,
                CreatedAtUtc = message.Timestamp.UtcDateTime,
                LocalDate = createdAtLocal.Date,
                Content = content,
                NormalizedContent = NormalizeText(content)
            });

            var staleDigest = await db.ProjectDailyDigests.FirstOrDefaultAsync(
                x => x.ProjectId == project.Id && x.LocalDate == createdAtLocal.Date,
                cancellationToken);

            if (staleDigest is not null)
            {
                db.ProjectDailyDigests.Remove(staleDigest);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể archive message {MessageId} vào memory", message.Id);
        }
    }

    public async Task<AssistantProjectMemory> BuildMemoryAsync(
        int projectId,
        ulong currentChannelId,
        ulong? currentThreadId,
        ulong excludedMessageId,
        string? question,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var digestDays = Math.Clamp(_options.MemoryDigestDays, 1, 14);
        var digestFromDate = _studioTime.LocalDate.AddDays(-(digestDays - 1));
        await EnsureDailyDigestsAsync(db, projectId, digestFromDate, _studioTime.LocalDate, cancellationToken);

        var digests = await db.ProjectDailyDigests
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= digestFromDate)
            .OrderByDescending(x => x.LocalDate)
            .Take(Math.Max(1, _options.MaxDailyMemoryDigests))
            .ToListAsync(cancellationToken);

        var stats = await db.ProjectMemoryMessages
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                OldestDate = group.Min(x => x.LocalDate),
                LatestDate = group.Max(x => x.LocalDate)
            })
            .FirstOrDefaultAsync(cancellationToken);

        var relevantMessages = await LoadRelevantMessagesAsync(
            db,
            projectId,
            currentChannelId,
            currentThreadId,
            excludedMessageId,
            question,
            cancellationToken);

        return new AssistantProjectMemory(
            ArchivedMessageCount: stats?.Count ?? 0,
            OldestLocalDate: stats?.OldestDate,
            LatestLocalDate: stats?.LatestDate,
            DailyDigests: digests
                .Select(x => new AssistantDailyMemoryDigest(
                    Date: x.LocalDate,
                    MessageCount: x.MessageCount,
                    DistinctAuthorCount: x.DistinctAuthorCount,
                    UserMessageCount: x.UserMessageCount,
                    BotMessageCount: x.BotMessageCount,
                    StandupReportCount: x.StandupReportCount,
                    BlockerCount: x.BlockerCount,
                    Summary: x.Summary,
                    TopKeywords: DeserializeStringList(x.KeywordsJson),
                    ActiveChannels: DeserializeStringList(x.ActiveChannelsJson),
                    Highlights: DeserializeStringList(x.HighlightsJson)))
                .ToList(),
            RelevantMessages: relevantMessages);
    }

    private async Task EnsureDailyDigestsAsync(
        BotDbContext db,
        int projectId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var existingDates = await db.ProjectDailyDigests
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate && x.LocalDate <= toDate)
            .Select(x => x.LocalDate)
            .ToListAsync(cancellationToken);

        var missingDates = EnumerateDates(fromDate, toDate)
            .Where(x => !existingDates.Contains(x))
            .ToList();

        if (missingDates.Count == 0)
        {
            return;
        }

        var archivedMessages = await db.ProjectMemoryMessages
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate && x.LocalDate <= toDate)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var standupReports = await db.StandupReports
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate && x.LocalDate <= toDate)
            .ToListAsync(cancellationToken);

        var messagesByDate = archivedMessages
            .GroupBy(x => x.LocalDate)
            .ToDictionary(x => x.Key, x => x.ToList());

        var standupsByDate = standupReports
            .GroupBy(x => x.LocalDate)
            .ToDictionary(x => x.Key, x => x.ToList());

        foreach (var date in missingDates)
        {
            messagesByDate.TryGetValue(date, out var messagesForDate);
            standupsByDate.TryGetValue(date, out var standupsForDate);

            messagesForDate ??= [];
            standupsForDate ??= [];
            if (messagesForDate.Count == 0 && standupsForDate.Count == 0)
            {
                continue;
            }

            db.ProjectDailyDigests.Add(BuildDailyDigest(projectId, date, messagesForDate, standupsForDate));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<AssistantHistoricalMessage>> LoadRelevantMessagesAsync(
        BotDbContext db,
        int projectId,
        ulong currentChannelId,
        ulong? currentThreadId,
        ulong excludedMessageId,
        string? question,
        CancellationToken cancellationToken)
    {
        var lookbackDays = Math.Clamp(_options.MemoryLookbackDays, 7, 120);
        var fromDate = _studioTime.LocalDate.AddDays(-(lookbackDays - 1));
        var searchTerms = ExtractSearchTerms(question);

        var candidates = await db.ProjectMemoryMessages
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate && x.MessageId != excludedMessageId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(MaxRelevantCandidates)
            .ToListAsync(cancellationToken);

        var scoredMessages = candidates
            .Select(x => new
            {
                Message = x,
                Score = ScoreRelevantMessage(x, searchTerms, currentChannelId, currentThreadId)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Message.CreatedAtUtc)
            .Take(Math.Max(1, _options.MaxRelevantMemoryMessages))
            .Select(x => new AssistantHistoricalMessage(
                MessageId: x.Message.MessageId,
                TimestampLocal: TimeZoneInfo.ConvertTime(x.Message.CreatedAtUtc, _studioTime.TimeZone),
                ChannelName: x.Message.ChannelName,
                ThreadName: x.Message.ThreadName,
                AuthorId: x.Message.AuthorId,
                AuthorName: x.Message.AuthorName,
                IsBot: x.Message.IsBot,
                Content: x.Message.Content,
                RelevanceScore: x.Score))
            .ToList();

        if (scoredMessages.Count > 0)
        {
            return scoredMessages;
        }

        return candidates
            .Where(x => !x.IsBot)
            .Take(Math.Max(1, _options.MaxRelevantMemoryMessages))
            .Select(x => new AssistantHistoricalMessage(
                MessageId: x.MessageId,
                TimestampLocal: TimeZoneInfo.ConvertTime(x.CreatedAtUtc, _studioTime.TimeZone),
                ChannelName: x.ChannelName,
                ThreadName: x.ThreadName,
                AuthorId: x.AuthorId,
                AuthorName: x.AuthorName,
                IsBot: x.IsBot,
                Content: x.Content,
                RelevanceScore: 1))
            .ToList();
    }

    private ProjectDailyDigest BuildDailyDigest(
        int projectId,
        DateTime localDate,
        IReadOnlyList<ProjectMemoryMessage> messages,
        IReadOnlyList<StandupReport> standups)
    {
        var userMessages = messages.Where(x => !x.IsBot).ToList();
        var blockerCount = standups.Count(x => HasMeaningfulBlockers(x.Blockers));

        var keywordSources = userMessages
            .Select(x => x.NormalizedContent)
            .Concat(standups.Select(BuildStandupKeywordText))
            .ToList();

        var topKeywords = ExtractTopKeywords(keywordSources, MaxKeywordCount);
        var activeChannels = messages
            .GroupBy(BuildChannelLabel)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxChannelCount)
            .Select(x => x.Key)
            .ToList();

        var highlights = SelectDailyHighlights(messages, standups);
        var summary = BuildDigestSummary(localDate, messages, standups, activeChannels, topKeywords, blockerCount);

        return new ProjectDailyDigest
        {
            ProjectId = projectId,
            LocalDate = localDate,
            MessageCount = messages.Count,
            DistinctAuthorCount = messages.Select(x => x.AuthorId).Distinct().Count(),
            UserMessageCount = userMessages.Count,
            BotMessageCount = messages.Count - userMessages.Count,
            StandupReportCount = standups.Count,
            BlockerCount = blockerCount,
            Summary = TrimTo(summary, 1800),
            KeywordsJson = JsonSerializer.Serialize(topKeywords, JsonOptions),
            ActiveChannelsJson = JsonSerializer.Serialize(activeChannels, JsonOptions),
            HighlightsJson = JsonSerializer.Serialize(highlights, JsonOptions),
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private List<string> SelectDailyHighlights(
        IReadOnlyList<ProjectMemoryMessage> messages,
        IReadOnlyList<StandupReport> standups)
    {
        var highlights = messages
            .Select(x => new
            {
                Text = $"{x.AuthorName} ở {BuildChannelLabel(x)}: {TrimTo(x.Content, 140)}",
                Score = ScoreHighlightMessage(x)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Text.Length)
            .Select(x => x.Text)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxHighlightCount)
            .ToList();

        if (highlights.Count < MaxHighlightCount)
        {
            var standupHighlights = standups
                .Where(x => HasMeaningfulBlockers(x.Blockers))
                .Select(x => $"Standup <@{x.DiscordUserId}>: {TrimTo(x.Blockers, 140)}")
                .Distinct(StringComparer.Ordinal)
                .Take(MaxHighlightCount - highlights.Count);

            highlights.AddRange(standupHighlights);
        }

        return highlights;
    }

    private string BuildDigestSummary(
        DateTime localDate,
        IReadOnlyList<ProjectMemoryMessage> messages,
        IReadOnlyList<StandupReport> standups,
        IReadOnlyList<string> activeChannels,
        IReadOnlyList<string> topKeywords,
        int blockerCount)
    {
        var builder = new StringBuilder();
        builder.Append($"Ngày {localDate:yyyy-MM-dd}: có {messages.Count} tin nhắn từ {messages.Select(x => x.AuthorId).Distinct().Count()} người.");

        if (activeChannels.Count > 0)
        {
            builder.Append(" Hoạt động nhiều ở ");
            builder.Append(string.Join(", ", activeChannels));
            builder.Append('.');
        }

        if (topKeywords.Count > 0)
        {
            builder.Append(" Chủ đề nổi bật: ");
            builder.Append(string.Join(", ", topKeywords));
            builder.Append('.');
        }

        if (standups.Count > 0)
        {
            builder.Append($" Có {standups.Count} báo cáo standup");
            if (blockerCount > 0)
            {
                builder.Append($", trong đó {blockerCount} báo cáo có blocker");
            }

            builder.Append('.');
        }

        return builder.ToString();
    }

    private int ScoreRelevantMessage(
        ProjectMemoryMessage message,
        IReadOnlyCollection<string> searchTerms,
        ulong currentChannelId,
        ulong? currentThreadId)
    {
        var score = 0;
        var hasSearchTerms = searchTerms.Count > 0;

        if (hasSearchTerms)
        {
            foreach (var term in searchTerms)
            {
                if (message.NormalizedContent.Contains(term, StringComparison.Ordinal))
                {
                    score += term.Length >= 6 ? 4 : 3;
                }
            }

            if (score == 0 && message.ChannelId != currentChannelId && message.ThreadId != currentThreadId)
            {
                return 0;
            }
        }

        if (currentThreadId.HasValue && message.ThreadId == currentThreadId)
        {
            score += 4;
        }
        else if (message.ChannelId == currentChannelId)
        {
            score += 2;
        }

        if (!message.IsBot)
        {
            score += 1;
        }

        if (ContainsImportantTerm(message.NormalizedContent))
        {
            score += 1;
        }

        var ageDays = (_studioTime.LocalDate - message.LocalDate.Date).Days;
        score += ageDays switch
        {
            <= 1 => 2,
            <= 7 => 1,
            _ => 0
        };

        return score;
    }

    private static string BuildArchiveContent(SocketMessage message)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            parts.Add(message.Content.Trim());
        }

        if (message.Attachments.Count > 0)
        {
            var attachmentSummary = string.Join(
                ", ",
                message.Attachments.Select(x => x.Filename).Where(x => !string.IsNullOrWhiteSpace(x)).Take(3));

            if (!string.IsNullOrWhiteSpace(attachmentSummary))
            {
                parts.Add($"Attachments: {attachmentSummary}");
            }
        }

        foreach (var embed in message.Embeds.Take(2))
        {
            if (!string.IsNullOrWhiteSpace(embed.Title))
            {
                parts.Add($"EmbedTitle: {embed.Title.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(embed.Description))
            {
                parts.Add($"EmbedDescription: {embed.Description.Trim()}");
            }

            foreach (var field in embed.Fields.Take(4))
            {
                if (!string.IsNullOrWhiteSpace(field.Name) || !string.IsNullOrWhiteSpace(field.Value))
                {
                    parts.Add($"EmbedField: {field.Name?.Trim()} {field.Value?.Trim()}".Trim());
                }
            }
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var combined = TrimTo(string.Join(" | ", parts), MaxStoredContentLength);
        if (message.Author.IsBot &&
            NormalizeText(combined).Contains("bao cao dieu phoi hang ngay", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return combined;
    }

    private bool ShouldArchive(SocketMessage message)
    {
        return message.Source == MessageSource.User ||
               (_client.CurrentUser is not null && message.Author.Id == _client.CurrentUser.Id);
    }

    private static bool IsGuildTextOrThread(ISocketMessageChannel channel)
    {
        return channel is SocketTextChannel or SocketThreadChannel;
    }

    private static (ulong ChannelId, string ChannelName, ulong? ThreadId, string? ThreadName) ResolveChannelInfo(ISocketMessageChannel channel)
    {
        return channel switch
        {
            SocketThreadChannel threadChannel when threadChannel.ParentChannel is not null => (
                threadChannel.ParentChannel.Id,
                threadChannel.ParentChannel.Name,
                threadChannel.Id,
                threadChannel.Name),
            SocketGuildChannel guildChannel => (guildChannel.Id, guildChannel.Name, null, null),
            _ => (channel.Id, channel.Id.ToString(), null, null)
        };
    }

    private static string GetAuthorDisplayName(IUser author)
    {
        return author is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : author.GlobalName ?? author.Username;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace('Đ', 'D')
            .Replace('đ', 'd')
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lowered = char.ToLowerInvariant(character);
            if (char.IsLetterOrDigit(lowered) || lowered == '#')
            {
                builder.Append(lowered);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static List<string> ExtractSearchTerms(string? question)
    {
        return NormalizeText(question)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3)
            .Where(x => !StopWords.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
    }

    private static List<string> ExtractTopKeywords(IEnumerable<string> normalizedTexts, int maxItems)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var text in normalizedTexts)
        {
            var uniqueTerms = text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length >= 3)
                .Where(x => !StopWords.Contains(x))
                .Distinct(StringComparer.Ordinal);

            foreach (var term in uniqueTerms)
            {
                counts.TryGetValue(term, out var currentCount);
                counts[term] = currentCount + 1;
            }
        }

        return counts
            .OrderByDescending(x => x.Value)
            .ThenByDescending(x => ContainsImportantTerm(x.Key))
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(maxItems)
            .Select(x => x.Key)
            .ToList();
    }

    private static bool ContainsImportantTerm(string normalizedContent)
    {
        return ImportantTerms.Any(normalizedContent.Contains);
    }

    private static string BuildStandupKeywordText(StandupReport report)
    {
        return NormalizeText($"{report.Yesterday} {report.Today} {report.Blockers}");
    }

    private static bool HasMeaningfulBlockers(string? blockers)
    {
        if (string.IsNullOrWhiteSpace(blockers))
        {
            return false;
        }

        var normalized = NormalizeText(blockers);
        return normalized is not "khong co" and
               not "none" and
               not "no" and
               not "na";
    }

    private static string BuildChannelLabel(ProjectMemoryMessage message)
    {
        return string.IsNullOrWhiteSpace(message.ThreadName)
            ? $"#{message.ChannelName}"
            : $"#{message.ChannelName}/{message.ThreadName}";
    }

    private static int ScoreHighlightMessage(ProjectMemoryMessage message)
    {
        var score = message.IsBot ? 0 : 1;
        score += message.Content.Length switch
        {
            >= 120 => 3,
            >= 80 => 2,
            >= 40 => 1,
            _ => 0
        };

        if (ContainsImportantTerm(message.NormalizedContent))
        {
            score += 3;
        }

        if (message.NormalizedContent.Contains('#'))
        {
            score += 2;
        }

        return score;
    }

    private static List<string> DeserializeStringList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<DateTime> EnumerateDates(DateTime fromDate, DateTime toDate)
    {
        for (var date = fromDate.Date; date <= toDate.Date; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static string TrimTo(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }
}

public sealed record AssistantProjectMemory(
    int ArchivedMessageCount,
    DateTime? OldestLocalDate,
    DateTime? LatestLocalDate,
    IReadOnlyList<AssistantDailyMemoryDigest> DailyDigests,
    IReadOnlyList<AssistantHistoricalMessage> RelevantMessages);

public sealed record AssistantDailyMemoryDigest(
    DateTime Date,
    int MessageCount,
    int DistinctAuthorCount,
    int UserMessageCount,
    int BotMessageCount,
    int StandupReportCount,
    int BlockerCount,
    string Summary,
    IReadOnlyList<string> TopKeywords,
    IReadOnlyList<string> ActiveChannels,
    IReadOnlyList<string> Highlights);

public sealed record AssistantHistoricalMessage(
    ulong MessageId,
    DateTimeOffset TimestampLocal,
    string ChannelName,
    string? ThreadName,
    ulong AuthorId,
    string AuthorName,
    bool IsBot,
    string Content,
    int RelevanceScore);
