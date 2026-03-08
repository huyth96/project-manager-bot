using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;
using ProjectManagerBot.Options;

namespace ProjectManagerBot.Services;

public sealed class ProjectInsightService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    ProjectService projectService,
    StudioTimeService studioTime,
    IOptions<AssistantOptions> options,
    ILogger<ProjectInsightService> logger)
{
    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly ProjectService _projectService = projectService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly AssistantOptions _options = options.Value;
    private readonly ILogger<ProjectInsightService> _logger = logger;

    public async Task<ProjectAssistantContext?> BuildContextAsync(
        SocketUserMessage message,
        CancellationToken cancellationToken = default)
    {
        var parentChannelId = message.Channel is SocketThreadChannel threadChannel
            ? threadChannel.ParentChannel?.Id
            : null;

        var project = await _projectService.GetProjectByChannelHierarchyAsync(
            message.Channel.Id,
            parentChannelId,
            cancellationToken);

        if (project is null)
        {
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var activeSprint = await db.Sprints
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ProjectId == project.Id && x.IsActive,
                cancellationToken);

        var projectTasksQuery = db.TaskItems
            .AsNoTracking()
            .Where(x => x.ProjectId == project.Id);

        var backlogCount = await projectTasksQuery.CountAsync(
            x => x.SprintId == null && x.Type == TaskItemType.Task,
            cancellationToken);

        var openBugs = await projectTasksQuery
            .Where(x => x.Type == TaskItemType.Bug && x.Status != TaskItemStatus.Done)
            .OrderByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        IQueryable<TaskItem> sprintTasksQuery = projectTasksQuery.Where(x => x.SprintId == null);
        if (activeSprint is not null)
        {
            sprintTasksQuery = projectTasksQuery.Where(x => x.SprintId == activeSprint.Id);
        }

        var sprintTasks = await sprintTasksQuery
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var sprintSnapshot = BuildSprintSnapshot(activeSprint, sprintTasks, backlogCount, openBugs.Count);
        var recentStandups = await LoadRecentStandupsAsync(db, project.Id, cancellationToken);
        var attentionItems = BuildAttentionItems(activeSprint, sprintTasks, openBugs, recentStandups);
        var recentConversation = await LoadRecentConversationAsync(message, cancellationToken);

        return new ProjectAssistantContext(
            GeneratedAtLocal: _studioTime.LocalNow,
            Scope: new AssistantScope(
                ProjectId: project.Id,
                ProjectName: project.Name,
                ChannelId: message.Channel.Id,
                ChannelName: GetChannelDisplayName(message.Channel),
                ParentChannelId: parentChannelId,
                ParentChannelName: GetParentChannelDisplayName(message.Channel),
                AskingUserId: message.Author.Id,
                AskingUserName: GetAuthorDisplayName(message.Author)),
            Sprint: sprintSnapshot,
            Standups: recentStandups,
            AttentionItems: attentionItems.Take(Math.Max(1, _options.MaxAttentionItems)).ToList(),
            RecentConversation: recentConversation);
    }

    private async Task<List<AssistantStandupEntry>> LoadRecentStandupsAsync(
        BotDbContext db,
        int projectId,
        CancellationToken cancellationToken)
    {
        var lookbackDays = Math.Clamp(_options.MaxStandupDays, 1, 7);
        var fromDate = _studioTime.LocalDate.AddDays(-(lookbackDays - 1));

        var reports = await db.StandupReports
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate)
            .OrderByDescending(x => x.LocalDate)
            .ThenBy(x => x.DiscordUserId)
            .ToListAsync(cancellationToken);

        return reports
            .Select(x => new AssistantStandupEntry(
                Date: x.LocalDate,
                DiscordUserId: x.DiscordUserId,
                Yesterday: TrimForPrompt(x.Yesterday, 180),
                Today: TrimForPrompt(x.Today, 180),
                Blockers: TrimForPrompt(x.Blockers, 180),
                HasBlockers: HasMeaningfulBlockers(x.Blockers)))
            .ToList();
    }

    private async Task<List<AssistantConversationMessage>> LoadRecentConversationAsync(
        SocketUserMessage currentMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var limit = Math.Clamp(_options.MaxRecentMessages + 4, 4, 40);
            var messages = await currentMessage.Channel
                .GetMessagesAsync(limit: limit)
                .FlattenAsync();

            return messages
                .Where(x => x.Id != currentMessage.Id)
                .Where(x => x.Source is MessageSource.User or MessageSource.Bot)
                .OrderBy(x => x.Timestamp)
                .TakeLast(Math.Clamp(_options.MaxRecentMessages, 1, 30))
                .Select(x => new AssistantConversationMessage(
                    MessageId: x.Id,
                    TimestampLocal: TimeZoneInfo.ConvertTime(x.Timestamp, _studioTime.TimeZone),
                    AuthorId: x.Author.Id,
                    AuthorName: GetAuthorDisplayName(x.Author),
                    IsBot: x.Author.IsBot,
                    Content: TrimForPrompt(x.Content, 240)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Content))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Không thể tải recent conversation cho channel {ChannelId}", currentMessage.Channel.Id);
            return [];
        }
    }

    private List<AssistantAttentionItem> BuildAttentionItems(
        Sprint? activeSprint,
        IReadOnlyCollection<TaskItem> sprintTasks,
        IReadOnlyCollection<TaskItem> openBugs,
        IReadOnlyCollection<AssistantStandupEntry> recentStandups)
    {
        var items = new List<AssistantAttentionItem>();

        if (activeSprint is not null)
        {
            var overdueTasks = sprintTasks
                .Where(x => x.Status != TaskItemStatus.Done)
                .Where(_ => IsSprintOverdue(activeSprint))
                .OrderByDescending(x => x.Points)
                .ThenBy(x => x.Id)
                .Take(5)
                .Select(x => new AssistantAttentionItem(
                    Kind: "overdue_task",
                    Title: $"Task #{x.Id} quá hạn",
                    Summary: x.Title,
                    TaskId: x.Id,
                    Status: x.Status.ToString(),
                    Points: x.Points,
                    AssigneeId: x.AssigneeId))
                .ToList();

            items.AddRange(overdueTasks);
        }

        items.AddRange(
            sprintTasks
                .Where(x => x.Status != TaskItemStatus.Done && !x.AssigneeId.HasValue)
                .OrderByDescending(x => x.Points)
                .ThenBy(x => x.Id)
                .Take(5)
                .Select(x => new AssistantAttentionItem(
                    Kind: "unassigned_task",
                    Title: $"Task #{x.Id} chưa có người xử lý",
                    Summary: x.Title,
                    TaskId: x.Id,
                    Status: x.Status.ToString(),
                    Points: x.Points,
                    AssigneeId: null)));

        items.AddRange(
            sprintTasks
                .Where(x => x.Status is TaskItemStatus.Backlog or TaskItemStatus.Todo && x.Points >= 5)
                .OrderByDescending(x => x.Points)
                .ThenBy(x => x.Id)
                .Take(5)
                .Select(x => new AssistantAttentionItem(
                    Kind: "high_point_not_started",
                    Title: $"Task #{x.Id} điểm cao chưa bắt đầu",
                    Summary: x.Title,
                    TaskId: x.Id,
                    Status: x.Status.ToString(),
                    Points: x.Points,
                    AssigneeId: x.AssigneeId)));

        items.AddRange(
            openBugs
                .Take(5)
                .Select(x => new AssistantAttentionItem(
                    Kind: "open_bug",
                    Title: $"Bug #{x.Id} đang mở",
                    Summary: x.Title,
                    TaskId: x.Id,
                    Status: x.Status.ToString(),
                    Points: x.Points,
                    AssigneeId: x.AssigneeId)));

        items.AddRange(
            recentStandups
                .Where(x => x.HasBlockers)
                .Take(5)
                .Select(x => new AssistantAttentionItem(
                    Kind: "standup_blocker",
                    Title: $"Blocker từ standup {x.Date:yyyy-MM-dd}",
                    Summary: $"<@{x.DiscordUserId}>: {x.Blockers}",
                    TaskId: null,
                    Status: null,
                    Points: null,
                    AssigneeId: x.DiscordUserId)));

        return items
            .DistinctBy(x => $"{x.Kind}:{x.TaskId}:{x.Title}")
            .ToList();
    }

    private AssistantSprintSnapshot BuildSprintSnapshot(
        Sprint? activeSprint,
        IReadOnlyCollection<TaskItem> sprintTasks,
        int backlogCount,
        int openBugCount)
    {
        var todoCount = sprintTasks.Count(x => x.Status == TaskItemStatus.Todo);
        var inProgressCount = sprintTasks.Count(x => x.Status == TaskItemStatus.InProgress);
        var doneCount = sprintTasks.Count(x => x.Status == TaskItemStatus.Done);
        var sprintBacklogCount = sprintTasks.Count(x => x.Status == TaskItemStatus.Backlog);

        var totalPoints = sprintTasks.Sum(x => x.Points);
        var donePoints = sprintTasks.Where(x => x.Status == TaskItemStatus.Done).Sum(x => x.Points);
        var inProgressPoints = sprintTasks.Where(x => x.Status == TaskItemStatus.InProgress).Sum(x => x.Points);

        var scheduleProgress = CalculateScheduleProgress(activeSprint);
        var deliveryProgress = totalPoints <= 0 ? 0D : (double)donePoints / totalPoints;
        var health = BuildHealthSummary(activeSprint, scheduleProgress, deliveryProgress, sprintTasks.Count, openBugCount);

        return new AssistantSprintSnapshot(
            HasActiveSprint: activeSprint is not null,
            SprintId: activeSprint?.Id,
            Name: activeSprint?.Name,
            Goal: string.IsNullOrWhiteSpace(activeSprint?.Goal) ? null : activeSprint.Goal,
            StartDateLocal: activeSprint?.StartDateLocal,
            EndDateLocal: activeSprint?.EndDateLocal,
            TotalTasks: sprintTasks.Count,
            DoneTasks: doneCount,
            TodoTasks: todoCount,
            InProgressTasks: inProgressCount,
            BacklogTasksInSprint: sprintBacklogCount,
            ProjectBacklogCount: backlogCount,
            OpenBugCount: openBugCount,
            TotalPoints: totalPoints,
            DonePoints: donePoints,
            InProgressPoints: inProgressPoints,
            DeliveryProgressPercent: ToPercent(deliveryProgress),
            ScheduleProgressPercent: scheduleProgress.HasValue ? ToPercent(scheduleProgress.Value) : null,
            Health: health);
    }

    private AssistantHealthSummary BuildHealthSummary(
        Sprint? activeSprint,
        double? scheduleProgress,
        double deliveryProgress,
        int totalTasks,
        int openBugCount)
    {
        if (activeSprint is null)
        {
            return new AssistantHealthSummary(
                Label: "no_active_sprint",
                Summary: "Chưa có sprint active để đánh giá health.",
                DeltaPercent: null);
        }

        if (!scheduleProgress.HasValue)
        {
            return new AssistantHealthSummary(
                Label: "insufficient_schedule_data",
                Summary: "Sprint đang chạy nhưng thiếu mốc thời gian để so sánh tiến độ với timeline.",
                DeltaPercent: null);
        }

        var delta = deliveryProgress - scheduleProgress.Value;
        var label = delta switch
        {
            >= 0.12 => "positive",
            <= -0.12 => "negative",
            _ => "neutral"
        };

        var summary = label switch
        {
            "positive" => "Tiến độ đang tích cực: điểm hoàn thành đang đi trước timeline sprint.",
            "negative" => "Tiến độ đang tiêu cực: phần việc hoàn thành đang chậm hơn timeline sprint.",
            _ => "Tiến độ đang trung tính: phần việc hoàn thành khá sát timeline sprint."
        };

        if (totalTasks == 0)
        {
            summary = "Sprint active nhưng chưa có task nào được đưa vào sprint.";
            label = "insufficient_task_data";
        }
        else if (openBugCount >= 5 && label == "positive")
        {
            summary += " Tuy nhiên số bug mở đang khá cao, cần kiểm tra chất lượng delivery.";
        }

        return new AssistantHealthSummary(
            Label: label,
            Summary: summary,
            DeltaPercent: ToPercent(delta));
    }

    private double? CalculateScheduleProgress(Sprint? sprint)
    {
        if (sprint?.StartDateLocal is null || sprint.EndDateLocal is null)
        {
            return null;
        }

        var start = sprint.StartDateLocal.Value;
        var end = sprint.EndDateLocal.Value;
        if (end <= start)
        {
            return null;
        }

        var now = _studioTime.LocalNow.DateTime;
        if (now <= start)
        {
            return 0;
        }

        if (now >= end)
        {
            return 1;
        }

        return (now - start).TotalSeconds / (end - start).TotalSeconds;
    }

    private bool IsSprintOverdue(Sprint sprint)
    {
        if (!sprint.EndDateLocal.HasValue)
        {
            return false;
        }

        var endLocal = sprint.EndDateLocal.Value;
        var effectiveEndLocal = endLocal.TimeOfDay == TimeSpan.Zero
            ? endLocal.Date.AddDays(1).AddTicks(-1)
            : endLocal;

        return _studioTime.LocalNow.DateTime > effectiveEndLocal;
    }

    private static int ToPercent(double ratio)
    {
        return (int)Math.Round(Math.Clamp(ratio, -1D, 1D) * 100D, MidpointRounding.AwayFromZero);
    }

    private static bool HasMeaningfulBlockers(string? blockers)
    {
        if (string.IsNullOrWhiteSpace(blockers))
        {
            return false;
        }

        var normalized = blockers.Trim().ToLowerInvariant();
        return normalized is not "khong co" and
               not "không có" and
               not "none" and
               not "no" and
               not "n/a";
    }

    private static string TrimForPrompt(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return compact.Length <= maxLength ? compact : $"{compact[..maxLength]}...";
    }

    private static string GetChannelDisplayName(ISocketMessageChannel channel)
    {
        return channel switch
        {
            SocketThreadChannel threadChannel when threadChannel.ParentChannel is not null =>
                $"#{threadChannel.ParentChannel.Name}/{threadChannel.Name}",
            SocketGuildChannel guildChannel => $"#{guildChannel.Name}",
            _ => channel.Id.ToString()
        };
    }

    private static string? GetParentChannelDisplayName(ISocketMessageChannel channel)
    {
        return channel is SocketThreadChannel threadChannel
            ? $"#{threadChannel.ParentChannel?.Name}"
            : null;
    }

    private static string GetAuthorDisplayName(IUser author)
    {
        return author is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : author.Username;
    }
}

public sealed record ProjectAssistantContext(
    DateTimeOffset GeneratedAtLocal,
    AssistantScope Scope,
    AssistantSprintSnapshot Sprint,
    IReadOnlyList<AssistantStandupEntry> Standups,
    IReadOnlyList<AssistantAttentionItem> AttentionItems,
    IReadOnlyList<AssistantConversationMessage> RecentConversation);

public sealed record AssistantScope(
    int ProjectId,
    string ProjectName,
    ulong ChannelId,
    string ChannelName,
    ulong? ParentChannelId,
    string? ParentChannelName,
    ulong AskingUserId,
    string AskingUserName);

public sealed record AssistantSprintSnapshot(
    bool HasActiveSprint,
    int? SprintId,
    string? Name,
    string? Goal,
    DateTime? StartDateLocal,
    DateTime? EndDateLocal,
    int TotalTasks,
    int DoneTasks,
    int TodoTasks,
    int InProgressTasks,
    int BacklogTasksInSprint,
    int ProjectBacklogCount,
    int OpenBugCount,
    int TotalPoints,
    int DonePoints,
    int InProgressPoints,
    int DeliveryProgressPercent,
    int? ScheduleProgressPercent,
    AssistantHealthSummary Health);

public sealed record AssistantHealthSummary(
    string Label,
    string Summary,
    int? DeltaPercent);

public sealed record AssistantStandupEntry(
    DateTime Date,
    ulong DiscordUserId,
    string Yesterday,
    string Today,
    string Blockers,
    bool HasBlockers);

public sealed record AssistantAttentionItem(
    string Kind,
    string Title,
    string Summary,
    int? TaskId,
    string? Status,
    int? Points,
    ulong? AssigneeId);

public sealed record AssistantConversationMessage(
    ulong MessageId,
    DateTimeOffset TimestampLocal,
    ulong AuthorId,
    string AuthorName,
    bool IsBot,
    string Content);
