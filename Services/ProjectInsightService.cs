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
    ProjectMemoryService projectMemoryService,
    ProjectKnowledgeService projectKnowledgeService,
    StudioTimeService studioTime,
    IOptions<AssistantOptions> options,
    ILogger<ProjectInsightService> logger)
{
    private const int StandupDisciplineLookbackDays = 14;
    private const int ExpectedReporterMessageThreshold = 3;
    private const int StalledInProgressThresholdDays = 3;
    private const int StalledNotStartedThresholdDays = 2;
    private const int NonSprintStalledThresholdDays = 5;
    private const int TaskHistoryLookbackDays = 14;
    private static readonly TimeSpan StandupDueTime = new(9, 30, 0);

    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly ProjectService _projectService = projectService;
    private readonly ProjectMemoryService _projectMemoryService = projectMemoryService;
    private readonly ProjectKnowledgeService _projectKnowledgeService = projectKnowledgeService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly AssistantOptions _options = options.Value;
    private readonly ILogger<ProjectInsightService> _logger = logger;

    public async Task<ProjectAssistantContext?> BuildContextAsync(
        SocketUserMessage message,
        string? question = null,
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

        var recentConversation = await LoadRecentConversationAsync(message, cancellationToken);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await BuildContextCoreAsync(
            db,
            project,
            new AssistantScope(
                ProjectId: project.Id,
                ProjectName: project.Name,
                ChannelId: message.Channel.Id,
                ChannelName: GetChannelDisplayName(message.Channel),
                ParentChannelId: parentChannelId,
                ParentChannelName: GetParentChannelDisplayName(message.Channel),
                AskingUserId: message.Author.Id,
                AskingUserName: GetAuthorDisplayName(message.Author)),
            currentChannelId: parentChannelId ?? message.Channel.Id,
            currentThreadId: message.Channel is SocketThreadChannel ? message.Channel.Id : null,
            excludedMessageId: message.Id,
            question: question,
            recentConversation: recentConversation,
            cancellationToken);
    }

    public async Task<ProjectAssistantContext?> BuildProjectContextAsync(
        int projectId,
        string? question = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);

        if (project is null)
        {
            return null;
        }

        var channelId = project.GlobalNotificationChannelId ?? project.ChannelId;
        return await BuildContextCoreAsync(
            db,
            project,
            new AssistantScope(
                ProjectId: project.Id,
                ProjectName: project.Name,
                ChannelId: channelId,
                ChannelName: $"project:{project.Name}",
                ParentChannelId: null,
                ParentChannelName: null,
                AskingUserId: 0,
                AskingUserName: "Hệ thống"),
            currentChannelId: channelId,
            currentThreadId: null,
            excludedMessageId: 0,
            question: question,
            recentConversation: [],
            cancellationToken);
    }

    private async Task<ProjectAssistantContext> BuildContextCoreAsync(
        BotDbContext db,
        Project project,
        AssistantScope scope,
        ulong currentChannelId,
        ulong? currentThreadId,
        ulong excludedMessageId,
        string? question,
        IReadOnlyList<AssistantConversationMessage> recentConversation,
        CancellationToken cancellationToken)
    {
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

        var openProjectTasks = await projectTasksQuery
            .Where(x => x.Type == TaskItemType.Task && x.Status != TaskItemStatus.Done)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var sprintCatalog = await LoadSprintCatalogAsync(db, project.Id, cancellationToken);

        IQueryable<TaskItem> sprintTasksQuery = projectTasksQuery.Where(x => x.SprintId == null);
        if (activeSprint is not null)
        {
            sprintTasksQuery = projectTasksQuery.Where(x => x.SprintId == activeSprint.Id);
        }

        var sprintTasks = await sprintTasksQuery
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var completedTasks = (activeSprint is not null
                ? sprintTasks
                    .Where(x => x.Type == TaskItemType.Task && x.Status == TaskItemStatus.Done)
                    .OrderByDescending(x => x.Id)
                    .Take(12)
                    .ToList()
                : await projectTasksQuery
                    .Where(x => x.Type == TaskItemType.Task && x.Status == TaskItemStatus.Done)
                    .OrderByDescending(x => x.Id)
                    .Take(12)
                    .ToListAsync(cancellationToken))
            .Select(x => new AssistantCompletedTask(
                TaskId: x.Id,
                Title: x.Title,
                Points: x.Points,
                AssigneeId: x.AssigneeId,
                IsInActiveSprint: activeSprint is not null && x.SprintId == activeSprint.Id))
            .ToList();

        var latestTaskEventsByTaskId = await LoadLatestTaskEventsByTaskIdAsync(
            db,
            project.Id,
            openProjectTasks.Select(x => x.Id).ToList(),
            cancellationToken);
        var recentTaskEvents = await LoadRecentTaskEventsAsync(db, project.Id, cancellationToken);

        var taskFlow = BuildTaskFlowSummary(recentTaskEvents);
        var memberWorkloads = BuildMemberWorkloads(openProjectTasks, openBugs, recentTaskEvents);
        var stalledTasks = BuildStalledTasks(activeSprint, sprintTasks, openProjectTasks, latestTaskEventsByTaskId);
        var sprintSnapshot = BuildSprintSnapshot(activeSprint, sprintTasks, backlogCount, openBugs.Count);
        var recentStandups = await LoadRecentStandupsAsync(db, project.Id, cancellationToken);
        var standupDiscipline = await BuildStandupDisciplineAsync(db, project.Id, cancellationToken);
        var attentionItems = BuildAttentionItems(
            activeSprint,
            sprintTasks,
            openBugs,
            recentStandups,
            standupDiscipline,
            stalledTasks,
            memberWorkloads);
        var memory = await _projectMemoryService.BuildMemoryAsync(
            project.Id,
            currentChannelId: currentChannelId,
            currentThreadId: currentThreadId,
            excludedMessageId: excludedMessageId,
            question: question,
            cancellationToken);
        var knowledge = await _projectKnowledgeService.BuildKnowledgeAsync(
            project.Id,
            sprintSnapshot,
            recentStandups,
            standupDiscipline,
            memberWorkloads,
            stalledTasks,
            attentionItems,
            cancellationToken);

        return new ProjectAssistantContext(
            GeneratedAtLocal: _studioTime.LocalNow,
            Scope: scope,
            Sprint: sprintSnapshot,
            Standups: recentStandups,
            StandupDiscipline: standupDiscipline,
            TaskFlow: taskFlow,
            CompletedTasks: completedTasks,
            SprintCatalog: sprintCatalog,
            MemberWorkloads: memberWorkloads,
            StalledTasks: stalledTasks,
            AttentionItems: attentionItems.Take(Math.Max(1, _options.MaxAttentionItems)).ToList(),
            RecentConversation: recentConversation,
            Memory: memory,
            Knowledge: knowledge);
    }

    private async Task<Dictionary<int, TaskEvent>> LoadLatestTaskEventsByTaskIdAsync(
        BotDbContext db,
        int projectId,
        IReadOnlyCollection<int> taskIds,
        CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return [];
        }

        var events = await db.TaskEvents
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && taskIds.Contains(x.TaskItemId))
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return events
            .GroupBy(x => x.TaskItemId)
            .ToDictionary(x => x.Key, x => x.First());
    }

    private async Task<List<AssistantSprintTaskList>> LoadSprintCatalogAsync(
        BotDbContext db,
        int projectId,
        CancellationToken cancellationToken)
    {
        var sprints = await db.Sprints
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.IsActive)
            .ThenByDescending(x => x.EndDateLocal ?? x.StartDateLocal ?? DateTime.MinValue)
            .ThenByDescending(x => x.Id)
            .Take(6)
            .ToListAsync(cancellationToken);

        if (sprints.Count == 0)
        {
            return [];
        }

        var sprintIds = sprints.Select(x => x.Id).ToList();
        var sprintTasks = await db.TaskItems
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.SprintId.HasValue && sprintIds.Contains(x.SprintId.Value) && x.Type == TaskItemType.Task)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var tasksBySprint = sprintTasks
            .GroupBy(x => x.SprintId!.Value)
            .ToDictionary(x => x.Key, x => x.ToList());

        return sprints
            .Select(sprint =>
            {
                var tasks = tasksBySprint.GetValueOrDefault(sprint.Id) ?? [];
                var taskItems = tasks
                    .OrderByDescending(x => x.Status == TaskItemStatus.Done)
                    .ThenByDescending(x => x.Id)
                    .Take(20)
                    .Select(x => new AssistantSprintTaskItem(
                        TaskId: x.Id,
                        Title: x.Title,
                        Status: x.Status.ToString(),
                        Points: x.Points,
                        AssigneeId: x.AssigneeId))
                    .ToList();

                return new AssistantSprintTaskList(
                    SprintId: sprint.Id,
                    Name: sprint.Name,
                    Goal: sprint.Goal,
                    IsActive: sprint.IsActive,
                    StartDateLocal: sprint.StartDateLocal,
                    EndDateLocal: sprint.EndDateLocal,
                    TotalTasks: tasks.Count,
                    DoneTasks: tasks.Count(x => x.Status == TaskItemStatus.Done),
                    TodoTasks: tasks.Count(x => x.Status == TaskItemStatus.Todo),
                    InProgressTasks: tasks.Count(x => x.Status == TaskItemStatus.InProgress),
                    TaskItems: taskItems);
            })
            .ToList();
    }

    private async Task<List<TaskEvent>> LoadRecentTaskEventsAsync(
        BotDbContext db,
        int projectId,
        CancellationToken cancellationToken)
    {
        var fromDate = _studioTime.LocalDate.AddDays(-(TaskHistoryLookbackDays - 1));

        return await db.TaskEvents
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate)
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
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
            .ToListAsync(cancellationToken);

        return reports
            .OrderByDescending(x => x.LocalDate)
            .ThenBy(x => x.DiscordUserId)
            .Select(x =>
            {
                var reportedAtLocal = TimeZoneInfo.ConvertTime(x.ReportedAtUtc, _studioTime.TimeZone);
                return new AssistantStandupEntry(
                    Date: x.LocalDate,
                    DiscordUserId: x.DiscordUserId,
                    ReportedAtLocal: reportedAtLocal,
                    Yesterday: TrimForPrompt(x.Yesterday, 180),
                    Today: TrimForPrompt(x.Today, 180),
                    Blockers: TrimForPrompt(x.Blockers, 180),
                    HasBlockers: HasMeaningfulBlockers(x.Blockers));
            })
            .ToList();
    }

    private async Task<AssistantStandupDisciplineSummary> BuildStandupDisciplineAsync(
        BotDbContext db,
        int projectId,
        CancellationToken cancellationToken)
    {
        var fromDate = _studioTime.LocalDate.AddDays(-(StandupDisciplineLookbackDays - 1));
        var dueDates = EnumerateDueDates(fromDate, _studioTime.LocalDate).ToList();

        var reports = await db.StandupReports
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate)
            .ToListAsync(cancellationToken);

        var expectedReporters = await BuildExpectedReporterCandidatesAsync(
            db,
            projectId,
            fromDate,
            reports,
            cancellationToken);

        var lateReporters = reports
            .GroupBy(x => x.DiscordUserId)
            .Select(group =>
            {
                var materialized = group
                    .Select(report =>
                    {
                        var reportedAtLocal = TimeZoneInfo.ConvertTime(report.ReportedAtUtc, _studioTime.TimeZone);
                        var deadlineLocal = report.LocalDate.Add(StandupDueTime);
                        var lateMinutes = Math.Max(0, (int)Math.Round((reportedAtLocal.DateTime - deadlineLocal).TotalMinutes));

                        return new
                        {
                            Report = report,
                            ReportedAtLocal = reportedAtLocal,
                            IsLate = lateMinutes > 0,
                            LateMinutes = lateMinutes
                        };
                    })
                    .OrderBy(x => x.Report.LocalDate)
                    .ThenBy(x => x.ReportedAtLocal)
                    .ToList();

                var lateEntries = materialized.Where(x => x.IsLate).ToList();
                var latest = materialized.LastOrDefault();

                return new AssistantLateReporter(
                    DiscordUserId: group.Key,
                    TotalReports: materialized.Count,
                    LateReports: lateEntries.Count,
                    OnTimeReports: materialized.Count - lateEntries.Count,
                    LateRatePercent: materialized.Count == 0 ? 0 : (int)Math.Round((double)lateEntries.Count / materialized.Count * 100, MidpointRounding.AwayFromZero),
                    AverageLateMinutes: lateEntries.Count == 0 ? null : (int)Math.Round(lateEntries.Average(x => x.LateMinutes), MidpointRounding.AwayFromZero),
                    LastReportedAtLocal: latest?.ReportedAtLocal,
                    WasLateLastReport: latest?.IsLate == true);
            })
            .Where(x => x.TotalReports > 0)
            .OrderByDescending(x => x.LateReports)
            .ThenByDescending(x => x.LateRatePercent)
            .ThenByDescending(x => x.AverageLateMinutes ?? 0)
            .ThenByDescending(x => x.TotalReports)
            .ThenBy(x => x.DiscordUserId)
            .ToList();

        var reportsLookup = reports
            .Select(x => (x.DiscordUserId, x.LocalDate))
            .ToHashSet();

        var missingReporters = expectedReporters
            .Select(candidate =>
            {
                var effectiveDueDates = dueDates
                    .Where(x => x >= candidate.ActiveSinceDate.Date)
                    .ToList();

                var missingDates = effectiveDueDates
                    .Where(x => !reportsLookup.Contains((candidate.DiscordUserId, x)))
                    .ToList();

                if (missingDates.Count == 0)
                {
                    return null;
                }

                return new AssistantMissingReporter(
                    DiscordUserId: candidate.DiscordUserId,
                    MissingDays: missingDates.Count,
                    SubmittedDays: effectiveDueDates.Count - missingDates.Count,
                    LastMissingDate: missingDates.Max(),
                    MissingToday: missingDates.Any(x => x.Date == _studioTime.LocalDate.Date),
                    BasisSummary: candidate.BuildBasisSummary());
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.MissingDays)
            .ThenByDescending(x => x.MissingToday)
            .ThenByDescending(x => x.LastMissingDate)
            .ThenBy(x => x.DiscordUserId)
            .ToList();

        return new AssistantStandupDisciplineSummary(
            LookbackDays: StandupDisciplineLookbackDays,
            DueTimeLocal: StandupDueTime,
            ExpectedReporterCount: expectedReporters.Count,
            LateReporters: lateReporters,
            MissingReporters: missingReporters);
    }

    private async Task<List<ExpectedReporterCandidate>> BuildExpectedReporterCandidatesAsync(
        BotDbContext db,
        int projectId,
        DateTime fromDate,
        IReadOnlyList<StandupReport> reports,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<ulong, ExpectedReporterCandidateBuilder>();

        var openAssignedTasks = await db.TaskItems
            .AsNoTracking()
            .Where(x =>
                x.ProjectId == projectId &&
                x.Type == TaskItemType.Task &&
                x.Status != TaskItemStatus.Done &&
                x.AssigneeId.HasValue)
            .Select(x => new
            {
                AssigneeId = x.AssigneeId!.Value,
                x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        foreach (var task in openAssignedTasks)
        {
            var createdAtLocal = TimeZoneInfo.ConvertTime(task.CreatedAtUtc, _studioTime.TimeZone);
            var builder = GetOrCreateExpectedReporter(candidates, task.AssigneeId);
            builder.HasOpenAssignedTask = true;
            builder.Observe(createdAtLocal.Date);
        }

        foreach (var report in reports)
        {
            var builder = GetOrCreateExpectedReporter(candidates, report.DiscordUserId);
            builder.HadRecentStandup = true;
            builder.Observe(report.LocalDate);
        }

        var activeMessageAuthors = await db.ProjectMemoryMessages
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate && !x.IsBot)
            .GroupBy(x => x.AuthorId)
            .Select(group => new
            {
                DiscordUserId = group.Key,
                MessageCount = group.Count(),
                FirstSeenDate = group.Min(x => x.LocalDate)
            })
            .ToListAsync(cancellationToken);

        foreach (var author in activeMessageAuthors.Where(x => x.MessageCount >= ExpectedReporterMessageThreshold))
        {
            var builder = GetOrCreateExpectedReporter(candidates, author.DiscordUserId);
            builder.RecentProjectMessageCount = Math.Max(builder.RecentProjectMessageCount, author.MessageCount);
            builder.Observe(author.FirstSeenDate);
        }

        return candidates.Values
            .Where(x => x.ActiveSinceDate.HasValue)
            .Select(x => new ExpectedReporterCandidate(
                DiscordUserId: x.DiscordUserId,
                ActiveSinceDate: x.ActiveSinceDate!.Value,
                HasOpenAssignedTask: x.HasOpenAssignedTask,
                HadRecentStandup: x.HadRecentStandup,
                RecentProjectMessageCount: x.RecentProjectMessageCount))
            .OrderBy(x => x.ActiveSinceDate)
            .ThenBy(x => x.DiscordUserId)
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
        IReadOnlyCollection<AssistantStandupEntry> recentStandups,
        AssistantStandupDisciplineSummary standupDiscipline,
        IReadOnlyCollection<AssistantStalledTask> stalledTasks,
        IReadOnlyCollection<AssistantMemberWorkload> memberWorkloads)
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

        items.AddRange(
            stalledTasks
                .Take(5)
                .Select(x => new AssistantAttentionItem(
                    Kind: "stalled_task",
                    Title: $"Task #{x.TaskId} có dấu hiệu đình trệ",
                    Summary: $"{x.Title} ({x.Reason})",
                    TaskId: x.TaskId,
                    Status: x.Status,
                    Points: x.Points,
                    AssigneeId: x.AssigneeId)));

        items.AddRange(
            standupDiscipline.LateReporters
                .Where(x => x.LateReports > 0)
                .Take(3)
                .Select(x => new AssistantAttentionItem(
                    Kind: "late_reporter",
                    Title: $"<@{x.DiscordUserId}> hay trễ standup",
                    Summary: $"{x.LateReports}/{x.TotalReports} báo cáo trễ trong {standupDiscipline.LookbackDays} ngày gần đây",
                    TaskId: null,
                    Status: null,
                    Points: null,
                    AssigneeId: x.DiscordUserId)));

        items.AddRange(
            standupDiscipline.MissingReporters
                .Take(3)
                .Select(x => new AssistantAttentionItem(
                    Kind: "missing_reporter",
                    Title: $"<@{x.DiscordUserId}> chưa nộp standup",
                    Summary: $"Thiếu {x.MissingDays} ngày trong {standupDiscipline.LookbackDays} ngày gần đây ({x.BasisSummary})",
                    TaskId: null,
                    Status: null,
                    Points: null,
                    AssigneeId: x.DiscordUserId)));

        items.AddRange(
            memberWorkloads
                .Where(x => x.OpenPoints >= 8 || x.OpenTaskCount >= 3 || x.OpenBugCount >= 2)
                .Take(3)
                .Select(x => new AssistantAttentionItem(
                    Kind: "member_workload",
                    Title: $"<@{x.DiscordUserId}> dang giu workload cao",
                    Summary: $"{x.OpenTaskCount} task mo, {x.OpenPoints} diem, {x.OpenBugCount} bug, {x.RecentActivityCount} cap nhat gan day",
                    TaskId: null,
                    Status: null,
                    Points: x.OpenPoints,
                    AssigneeId: x.DiscordUserId)));

        return items
            .DistinctBy(x => $"{x.Kind}:{x.TaskId}:{x.Title}")
            .ToList();
    }

    private List<AssistantStalledTask> BuildStalledTasks(
        Sprint? activeSprint,
        IReadOnlyCollection<TaskItem> sprintTasks,
        IReadOnlyCollection<TaskItem> openProjectTasks,
        IReadOnlyDictionary<int, TaskEvent> latestTaskEventsByTaskId)
    {
        var candidateTasks = (activeSprint is not null ? sprintTasks : openProjectTasks)
            .Where(x => x.Type == TaskItemType.Task && x.Status != TaskItemStatus.Done)
            .ToList();

        var scheduleProgress = CalculateScheduleProgress(activeSprint) ?? 0D;
        var sprintOverdue = activeSprint is not null && IsSprintOverdue(activeSprint);

        return candidateTasks
            .Select(task => BuildStalledTask(
                task,
                activeSprint is not null,
                scheduleProgress,
                sprintOverdue,
                latestTaskEventsByTaskId.TryGetValue(task.Id, out var latestEvent) ? latestEvent : null))
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.IsOverdue)
            .ThenByDescending(x => x.Status == nameof(TaskItemStatus.InProgress))
            .ThenByDescending(x => x.DaysWithoutChange)
            .ThenByDescending(x => x.AgeDays)
            .ThenByDescending(x => x.Points)
            .ThenBy(x => x.TaskId)
            .Take(8)
            .ToList();
    }

    private AssistantStalledTask? BuildStalledTask(
        TaskItem task,
        bool hasActiveSprint,
        double scheduleProgress,
        bool sprintOverdue,
        TaskEvent? latestTaskEvent)
    {
        var createdAtLocal = TimeZoneInfo.ConvertTime(task.CreatedAtUtc, _studioTime.TimeZone);
        var age = _studioTime.LocalNow - createdAtLocal;
        var ageDays = Math.Max(0, (int)Math.Floor(age.TotalDays));
        var lastChangedLocal = latestTaskEvent is null
            ? createdAtLocal
            : ConvertUtcDateTimeToLocal(latestTaskEvent.OccurredAtUtc);
        var daysWithoutChange = Math.Max(0, (int)Math.Floor((_studioTime.LocalNow - lastChangedLocal).TotalDays));
        var evidence = latestTaskEvent is null
            ? $"Chua co task event, tam dung CreatedAt {createdAtLocal:yyyy-MM-dd HH:mm}"
            : $"Lan thay doi gan nhat: {FormatTaskEventType(latestTaskEvent.EventType)} luc {lastChangedLocal:yyyy-MM-dd HH:mm}";

        if (sprintOverdue)
        {
            return new AssistantStalledTask(
                TaskId: task.Id,
                Title: task.Title,
                Status: task.Status.ToString(),
                Points: task.Points,
                AssigneeId: task.AssigneeId,
                AgeDays: ageDays,
                DaysWithoutChange: daysWithoutChange,
                Reason: "Sprint đã quá hạn nhưng task chưa hoàn thành",
                IsOverdue: true,
                Evidence: evidence);
        }

        if (task.Status == TaskItemStatus.InProgress && daysWithoutChange >= StalledInProgressThresholdDays)
        {
            return new AssistantStalledTask(
                TaskId: task.Id,
                Title: task.Title,
                Status: task.Status.ToString(),
                Points: task.Points,
                AssigneeId: task.AssigneeId,
                AgeDays: ageDays,
                DaysWithoutChange: daysWithoutChange,
                Reason: $"Đang làm hơn {StalledInProgressThresholdDays} ngày mà chưa xong",
                IsOverdue: false,
                Evidence: evidence);
        }

        if (hasActiveSprint &&
            task.Status is TaskItemStatus.Todo or TaskItemStatus.Backlog &&
            scheduleProgress >= 0.5 &&
            daysWithoutChange >= StalledNotStartedThresholdDays)
        {
            return new AssistantStalledTask(
                TaskId: task.Id,
                Title: task.Title,
                Status: task.Status.ToString(),
                Points: task.Points,
                AssigneeId: task.AssigneeId,
                AgeDays: ageDays,
                DaysWithoutChange: daysWithoutChange,
                Reason: "Đã qua nửa sprint nhưng task vẫn chưa bắt đầu",
                IsOverdue: false,
                Evidence: evidence);
        }

        if (!hasActiveSprint &&
            task.Status == TaskItemStatus.InProgress &&
            daysWithoutChange >= NonSprintStalledThresholdDays)
        {
            return new AssistantStalledTask(
                TaskId: task.Id,
                Title: task.Title,
                Status: task.Status.ToString(),
                Points: task.Points,
                AssigneeId: task.AssigneeId,
                AgeDays: ageDays,
                DaysWithoutChange: daysWithoutChange,
                Reason: $"Task đã mở hơn {NonSprintStalledThresholdDays} ngày mà chưa hoàn thành",
                IsOverdue: false,
                Evidence: evidence);
        }

        return null;
    }

    private AssistantTaskFlowSummary BuildTaskFlowSummary(IReadOnlyCollection<TaskEvent> recentTaskEvents)
    {
        var topActors = recentTaskEvents
            .Where(x => x.ActorDiscordId.HasValue)
            .GroupBy(x => x.ActorDiscordId!.Value)
            .Select(group => new AssistantTaskActorSummary(
                DiscordUserId: group.Key,
                EventCount: group.Count(),
                CompletedTasks: group.Where(x => x.TaskType == TaskItemType.Task && x.EventType == TaskEventType.Completed).Select(x => x.TaskItemId).Distinct().Count(),
                FixedBugs: group.Where(x => x.TaskType == TaskItemType.Bug && x.EventType == TaskEventType.BugFixed).Select(x => x.TaskItemId).Distinct().Count(),
                ClaimedOrAssignedTasks: group.Count(x => x.EventType is TaskEventType.Claimed or TaskEventType.Assigned or TaskEventType.Started or TaskEventType.BugClaimed)))
            .OrderByDescending(x => x.EventCount)
            .ThenByDescending(x => x.CompletedTasks)
            .ThenByDescending(x => x.FixedBugs)
            .ThenBy(x => x.DiscordUserId)
            .Take(5)
            .ToList();

        return new AssistantTaskFlowSummary(
            LookbackDays: TaskHistoryLookbackDays,
            TotalEvents: recentTaskEvents.Count,
            CreatedTasks: recentTaskEvents.Count(x => x.TaskType == TaskItemType.Task && x.EventType == TaskEventType.Created),
            CompletedTasks: recentTaskEvents.Where(x => x.TaskType == TaskItemType.Task && x.EventType == TaskEventType.Completed).Select(x => x.TaskItemId).Distinct().Count(),
            CreatedBugs: recentTaskEvents.Count(x => x.TaskType == TaskItemType.Bug && x.EventType == TaskEventType.BugReported),
            FixedBugs: recentTaskEvents.Where(x => x.TaskType == TaskItemType.Bug && x.EventType == TaskEventType.BugFixed).Select(x => x.TaskItemId).Distinct().Count(),
            ReturnedToBacklog: recentTaskEvents.Where(x => x.EventType == TaskEventType.ReturnedToBacklog).Select(x => x.TaskItemId).Distinct().Count(),
            TopActors: topActors);
    }

    private List<AssistantMemberWorkload> BuildMemberWorkloads(
        IReadOnlyCollection<TaskItem> openProjectTasks,
        IReadOnlyCollection<TaskItem> openBugs,
        IReadOnlyCollection<TaskEvent> recentTaskEvents)
    {
        var taskGroups = openProjectTasks
            .Where(x => x.AssigneeId.HasValue)
            .GroupBy(x => x.AssigneeId!.Value)
            .ToDictionary(x => x.Key, x => x.ToList());

        var bugGroups = openBugs
            .Where(x => x.AssigneeId.HasValue)
            .GroupBy(x => x.AssigneeId!.Value)
            .ToDictionary(x => x.Key, x => x.ToList());

        var recentActivityByActor = recentTaskEvents
            .Where(x => x.ActorDiscordId.HasValue)
            .GroupBy(x => x.ActorDiscordId!.Value)
            .ToDictionary(x => x.Key, x => x.Count());

        return taskGroups.Keys
            .Union(bugGroups.Keys)
            .Select(discordUserId =>
            {
                taskGroups.TryGetValue(discordUserId, out var memberTasks);
                bugGroups.TryGetValue(discordUserId, out var memberBugs);
                recentActivityByActor.TryGetValue(discordUserId, out var activityCount);

                memberTasks ??= [];
                memberBugs ??= [];

                return new AssistantMemberWorkload(
                    DiscordUserId: discordUserId,
                    OpenTaskCount: memberTasks.Count,
                    InProgressTaskCount: memberTasks.Count(x => x.Status == TaskItemStatus.InProgress),
                    OpenBugCount: memberBugs.Count,
                    OpenPoints: memberTasks.Sum(x => x.Points),
                    RecentActivityCount: activityCount);
            })
            .OrderByDescending(x => x.OpenPoints)
            .ThenByDescending(x => x.OpenTaskCount)
            .ThenByDescending(x => x.OpenBugCount)
            .ThenByDescending(x => x.RecentActivityCount)
            .ThenBy(x => x.DiscordUserId)
            .ToList();
    }

    private DateTimeOffset ConvertUtcDateTimeToLocal(DateTime utcDateTime)
    {
        var utc = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), _studioTime.TimeZone);
    }

    private static string FormatTaskEventType(TaskEventType eventType)
    {
        return eventType switch
        {
            TaskEventType.Created => "tao task",
            TaskEventType.BacklogUpdated => "cap nhat backlog",
            TaskEventType.AddedToSprint => "them vao sprint",
            TaskEventType.Claimed => "nhan task",
            TaskEventType.Started => "bat dau",
            TaskEventType.Completed => "hoan thanh",
            TaskEventType.Assigned => "giao task",
            TaskEventType.BugReported => "bao bug",
            TaskEventType.BugClaimed => "nhan bug",
            TaskEventType.BugFixed => "dong bug",
            TaskEventType.ReturnedToBacklog => "tra ve backlog",
            TaskEventType.Deleted => "xoa task",
            TaskEventType.SeededForTest => "cap nhat test",
            _ => eventType.ToString()
        };
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

    private IEnumerable<DateTime> EnumerateDueDates(DateTime fromDate, DateTime toDate)
    {
        for (var date = fromDate.Date; date <= toDate.Date; date = date.AddDays(1))
        {
            if (date.Date == _studioTime.LocalDate.Date &&
                _studioTime.LocalNow.TimeOfDay < StandupDueTime)
            {
                continue;
            }

            yield return date;
        }
    }

    private static ExpectedReporterCandidateBuilder GetOrCreateExpectedReporter(
        IDictionary<ulong, ExpectedReporterCandidateBuilder> candidates,
        ulong discordUserId)
    {
        if (!candidates.TryGetValue(discordUserId, out var builder))
        {
            builder = new ExpectedReporterCandidateBuilder(discordUserId);
            candidates[discordUserId] = builder;
        }

        return builder;
    }
}

public sealed record ProjectAssistantContext(
    DateTimeOffset GeneratedAtLocal,
    AssistantScope Scope,
    AssistantSprintSnapshot Sprint,
    IReadOnlyList<AssistantStandupEntry> Standups,
    AssistantStandupDisciplineSummary StandupDiscipline,
    AssistantTaskFlowSummary TaskFlow,
    IReadOnlyList<AssistantCompletedTask> CompletedTasks,
    IReadOnlyList<AssistantSprintTaskList> SprintCatalog,
    IReadOnlyList<AssistantMemberWorkload> MemberWorkloads,
    IReadOnlyList<AssistantStalledTask> StalledTasks,
    IReadOnlyList<AssistantAttentionItem> AttentionItems,
    IReadOnlyList<AssistantConversationMessage> RecentConversation,
    AssistantProjectMemory Memory,
    AssistantProjectKnowledge Knowledge);

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
    DateTimeOffset ReportedAtLocal,
    string Yesterday,
    string Today,
    string Blockers,
    bool HasBlockers);

public sealed record AssistantStandupDisciplineSummary(
    int LookbackDays,
    TimeSpan DueTimeLocal,
    int ExpectedReporterCount,
    IReadOnlyList<AssistantLateReporter> LateReporters,
    IReadOnlyList<AssistantMissingReporter> MissingReporters);

public sealed record AssistantLateReporter(
    ulong DiscordUserId,
    int TotalReports,
    int LateReports,
    int OnTimeReports,
    int LateRatePercent,
    int? AverageLateMinutes,
    DateTimeOffset? LastReportedAtLocal,
    bool WasLateLastReport);

public sealed record AssistantMissingReporter(
    ulong DiscordUserId,
    int MissingDays,
    int SubmittedDays,
    DateTime? LastMissingDate,
    bool MissingToday,
    string BasisSummary);

public sealed record AssistantTaskFlowSummary(
    int LookbackDays,
    int TotalEvents,
    int CreatedTasks,
    int CompletedTasks,
    int CreatedBugs,
    int FixedBugs,
    int ReturnedToBacklog,
    IReadOnlyList<AssistantTaskActorSummary> TopActors);

public sealed record AssistantTaskActorSummary(
    ulong DiscordUserId,
    int EventCount,
    int CompletedTasks,
    int FixedBugs,
    int ClaimedOrAssignedTasks);

public sealed record AssistantCompletedTask(
    int TaskId,
    string Title,
    int Points,
    ulong? AssigneeId,
    bool IsInActiveSprint);

public sealed record AssistantSprintTaskList(
    int SprintId,
    string Name,
    string Goal,
    bool IsActive,
    DateTime? StartDateLocal,
    DateTime? EndDateLocal,
    int TotalTasks,
    int DoneTasks,
    int TodoTasks,
    int InProgressTasks,
    IReadOnlyList<AssistantSprintTaskItem> TaskItems);

public sealed record AssistantSprintTaskItem(
    int TaskId,
    string Title,
    string Status,
    int Points,
    ulong? AssigneeId);

public sealed record AssistantMemberWorkload(
    ulong DiscordUserId,
    int OpenTaskCount,
    int InProgressTaskCount,
    int OpenBugCount,
    int OpenPoints,
    int RecentActivityCount);

public sealed record AssistantStalledTask(
    int TaskId,
    string Title,
    string Status,
    int Points,
    ulong? AssigneeId,
    int AgeDays,
    int DaysWithoutChange,
    string Reason,
    bool IsOverdue,
    string Evidence);

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

sealed class ExpectedReporterCandidateBuilder(ulong discordUserId)
{
    private const int MessageThreshold = 3;

    public ulong DiscordUserId { get; } = discordUserId;
    public bool HasOpenAssignedTask { get; set; }
    public bool HadRecentStandup { get; set; }
    public int RecentProjectMessageCount { get; set; }
    public DateTime? ActiveSinceDate { get; private set; }

    public void Observe(DateTime date)
    {
        if (!ActiveSinceDate.HasValue || date < ActiveSinceDate.Value)
        {
            ActiveSinceDate = date;
        }
    }

    public string BuildBasisSummary()
    {
        var parts = new List<string>();
        if (HasOpenAssignedTask)
        {
            parts.Add("đang có task mở");
        }

        if (HadRecentStandup)
        {
            parts.Add("đã từng nộp standup");
        }

        if (RecentProjectMessageCount >= MessageThreshold)
        {
            parts.Add($"{RecentProjectMessageCount} tin nhắn project gần đây");
        }

        return parts.Count == 0
            ? "có hoạt động gần đây trong project"
            : string.Join(", ", parts);
    }
}

sealed record ExpectedReporterCandidate(
    ulong DiscordUserId,
    DateTime ActiveSinceDate,
    bool HasOpenAssignedTask,
    bool HadRecentStandup,
    int RecentProjectMessageCount)
{
    public string BuildBasisSummary()
    {
        var parts = new List<string>();
        if (HasOpenAssignedTask)
        {
            parts.Add("đang có task mở");
        }

        if (HadRecentStandup)
        {
            parts.Add("đã từng nộp standup");
        }

        if (RecentProjectMessageCount >= 3)
        {
            parts.Add($"{RecentProjectMessageCount} tin nhắn project gần đây");
        }

        return parts.Count == 0
            ? "có hoạt động gần đây trong project"
            : string.Join(", ", parts);
    }
}
