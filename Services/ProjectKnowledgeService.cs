using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;

namespace ProjectManagerBot.Services;

public sealed class ProjectKnowledgeService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    ProjectMemoryService projectMemoryService,
    StudioTimeService studioTime,
    ILogger<ProjectKnowledgeService> logger)
{
    private const int KnowledgeLookbackDays = 14;
    private const int TopicLookbackDays = 7;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StandupDueTime = new(9, 30, 0);
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ProjectKnowledgeLocks = new();
    private static readonly Dictionary<string, string[]> TopicKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auth"] = ["auth", "login", "signin", "sign in", "token", "oauth", "permission"],
        ["ui"] = ["ui", "ux", "layout", "screen", "button", "modal", "responsive"],
        ["api"] = ["api", "endpoint", "controller", "request", "response", "service"],
        ["deploy"] = ["deploy", "release", "server", "hosting", "production", "pipeline", "ci", "cd"],
        ["database"] = ["db", "database", "sql", "sqlite", "migration", "schema", "query"],
        ["payment"] = ["payment", "billing", "checkout", "invoice", "stripe"],
        ["testing"] = ["test", "testing", "qa", "bug repro", "verify", "regression"],
        ["performance"] = ["slow", "lag", "performance", "optimize", "memory", "cpu"],
        ["process"] = ["estimate", "scope", "plan", "standup", "retro", "review", "deadline"],
        ["bug"] = ["bug", "issue", "error", "loi", "exception", "fail", "crash", "reopen"]
    };
    private static readonly string[] DecisionCues =
    [
        "chot", "quyet dinh", "thong nhat", "dong y", "se dung", "final", "approved", "chon", "uu tien"
    ];
    private static readonly string[] RiskCues =
    [
        "blocker", "rui ro", "risk", "tre", "delay", "overdue", "bug", "issue", "stuck", "ket", "tac", "dependency"
    ];

    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly ProjectMemoryService _projectMemoryService = projectMemoryService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly ILogger<ProjectKnowledgeService> _logger = logger;

    public async Task<AssistantProjectKnowledge> BuildKnowledgeAsync(
        int projectId,
        AssistantSprintSnapshot sprint,
        IReadOnlyList<AssistantStandupEntry> standups,
        AssistantStandupDisciplineSummary standupDiscipline,
        IReadOnlyList<AssistantMemberWorkload> memberWorkloads,
        IReadOnlyList<AssistantStalledTask> stalledTasks,
        IReadOnlyList<AssistantAttentionItem> attentionItems,
        CancellationToken cancellationToken = default)
    {
        var projectGate = ProjectKnowledgeLocks.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await projectGate.WaitAsync(cancellationToken);
        try
        {
            var today = _studioTime.LocalDate.Date;
            var knowledgeFromDate = today.AddDays(-(KnowledgeLookbackDays - 1));
            var topicFromDate = today.AddDays(-(TopicLookbackDays - 1));

            await _projectMemoryService.EnsureDailyDigestsAsync(projectId, topicFromDate, today, cancellationToken);

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await EnsureTaskEventBaselinesAsync(db, projectId, cancellationToken);

            var messages = await db.ProjectMemoryMessages
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.LocalDate >= knowledgeFromDate)
                .ToListAsync(cancellationToken);
            messages = messages
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .ToList();
            var botUserIds = messages
                .Where(x => x.IsBot)
                .Select(x => x.AuthorId)
                .ToHashSet();
            botUserIds.Add(0);
            var humanMessages = messages
                .Where(x => !x.IsBot)
                .ToList();
            var displayNameLookup = BuildDisplayNameMap(humanMessages);
            var filteredStandups = standups
                .Where(x => !botUserIds.Contains(x.DiscordUserId))
                .ToList();
            var filteredStandupDiscipline = FilterStandupDiscipline(standupDiscipline, botUserIds);
            var filteredMemberWorkloads = memberWorkloads
                .Where(x => !botUserIds.Contains(x.DiscordUserId))
                .ToList();
            var filteredStalledTasks = stalledTasks
                .Where(x => !x.AssigneeId.HasValue || !botUserIds.Contains(x.AssigneeId.Value))
                .ToList();
            var filteredAttentionItems = attentionItems
                .Where(x => !x.AssigneeId.HasValue || !botUserIds.Contains(x.AssigneeId.Value))
                .ToList();

            var digests = await db.ProjectDailyDigests
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.LocalDate >= topicFromDate)
                .OrderByDescending(x => x.LocalDate)
                .ToListAsync(cancellationToken);

            var taskEvents = await db.TaskEvents
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.LocalDate >= knowledgeFromDate)
                .OrderByDescending(x => x.OccurredAtUtc)
                .ThenByDescending(x => x.Id)
                .ToListAsync(cancellationToken);
            taskEvents = taskEvents
                .Where(x => !x.ActorDiscordId.HasValue || !botUserIds.Contains(x.ActorDiscordId.Value))
                .ToList();

            var taskCreations = await db.TaskItems
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId)
                .Select(x => new TaskCreationSeed(x.Id, x.AssigneeId, x.CreatedAtUtc))
                .ToListAsync(cancellationToken);
            var sprintNameLookup = await db.Sprints
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId)
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            var completedWorkItems = await db.TaskItems
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.AssigneeId.HasValue && x.Status == TaskItemStatus.Done)
                .Select(x => new CompletedWorkSeed(
                    x.AssigneeId!.Value,
                    x.Type,
                    x.Id,
                    x.Title,
                    x.Points,
                    x.SprintId))
                .ToListAsync(cancellationToken);
            completedWorkItems = completedWorkItems
                .Where(x => !botUserIds.Contains(x.AssigneeId))
                .ToList();

            var topicMentions = BuildTopicMentions(projectId, topicFromDate, humanMessages, digests)
                .GroupBy(x => new { x.ProjectId, Date = x.LocalDate.Date, x.TopicKey })
                .Select(x => x
                    .OrderByDescending(y => y.MentionCount)
                    .ThenByDescending(y => y.DistinctAuthorCount)
                    .First())
                .ToList();
            var memberSignals = BuildMemberSignals(
                    projectId,
                    knowledgeFromDate,
                    humanMessages,
                    filteredStandups,
                    taskEvents,
                    taskCreations,
                    filteredMemberWorkloads,
                    botUserIds)
                .GroupBy(x => new { x.ProjectId, x.DiscordUserId, Date = x.LocalDate.Date })
                .Select(x => x
                    .OrderByDescending(y => y.SubmittedStandup)
                    .ThenByDescending(y => y.ActivityCount)
                    .ThenByDescending(y => y.ReliabilityScore)
                    .First())
                .ToList();
            var decisionLogs = BuildDecisionLogs(projectId, topicFromDate, humanMessages);
            var riskLogs = BuildRiskLogs(projectId, today, humanMessages, filteredStandups, filteredAttentionItems, filteredStalledTasks, displayNameLookup);
            var memberProfiles = BuildMemberProfiles(
                projectId,
                humanMessages,
                filteredStandups,
                taskEvents,
                completedWorkItems,
                sprintNameLookup,
                memberSignals,
                filteredMemberWorkloads,
                displayNameLookup,
                botUserIds);

            ReplaceDateRange(db.TopicMentions, db.TopicMentions.Where(x => x.ProjectId == projectId && x.LocalDate >= topicFromDate));
            ReplaceDateRange(db.MemberDailySignals, db.MemberDailySignals.Where(x => x.ProjectId == projectId && x.LocalDate >= knowledgeFromDate));
            ReplaceDateRange(db.DecisionLogs, db.DecisionLogs.Where(x => x.ProjectId == projectId && x.LocalDate >= topicFromDate));
            ReplaceDateRange(db.RiskLogs, db.RiskLogs.Where(x => x.ProjectId == projectId && x.LocalDate >= topicFromDate));
            await db.SaveChangesAsync(cancellationToken);

            db.TopicMentions.AddRange(topicMentions);
            db.MemberDailySignals.AddRange(memberSignals);
            db.DecisionLogs.AddRange(decisionLogs);
            db.RiskLogs.AddRange(riskLogs);
            UpsertMemberProfiles(db, projectId, memberProfiles);
            UpsertSprintSnapshot(db, projectId, today, sprint, stalledTasks, attentionItems);
            UpsertRiskSnapshot(db, projectId, today, filteredStandups, filteredStandupDiscipline, filteredAttentionItems, filteredStalledTasks, sprint.OpenBugCount);

            await db.SaveChangesAsync(cancellationToken);

            return await LoadAssistantKnowledgeAsync(db, projectId, topicFromDate, memberProfiles, memberSignals, topicMentions, decisionLogs, riskLogs, cancellationToken);
        }
        finally
        {
            projectGate.Release();
        }
    }

    private async Task EnsureTaskEventBaselinesAsync(BotDbContext db, int projectId, CancellationToken cancellationToken)
    {
        var tasksWithoutEvents = await db.TaskItems
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && !db.TaskEvents.Any(e => e.TaskItemId == x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (tasksWithoutEvents.Count == 0)
        {
            return;
        }

        foreach (var task in tasksWithoutEvents)
        {
            db.TaskEvents.Add(new TaskEvent
            {
                ProjectId = task.ProjectId,
                TaskItemId = task.Id,
                TaskType = task.Type,
                EventType = TaskEventType.BackfilledSnapshot,
                ActorDiscordId = task.CreatedById,
                OccurredAtUtc = task.CreatedAtUtc.UtcDateTime,
                LocalDate = TimeZoneInfo.ConvertTime(task.CreatedAtUtc, _studioTime.TimeZone).Date,
                TitleSnapshot = task.Title,
                DescriptionSnapshot = task.Description,
                ToStatus = task.Status,
                ToAssigneeId = task.AssigneeId,
                ToSprintId = task.SprintId,
                ToPoints = task.Points,
                Summary = $"Backfill snapshot for {task.Title}",
                Source = "knowledge_backfill"
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Backfilled {Count} task events for project {ProjectId}", tasksWithoutEvents.Count, projectId);
    }

    private List<MemberDailySignal> BuildMemberSignals(
        int projectId,
        DateTime fromDate,
        IReadOnlyList<ProjectMemoryMessage> messages,
        IReadOnlyList<AssistantStandupEntry> standups,
        IReadOnlyList<TaskEvent> taskEvents,
        IReadOnlyList<TaskCreationSeed> taskCreations,
        IReadOnlyList<AssistantMemberWorkload> memberWorkloads,
        HashSet<ulong> ignoredUserIds)
    {
        var today = _studioTime.LocalDate.Date;
        var reportLookup = standups.ToLookup(x => (x.DiscordUserId, x.Date.Date));
        var messageLookup = messages.ToLookup(x => (x.AuthorId, x.LocalDate.Date));
        var eventLookup = taskEvents.Where(x => x.ActorDiscordId.HasValue).ToLookup(x => (x.ActorDiscordId!.Value, x.LocalDate.Date));
        var currentWorkloads = memberWorkloads.ToDictionary(x => x.DiscordUserId);
        var firstActiveDates = new Dictionary<ulong, DateTime>();

        foreach (var message in messages)
        {
            if (!ignoredUserIds.Contains(message.AuthorId))
            {
                ObserveFirstActive(firstActiveDates, message.AuthorId, message.LocalDate.Date);
            }
        }

        foreach (var standup in standups)
        {
            if (!ignoredUserIds.Contains(standup.DiscordUserId))
            {
                ObserveFirstActive(firstActiveDates, standup.DiscordUserId, standup.Date.Date);
            }
        }

        foreach (var taskEvent in taskEvents)
        {
            if (taskEvent.ActorDiscordId.HasValue && !ignoredUserIds.Contains(taskEvent.ActorDiscordId.Value))
            {
                ObserveFirstActive(firstActiveDates, taskEvent.ActorDiscordId.Value, taskEvent.LocalDate.Date);
            }

            if (taskEvent.ToAssigneeId.HasValue && !ignoredUserIds.Contains(taskEvent.ToAssigneeId.Value))
            {
                ObserveFirstActive(firstActiveDates, taskEvent.ToAssigneeId.Value, taskEvent.LocalDate.Date);
            }
        }

        foreach (var task in taskCreations)
        {
            if (task.AssigneeId.HasValue && !ignoredUserIds.Contains(task.AssigneeId.Value))
            {
                ObserveFirstActive(firstActiveDates, task.AssigneeId.Value, TimeZoneInfo.ConvertTime(task.CreatedAtUtc, _studioTime.TimeZone).Date);
            }
        }

        var result = new List<MemberDailySignal>();
        foreach (var pair in firstActiveDates.OrderBy(x => x.Key))
        {
            var signalStartDate = pair.Value.Date < fromDate.Date
                ? fromDate.Date
                : pair.Value.Date;

            for (var date = signalStartDate; date <= today; date = date.AddDays(1))
            {
                var standup = reportLookup[(pair.Key, date)].FirstOrDefault();
                var messageCount = messageLookup[(pair.Key, date)].Count();
                var eventsForDay = eventLookup[(pair.Key, date)].ToList();
                var duePassed = date.Date < today || _studioTime.LocalNow.TimeOfDay >= StandupDueTime;
                var expectedStandup = duePassed && date.Date >= fromDate.Date;
                var completedTasks = eventsForDay.Count(x => x.EventType == TaskEventType.Completed);
                var fixedBugs = eventsForDay.Count(x => x.EventType == TaskEventType.BugFixed);
                var submitted = standup is not null;
                var hasBlocker = standup?.HasBlockers == true;
                var lateMinutes = submitted ? (int?)CalculateLateMinutes(standup!.ReportedAtLocal, date) : null;
                var wasLate = lateMinutes.GetValueOrDefault() > 0;
                var reliability = 100;

                if (expectedStandup && !submitted)
                {
                    reliability -= 25;
                }
                else if (wasLate && lateMinutes.HasValue)
                {
                    reliability -= Math.Min(15, 5 + (lateMinutes.Value / 15));
                }

                if (hasBlocker)
                {
                    reliability -= 5;
                }

                reliability += Math.Min(10, completedTasks * 4 + fixedBugs * 4 + messageCount);
                reliability = Math.Clamp(reliability, 0, 100);

                var evidence = new List<string>();
                if (messageCount > 0)
                {
                    evidence.Add($"{messageCount} project messages");
                }

                if (eventsForDay.Count > 0)
                {
                    evidence.Add($"{eventsForDay.Count} task events");
                }

                if (submitted)
                {
                    evidence.Add(wasLate && lateMinutes.HasValue
                        ? $"standup late {lateMinutes.Value} min"
                        : "standup on time");
                }
                else if (expectedStandup)
                {
                    evidence.Add("standup missing");
                }

                if (hasBlocker)
                {
                    evidence.Add("reported blocker");
                }

                currentWorkloads.TryGetValue(pair.Key, out var workload);
                result.Add(new MemberDailySignal
                {
                    ProjectId = projectId,
                    DiscordUserId = pair.Key,
                    LocalDate = date,
                    ExpectedStandup = expectedStandup,
                    SubmittedStandup = submitted,
                    WasLate = wasLate,
                    LateMinutes = lateMinutes,
                    HasBlocker = hasBlocker,
                    CompletedTasks = completedTasks,
                    FixedBugs = fixedBugs,
                    ActivityCount = messageCount + eventsForDay.Count,
                    OpenTaskCount = date.Date == today ? workload?.OpenTaskCount ?? 0 : 0,
                    OpenBugCount = date.Date == today ? workload?.OpenBugCount ?? 0 : 0,
                    OpenPoints = date.Date == today ? workload?.OpenPoints ?? 0 : 0,
                    ReliabilityScore = reliability,
                    EvidenceJson = Serialize(evidence),
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
        }

        return result;
    }

    private List<MemberProfile> BuildMemberProfiles(
        int projectId,
        IReadOnlyList<ProjectMemoryMessage> messages,
        IReadOnlyList<AssistantStandupEntry> standups,
        IReadOnlyList<TaskEvent> taskEvents,
        IReadOnlyList<CompletedWorkSeed> completedWorkItems,
        IReadOnlyDictionary<int, string> sprintNameLookup,
        IReadOnlyList<MemberDailySignal> memberSignals,
        IReadOnlyList<AssistantMemberWorkload> memberWorkloads,
        IReadOnlyDictionary<ulong, string> displayNameLookup,
        HashSet<ulong> ignoredUserIds)
    {
        var workloadsByUser = memberWorkloads.ToDictionary(x => x.DiscordUserId);
        var messageGroups = messages.GroupBy(x => x.AuthorId).ToDictionary(x => x.Key, x => x.ToList());
        var standupGroups = standups.GroupBy(x => x.DiscordUserId).ToDictionary(x => x.Key, x => x.ToList());
        var eventGroups = taskEvents
            .Where(x => x.ActorDiscordId.HasValue)
            .GroupBy(x => x.ActorDiscordId!.Value)
            .ToDictionary(x => x.Key, x => x.ToList());
        var completedWorkGroups = completedWorkItems
            .GroupBy(x => x.AssigneeId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var signalGroups = memberSignals.GroupBy(x => x.DiscordUserId).ToDictionary(x => x.Key, x => x.ToList());
        var memberIds = messageGroups.Keys
            .Concat(eventGroups.Keys)
            .Concat(completedWorkGroups.Keys)
            .Concat(signalGroups.Keys)
            .Concat(workloadsByUser.Keys)
            .Distinct()
            .Where(x => !ignoredUserIds.Contains(x))
            .ToList();

        return memberIds.Select(userId =>
        {
            var memberMessages = messageGroups.GetValueOrDefault(userId) ?? [];
            var memberStandups = standupGroups.GetValueOrDefault(userId) ?? [];
            var memberEvents = eventGroups.GetValueOrDefault(userId) ?? [];
            var memberCompletedWork = completedWorkGroups.GetValueOrDefault(userId) ?? [];
            var signals = signalGroups.GetValueOrDefault(userId) ?? [];
            var workload = workloadsByUser.GetValueOrDefault(userId);
            var expectedSignals = signals.Where(x => x.ExpectedStandup).ToList();
            var submittedSignals = signals.Where(x => x.SubmittedStandup).ToList();
            var lateSignals = submittedSignals.Where(x => x.WasLate && x.LateMinutes.HasValue).ToList();
            var missingStandupDays = expectedSignals.Count(x => !x.SubmittedStandup);
            var lateRatePercent = submittedSignals.Count == 0
                ? 0
                : (int)Math.Round((double)lateSignals.Count / submittedSignals.Count * 100, MidpointRounding.AwayFromZero);
            var averageLateMinutes = lateSignals.Count == 0
                ? null
                : (int?)Math.Round(lateSignals.Average(x => x.LateMinutes!.Value), MidpointRounding.AwayFromZero);
            var blockerDays = signals.Count(x => x.HasBlocker);
            var completedTasksRecent = signals.Sum(x => x.CompletedTasks);
            var fixedBugsRecent = signals.Sum(x => x.FixedBugs);
            var completedTasksAllTime = memberCompletedWork.Count(x => x.TaskType == TaskItemType.Task);
            var fixedBugsAllTime = memberCompletedWork.Count(x => x.TaskType == TaskItemType.Bug);
            var activeChannels = memberMessages
                .GroupBy(x => x.ChannelName)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(x => x.Key)
                .ToList();
            var dominantTopics = CollectDominantTopics(memberMessages, memberStandups, memberEvents)
                .Take(4)
                .ToList();
            var reliability = signals.Count == 0 ? 50 : (int)Math.Round(signals.Average(x => x.ReliabilityScore));
            var confidence = Math.Clamp(30 + memberMessages.Count * 3 + memberEvents.Count * 2 + signals.Count * 4, 25, 95);
            var currentFocusSummary = BuildCurrentFocusSummary(memberMessages, memberStandups, memberEvents, workload, dominantTopics);
            var recentOutputSummary = BuildRecentOutputSummary(completedTasksRecent, fixedBugsRecent, memberEvents);
            var historicalOutputSummary = BuildHistoricalOutputSummary(completedTasksAllTime, fixedBugsAllTime, memberCompletedWork, sprintNameLookup);
            var standupSummary = BuildStandupSummary(expectedSignals.Count, submittedSignals.Count, missingStandupDays, lateSignals.Count, lateRatePercent, averageLateMinutes, blockerDays);
            var riskSummary = BuildMemberRiskSummary(workload, missingStandupDays, lateRatePercent, blockerDays);

            return new MemberProfile
            {
                ProjectId = projectId,
                DiscordUserId = userId,
                DisplayName = ResolveDisplayName(displayNameLookup, userId),
                RoleSummary = ResolveRoleSummary(workload, memberEvents),
                SkillKeywordsJson = Serialize(dominantTopics),
                DominantTopicsJson = Serialize(dominantTopics),
                ActiveChannelsJson = Serialize(activeChannels),
                TotalMessageCount = memberMessages.Count,
                TotalStandupReports = submittedSignals.Count,
                TotalTaskEvents = memberEvents.Count,
                MissingStandupDays = missingStandupDays,
                LateStandupRatePercent = lateRatePercent,
                AverageLateMinutes = averageLateMinutes,
                BlockerDays = blockerDays,
                CompletedTasksRecent = completedTasksRecent,
                FixedBugsRecent = fixedBugsRecent,
                CompletedTasksAllTime = completedTasksAllTime,
                FixedBugsAllTime = fixedBugsAllTime,
                OpenTaskCount = workload?.OpenTaskCount ?? 0,
                OpenBugCount = workload?.OpenBugCount ?? 0,
                OpenPoints = workload?.OpenPoints ?? 0,
                ReliabilityScore = reliability,
                ConfidencePercent = confidence,
                StandupSummary = standupSummary,
                CurrentFocusSummary = currentFocusSummary,
                RecentOutputSummary = recentOutputSummary,
                HistoricalOutputSummary = historicalOutputSummary,
                RiskSummary = riskSummary,
                EvidenceSummary = $"{KnowledgeLookbackDays}d window: {memberMessages.Count} messages, {submittedSignals.Count} standups, {memberEvents.Count} task events | all-time done {completedTasksAllTime} task, fixed {fixedBugsAllTime} bug",
                LastSignalDate = signals.OrderByDescending(x => x.LocalDate).FirstOrDefault()?.LocalDate,
                LastSeenAtUtc = memberMessages.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault()?.CreatedAtUtc.UtcDateTime,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }).ToList();
    }

    private List<TopicMention> BuildTopicMentions(
        int projectId,
        DateTime fromDate,
        IReadOnlyList<ProjectMemoryMessage> messages,
        IReadOnlyList<ProjectDailyDigest> digests)
    {
        var result = new List<TopicMention>();
        foreach (var date in Enumerable.Range(0, TopicLookbackDays).Select(offset => fromDate.Date.AddDays(offset)))
        {
            var messagesForDate = messages.Where(x => x.LocalDate.Date == date.Date && !x.IsBot).ToList();
            var digest = digests.FirstOrDefault(x => x.LocalDate.Date == date.Date);
            var topicBuckets = new Dictionary<string, List<ProjectMemoryMessage>>(StringComparer.OrdinalIgnoreCase);

            foreach (var message in messagesForDate)
            {
                foreach (var topicKey in DetectTopicKeys(message.NormalizedContent))
                {
                    if (!topicBuckets.TryGetValue(topicKey, out var bucket))
                    {
                        bucket = [];
                        topicBuckets[topicKey] = bucket;
                    }

                    bucket.Add(message);
                }
            }

            foreach (var pair in topicBuckets)
            {
                result.Add(new TopicMention
                {
                    ProjectId = projectId,
                    LocalDate = date,
                    TopicKey = pair.Key,
                    MentionCount = pair.Value.Count,
                    DistinctAuthorCount = pair.Value.Select(x => x.AuthorId).Distinct().Count(),
                    TopChannelsJson = Serialize(pair.Value.GroupBy(x => x.ChannelName).OrderByDescending(x => x.Count()).Take(3).Select(x => x.Key)),
                    TopAuthorsJson = Serialize(pair.Value.GroupBy(x => x.AuthorName).OrderByDescending(x => x.Count()).Take(3).Select(x => x.Key)),
                    SourceSummary = digest is null ? $"Daily topic signal for {pair.Key}" : digest.Summary,
                    EvidenceJson = Serialize(pair.Value.Take(3).Select(x => TrimTo(x.Content, 120))),
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
        }

        return result;
    }

    private List<DecisionLog> BuildDecisionLogs(int projectId, DateTime fromDate, IReadOnlyList<ProjectMemoryMessage> messages)
    {
        return messages
            .Where(x => !x.IsBot && x.LocalDate >= fromDate)
            .Where(x => ContainsAny(x.NormalizedContent, DecisionCues))
            .GroupBy(x => $"{x.LocalDate:yyyy-MM-dd}:{TrimTo(x.NormalizedContent, 80)}", StringComparer.Ordinal)
            .Select(group =>
            {
                var message = group.First();
                var topicKey = DetectTopicKeys(message.NormalizedContent).FirstOrDefault() ?? "general";
                return new DecisionLog
                {
                    ProjectId = projectId,
                    LocalDate = message.LocalDate.Date,
                    TopicKey = topicKey,
                    Summary = TrimTo(message.Content, 180),
                    Evidence = $"Message by {message.AuthorName} in #{message.ChannelName}",
                    ConfidencePercent = topicKey == "general" ? 60 : 75,
                    SourceMessageId = message.MessageId,
                    SourceChannelName = message.ChannelName,
                    CreatedAtUtc = DateTime.UtcNow
                };
            })
            .Take(20)
            .ToList();
    }

    private List<RiskLog> BuildRiskLogs(
        int projectId,
        DateTime today,
        IReadOnlyList<ProjectMemoryMessage> messages,
        IReadOnlyList<AssistantStandupEntry> standups,
        IReadOnlyList<AssistantAttentionItem> attentionItems,
        IReadOnlyList<AssistantStalledTask> stalledTasks,
        IReadOnlyDictionary<ulong, string> displayNameLookup)
    {
        var result = new List<RiskLog>();

        result.AddRange(standups
            .Where(x => x.HasBlockers)
            .Select(x => new RiskLog
            {
                ProjectId = projectId,
                LocalDate = x.Date.Date,
                RiskKey = "standup_blocker",
                Severity = "high",
                Summary = TrimTo(x.Blockers, 180),
                Evidence = $"Standup blocker from {ResolveDisplayName(displayNameLookup, x.DiscordUserId)} on {x.Date:yyyy-MM-dd}",
                ConfidencePercent = 90,
                CreatedAtUtc = DateTime.UtcNow
            }));

        result.AddRange(attentionItems.Take(8).Select(item => new RiskLog
        {
            ProjectId = projectId,
            LocalDate = today,
            RiskKey = item.Kind,
            Severity = item.Kind is "overdue_task" or "stalled_task" ? "high" : "medium",
            Summary = TrimTo(item.Summary, 180),
            Evidence = item.TaskId.HasValue ? $"Task #{item.TaskId.Value} {item.Title}" : item.Title,
            ConfidencePercent = 80,
            CreatedAtUtc = DateTime.UtcNow
        }));

        result.AddRange(messages
            .Where(x => !x.IsBot && x.LocalDate >= today.AddDays(-(TopicLookbackDays - 1)))
            .Where(x => ContainsAny(x.NormalizedContent, RiskCues))
            .Take(10)
            .Select(x => new RiskLog
            {
                ProjectId = projectId,
                LocalDate = x.LocalDate.Date,
                RiskKey = DetectTopicKeys(x.NormalizedContent).FirstOrDefault() ?? "general_risk",
                Severity = "medium",
                Summary = TrimTo(x.Content, 180),
                Evidence = $"Message by {x.AuthorName} in #{x.ChannelName}",
                ConfidencePercent = 60,
                CreatedAtUtc = DateTime.UtcNow
            }));

        foreach (var stalled in stalledTasks.Take(5))
        {
            result.Add(new RiskLog
            {
                ProjectId = projectId,
                LocalDate = today,
                RiskKey = "stalled_task",
                Severity = stalled.IsOverdue ? "high" : "medium",
                Summary = $"{stalled.Title}: {stalled.Reason}",
                Evidence = stalled.Evidence,
                ConfidencePercent = 85,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        return result
            .GroupBy(x => $"{x.LocalDate:yyyy-MM-dd}:{x.RiskKey}:{x.Summary}", StringComparer.Ordinal)
            .Select(x => x.First())
            .Take(24)
            .ToList();
    }

    private async Task<AssistantProjectKnowledge> LoadAssistantKnowledgeAsync(
        BotDbContext db,
        int projectId,
        DateTime fromDate,
        IReadOnlyList<MemberProfile> memberProfiles,
        IReadOnlyList<MemberDailySignal> memberSignals,
        IReadOnlyList<TopicMention> topicMentions,
        IReadOnlyList<DecisionLog> decisionLogs,
        IReadOnlyList<RiskLog> riskLogs,
        CancellationToken cancellationToken)
    {
        var sprintTrend = await db.SprintDailySnapshots
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate)
            .OrderByDescending(x => x.LocalDate)
            .Take(TopicLookbackDays)
            .ToListAsync(cancellationToken);

        var riskTrend = await db.ProjectRiskSnapshots
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate >= fromDate)
            .OrderByDescending(x => x.LocalDate)
            .Take(TopicLookbackDays)
            .ToListAsync(cancellationToken);

        return new AssistantProjectKnowledge(
            Members: memberProfiles
                .OrderByDescending(x => x.OpenPoints)
                .ThenBy(x => x.ReliabilityScore)
                .Take(10)
                .Select(MapMemberProfile)
                .ToList(),
            MemberSignals: memberSignals
                .OrderByDescending(x => x.LocalDate)
                .ThenBy(x => x.ReliabilityScore)
                .Take(18)
                .Select(MapMemberSignal)
                .ToList(),
            Topics: topicMentions
                .GroupBy(x => x.TopicKey)
                .Select(MapTopic)
                .OrderByDescending(x => x.MentionCount)
                .ThenByDescending(x => x.DistinctAuthorCount)
                .Take(8)
                .ToList(),
            Decisions: decisionLogs
                .OrderByDescending(x => x.LocalDate)
                .ThenByDescending(x => x.ConfidencePercent)
                .Take(8)
                .Select(x => new AssistantDecisionEntry(x.LocalDate, x.TopicKey, x.Summary, x.Evidence, x.ConfidencePercent, x.SourceChannelName))
                .ToList(),
            Risks: riskLogs
                .OrderByDescending(x => x.LocalDate)
                .ThenByDescending(x => GetSeverityRank(x.Severity))
                .Take(10)
                .Select(x => new AssistantRiskEntry(x.LocalDate, x.RiskKey, x.Severity, x.Summary, x.Evidence, x.ConfidencePercent))
                .ToList(),
            SprintTrend: sprintTrend
                .Select(x => new AssistantSprintTrendPoint(x.LocalDate, x.DeliveryProgressPercent, x.ScheduleProgressPercent, x.OpenBugCount, x.StalledTaskCount, x.HealthLabel, x.HealthDeltaPercent))
                .ToList(),
            RiskTrend: riskTrend
                .Select(x => new AssistantRiskTrendPoint(x.LocalDate, x.RiskScore, x.OpenRiskCount, x.OverdueTaskCount, x.StalledTaskCount, x.MissingStandupCount, x.OpenBugCount, x.BlockerCount, x.Summary))
                .ToList());
    }

    private void UpsertMemberProfiles(BotDbContext db, int projectId, IReadOnlyList<MemberProfile> profiles)
    {
        var existing = db.MemberProfiles.Where(x => x.ProjectId == projectId).ToList();
        db.MemberProfiles.RemoveRange(existing.Where(x => profiles.All(p => p.DiscordUserId != x.DiscordUserId)));

        foreach (var profile in profiles)
        {
            var current = existing.FirstOrDefault(x => x.DiscordUserId == profile.DiscordUserId);
            if (current is null)
            {
                db.MemberProfiles.Add(profile);
                continue;
            }

            current.DisplayName = profile.DisplayName;
            current.RoleSummary = profile.RoleSummary;
            current.SkillKeywordsJson = profile.SkillKeywordsJson;
            current.DominantTopicsJson = profile.DominantTopicsJson;
            current.ActiveChannelsJson = profile.ActiveChannelsJson;
            current.TotalMessageCount = profile.TotalMessageCount;
            current.TotalStandupReports = profile.TotalStandupReports;
            current.TotalTaskEvents = profile.TotalTaskEvents;
            current.MissingStandupDays = profile.MissingStandupDays;
            current.LateStandupRatePercent = profile.LateStandupRatePercent;
            current.AverageLateMinutes = profile.AverageLateMinutes;
            current.BlockerDays = profile.BlockerDays;
            current.CompletedTasksRecent = profile.CompletedTasksRecent;
            current.FixedBugsRecent = profile.FixedBugsRecent;
            current.CompletedTasksAllTime = profile.CompletedTasksAllTime;
            current.FixedBugsAllTime = profile.FixedBugsAllTime;
            current.OpenTaskCount = profile.OpenTaskCount;
            current.OpenBugCount = profile.OpenBugCount;
            current.OpenPoints = profile.OpenPoints;
            current.ReliabilityScore = profile.ReliabilityScore;
            current.ConfidencePercent = profile.ConfidencePercent;
            current.StandupSummary = profile.StandupSummary;
            current.CurrentFocusSummary = profile.CurrentFocusSummary;
            current.RecentOutputSummary = profile.RecentOutputSummary;
            current.HistoricalOutputSummary = profile.HistoricalOutputSummary;
            current.RiskSummary = profile.RiskSummary;
            current.EvidenceSummary = profile.EvidenceSummary;
            current.LastSignalDate = profile.LastSignalDate;
            current.LastSeenAtUtc = profile.LastSeenAtUtc;
            current.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private void UpsertSprintSnapshot(
        BotDbContext db,
        int projectId,
        DateTime today,
        AssistantSprintSnapshot sprint,
        IReadOnlyList<AssistantStalledTask> stalledTasks,
        IReadOnlyList<AssistantAttentionItem> attentionItems)
    {
        if (!sprint.HasActiveSprint || !sprint.SprintId.HasValue)
        {
            return;
        }

        var current = db.SprintDailySnapshots
            .Where(x => x.ProjectId == projectId && x.SprintId == sprint.SprintId.Value && x.LocalDate == today)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
        if (current is null)
        {
            current = new SprintDailySnapshot { ProjectId = projectId, SprintId = sprint.SprintId.Value, LocalDate = today };
            db.SprintDailySnapshots.Add(current);
        }

        current.TotalTasks = sprint.TotalTasks;
        current.DoneTasks = sprint.DoneTasks;
        current.InProgressTasks = sprint.InProgressTasks;
        current.BacklogTasksInSprint = sprint.BacklogTasksInSprint;
        current.OpenBugCount = sprint.OpenBugCount;
        current.TotalPoints = sprint.TotalPoints;
        current.DonePoints = sprint.DonePoints;
        current.InProgressPoints = sprint.InProgressPoints;
        current.DeliveryProgressPercent = sprint.DeliveryProgressPercent;
        current.ScheduleProgressPercent = sprint.ScheduleProgressPercent;
        current.StalledTaskCount = stalledTasks.Count;
        current.OverdueTaskCount = attentionItems.Count(x => x.Kind == "overdue_task");
        current.HealthLabel = sprint.Health.Label;
        current.HealthDeltaPercent = sprint.Health.DeltaPercent;
        current.GeneratedAtUtc = DateTime.UtcNow;
    }

    private void UpsertRiskSnapshot(
        BotDbContext db,
        int projectId,
        DateTime today,
        IReadOnlyList<AssistantStandupEntry> standups,
        AssistantStandupDisciplineSummary standupDiscipline,
        IReadOnlyList<AssistantAttentionItem> attentionItems,
        IReadOnlyList<AssistantStalledTask> stalledTasks,
        int openBugCount)
    {
        var current = db.ProjectRiskSnapshots
            .Where(x => x.ProjectId == projectId && x.LocalDate == today)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
        if (current is null)
        {
            current = new ProjectRiskSnapshot { ProjectId = projectId, LocalDate = today };
            db.ProjectRiskSnapshots.Add(current);
        }

        current.OverdueTaskCount = attentionItems.Count(x => x.Kind == "overdue_task");
        current.StalledTaskCount = stalledTasks.Count;
        current.MissingStandupCount = standupDiscipline.MissingReporters.Count(x => x.MissingToday);
        current.OpenBugCount = openBugCount;
        current.BlockerCount = standups.Count(x => x.HasBlockers);
        current.OpenRiskCount = current.OverdueTaskCount + current.StalledTaskCount + current.MissingStandupCount + current.BlockerCount;
        current.RiskScore = current.OpenRiskCount * 10 + openBugCount * 3;
        current.Summary = $"Risks: overdue {current.OverdueTaskCount}, stalled {current.StalledTaskCount}, missing standup {current.MissingStandupCount}, blockers {current.BlockerCount}, bugs {openBugCount}";
        current.GeneratedAtUtc = DateTime.UtcNow;
    }

    private static AssistantMemberProfile MapMemberProfile(MemberProfile profile)
    {
        return new AssistantMemberProfile(
            profile.DiscordUserId,
            profile.DisplayName,
            profile.RoleSummary,
            DeserializeList(profile.SkillKeywordsJson),
            DeserializeList(profile.DominantTopicsJson),
            DeserializeList(profile.ActiveChannelsJson),
            profile.MissingStandupDays,
            profile.LateStandupRatePercent,
            profile.AverageLateMinutes,
            profile.BlockerDays,
            profile.CompletedTasksRecent,
            profile.FixedBugsRecent,
            profile.CompletedTasksAllTime,
            profile.FixedBugsAllTime,
            profile.OpenTaskCount,
            profile.OpenBugCount,
            profile.OpenPoints,
            profile.ReliabilityScore,
            profile.ConfidencePercent,
            profile.StandupSummary,
            profile.CurrentFocusSummary,
            profile.RecentOutputSummary,
            profile.HistoricalOutputSummary,
            profile.RiskSummary,
            profile.EvidenceSummary);
    }

    private static AssistantMemberDailySignal MapMemberSignal(MemberDailySignal signal)
    {
        return new AssistantMemberDailySignal(
            signal.LocalDate,
            signal.DiscordUserId,
            signal.ExpectedStandup,
            signal.SubmittedStandup,
            signal.WasLate,
            signal.LateMinutes,
            signal.HasBlocker,
            signal.CompletedTasks,
            signal.FixedBugs,
            signal.ActivityCount,
            signal.OpenTaskCount,
            signal.OpenBugCount,
            signal.OpenPoints,
            signal.ReliabilityScore,
            DeserializeList(signal.EvidenceJson));
    }

    private static AssistantTopicSummary MapTopic(IGrouping<string, TopicMention> topicGroup)
    {
        var mentions = topicGroup.ToList();
        return new AssistantTopicSummary(
            topicGroup.Key,
            mentions.Sum(x => x.MentionCount),
            mentions.SelectMany(x => DeserializeList(x.TopAuthorsJson)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            mentions.SelectMany(x => DeserializeList(x.TopChannelsJson)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList(),
            mentions.SelectMany(x => DeserializeList(x.TopAuthorsJson)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList(),
            mentions.Select(x => x.SourceSummary).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? $"Topic {topicGroup.Key}");
    }

    private static IEnumerable<string> DetectTopicKeys(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return TopicKeywords
            .Where(x => x.Value.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsAny(string? value, IEnumerable<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRoleSummary(AssistantMemberWorkload? workload, IReadOnlyList<TaskEvent> events)
    {
        var assignedCount = events.Count(x => x.EventType is TaskEventType.Assigned or TaskEventType.Claimed);
        if (assignedCount >= 4)
        {
            return "coordinator";
        }

        if ((workload?.OpenBugCount ?? 0) >= Math.Max(2, workload?.OpenTaskCount ?? 0))
        {
            return "bug-owner";
        }

        if ((workload?.OpenPoints ?? 0) >= 8 || (workload?.OpenTaskCount ?? 0) >= 4)
        {
            return "main-contributor";
        }

        return "contributor";
    }

    private static IReadOnlyList<string> CollectDominantTopics(
        IReadOnlyList<ProjectMemoryMessage> memberMessages,
        IReadOnlyList<AssistantStandupEntry> memberStandups,
        IReadOnlyList<TaskEvent> memberEvents)
    {
        var topicSignals = memberMessages
            .SelectMany(x => DetectTopicKeys(x.NormalizedContent))
            .Concat(memberStandups.SelectMany(x => DetectTopicKeys($"{x.Yesterday} {x.Today} {x.Blockers}")))
            .Concat(memberEvents
                .SelectMany(x => DetectTopicKeys($"{x.TitleSnapshot} {x.DescriptionSnapshot} {x.Summary}")))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .Select(x => x.Key)
            .ToList();

        return topicSignals;
    }

    private static string BuildCurrentFocusSummary(
        IReadOnlyList<ProjectMemoryMessage> memberMessages,
        IReadOnlyList<AssistantStandupEntry> memberStandups,
        IReadOnlyList<TaskEvent> memberEvents,
        AssistantMemberWorkload? workload,
        IReadOnlyList<string> dominantTopics)
    {
        var latestStandup = memberStandups
            .OrderByDescending(x => x.Date)
            .ThenByDescending(x => x.ReportedAtLocal)
            .FirstOrDefault();

        if (latestStandup is not null && !string.IsNullOrWhiteSpace(latestStandup.Today))
        {
            var blockerText = latestStandup.HasBlockers && !string.IsNullOrWhiteSpace(latestStandup.Blockers)
                ? $" Blocker: {TrimTo(latestStandup.Blockers, 80)}"
                : string.Empty;
            return TrimTo($"{latestStandup.Today}.{blockerText}", 220);
        }

        var latestEvent = memberEvents
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();

        if (latestEvent is not null && !string.IsNullOrWhiteSpace(latestEvent.TitleSnapshot))
        {
            var action = latestEvent.EventType switch
            {
                TaskEventType.Completed => "Vua hoan thanh",
                TaskEventType.BugFixed => "Vua dong bug",
                TaskEventType.Started => "Dang trien khai",
                TaskEventType.Claimed or TaskEventType.Assigned => "Moi nhan",
                TaskEventType.BugClaimed => "Dang xu ly bug",
                _ => "Gan day dang xu ly"
            };

            return TrimTo($"{action} {latestEvent.TaskType.ToString().ToLowerInvariant()} {latestEvent.TitleSnapshot}", 220);
        }

        var recentMessage = memberMessages
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Content));

        if (recentMessage is not null)
        {
            return TrimTo(recentMessage.Content, 220);
        }

        if (dominantTopics.Count > 0)
        {
            return $"Dang tap trung vao {string.Join(", ", dominantTopics.Take(2))}.";
        }

        if (workload is not null && (workload.OpenTaskCount > 0 || workload.OpenBugCount > 0))
        {
            return $"Dang giu {workload.OpenTaskCount} task mo va {workload.OpenBugCount} bug.";
        }

        return "Chua ro focus hien tai.";
    }

    private static string BuildRecentOutputSummary(int completedTasksRecent, int fixedBugsRecent, IReadOnlyList<TaskEvent> memberEvents)
    {
        if (completedTasksRecent > 0 || fixedBugsRecent > 0)
        {
            return $"Trong {KnowledgeLookbackDays}d: done {completedTasksRecent} task, fix {fixedBugsRecent} bug.";
        }

        if (memberEvents.Count > 0)
        {
            return $"Trong {KnowledgeLookbackDays}d co {memberEvents.Count} task event, chua thay output hoan thanh ro.";
        }

        return $"Chua thay output ro trong {KnowledgeLookbackDays}d.";
    }

    private static string BuildHistoricalOutputSummary(
        int completedTasksAllTime,
        int fixedBugsAllTime,
        IReadOnlyList<CompletedWorkSeed> completedWorkItems,
        IReadOnlyDictionary<int, string> sprintNameLookup)
    {
        if (completedTasksAllTime == 0 && fixedBugsAllTime == 0)
        {
            return "Chua thay lich su task/bug done gan cho member nay trong du lieu project hien co.";
        }

        var notable = completedWorkItems
            .OrderByDescending(x => x.SprintId ?? 0)
            .ThenByDescending(x => x.TaskItemId)
            .Take(3)
            .Select(x =>
            {
                var sprintLabel = x.SprintId.HasValue && sprintNameLookup.TryGetValue(x.SprintId.Value, out var sprintName)
                    ? sprintName
                    : "ngoai sprint";
                var workLabel = x.TaskType == TaskItemType.Bug ? "bug" : "task";
                return $"{sprintLabel}: {workLabel} #{x.TaskItemId} {TrimTo(x.Title, 50)}";
            })
            .ToList();

        var notableText = notable.Count == 0 ? string.Empty : $" Noi bat: {string.Join("; ", notable)}.";
        return $"Toan project: done {completedTasksAllTime} task, fix {fixedBugsAllTime} bug.{notableText}";
    }

    private static string BuildStandupSummary(
        int expectedDays,
        int submittedDays,
        int missingDays,
        int lateDays,
        int lateRatePercent,
        int? averageLateMinutes,
        int blockerDays)
    {
        if (expectedDays <= 0)
        {
            return $"Chua du standup signal trong {KnowledgeLookbackDays}d.";
        }

        var averageLateText = averageLateMinutes.HasValue ? $", tre TB {averageLateMinutes.Value}m" : string.Empty;
        return $"Standup {submittedDays}/{expectedDays}, missing {missingDays}, late {lateDays} ({lateRatePercent}%){averageLateText}, blocker {blockerDays} ngay trong {KnowledgeLookbackDays}d.";
    }

    private static string BuildMemberRiskSummary(
        AssistantMemberWorkload? workload,
        int missingStandupDays,
        int lateStandupRatePercent,
        int blockerDays)
    {
        var parts = new List<string>();

        if (missingStandupDays > 0)
        {
            parts.Add($"thieu standup {missingStandupDays} ngay");
        }

        if (lateStandupRatePercent >= 50)
        {
            parts.Add($"tre standup {lateStandupRatePercent}%");
        }

        if (blockerDays > 0)
        {
            parts.Add($"co blocker {blockerDays} ngay");
        }

        if (workload is not null)
        {
            if (workload.OpenPoints >= 8 || workload.OpenTaskCount >= 4)
            {
                parts.Add($"workload cao ({workload.OpenTaskCount} task, {workload.OpenPoints} points)");
            }

            if (workload.OpenBugCount >= 2)
            {
                parts.Add($"{workload.OpenBugCount} bug dang mo");
            }
        }

        return parts.Count == 0
            ? "Chua thay risk member noi bat."
            : string.Join("; ", parts) + ".";
    }

    private static int GetSeverityRank(string severity) => severity switch
    {
        "high" => 3,
        "medium" => 2,
        _ => 1
    };

    private static Dictionary<ulong, string> BuildDisplayNameMap(IReadOnlyList<ProjectMemoryMessage> messages)
    {
        return messages
            .Where(x => !string.IsNullOrWhiteSpace(x.AuthorName))
            .OrderByDescending(x => x.CreatedAtUtc)
            .GroupBy(x => x.AuthorId)
            .ToDictionary(x => x.Key, x => x.First().AuthorName);
    }

    private static AssistantStandupDisciplineSummary FilterStandupDiscipline(
        AssistantStandupDisciplineSummary summary,
        HashSet<ulong> ignoredUserIds)
    {
        var lateReporters = summary.LateReporters
            .Where(x => !ignoredUserIds.Contains(x.DiscordUserId))
            .ToList();
        var missingReporters = summary.MissingReporters
            .Where(x => !ignoredUserIds.Contains(x.DiscordUserId))
            .ToList();
        var removedReporterIds = summary.LateReporters
            .Select(x => x.DiscordUserId)
            .Concat(summary.MissingReporters.Select(x => x.DiscordUserId))
            .Where(ignoredUserIds.Contains)
            .Distinct()
            .Count();

        return new AssistantStandupDisciplineSummary(
            summary.LookbackDays,
            summary.DueTimeLocal,
            Math.Max(0, summary.ExpectedReporterCount - removedReporterIds),
            lateReporters,
            missingReporters);
    }

    private static string ResolveDisplayName(IReadOnlyDictionary<ulong, string> displayNameLookup, ulong userId)
    {
        return displayNameLookup.TryGetValue(userId, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : $"<@{userId}>";
    }

    private static void ObserveFirstActive(IDictionary<ulong, DateTime> map, ulong discordUserId, DateTime date)
    {
        if (!map.TryGetValue(discordUserId, out var current) || date < current)
        {
            map[discordUserId] = date.Date;
        }
    }

    private static int CalculateLateMinutes(DateTimeOffset reportedAtLocal, DateTime standupDate)
    {
        var dueAt = standupDate.Date.Add(StandupDueTime);
        return Math.Max(0, (int)Math.Round((reportedAtLocal.DateTime - dueAt).TotalMinutes));
    }

    private static void ReplaceDateRange<TEntity>(DbSet<TEntity> set, IQueryable<TEntity> query)
        where TEntity : class
    {
        set.RemoveRange(query);
    }

    private static string Serialize(IEnumerable<string> values) => JsonSerializer.Serialize(values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), JsonOptions);

    private static IReadOnlyList<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = value.Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength];
    }
}

public sealed record AssistantProjectKnowledge(
    IReadOnlyList<AssistantMemberProfile> Members,
    IReadOnlyList<AssistantMemberDailySignal> MemberSignals,
    IReadOnlyList<AssistantTopicSummary> Topics,
    IReadOnlyList<AssistantDecisionEntry> Decisions,
    IReadOnlyList<AssistantRiskEntry> Risks,
    IReadOnlyList<AssistantSprintTrendPoint> SprintTrend,
    IReadOnlyList<AssistantRiskTrendPoint> RiskTrend);

public sealed record AssistantMemberProfile(
    ulong DiscordUserId,
    string DisplayName,
    string RoleSummary,
    IReadOnlyList<string> SkillKeywords,
    IReadOnlyList<string> DominantTopics,
    IReadOnlyList<string> ActiveChannels,
    int MissingStandupDays,
    int LateStandupRatePercent,
    int? AverageLateMinutes,
    int BlockerDays,
    int CompletedTasksRecent,
    int FixedBugsRecent,
    int CompletedTasksAllTime,
    int FixedBugsAllTime,
    int OpenTaskCount,
    int OpenBugCount,
    int OpenPoints,
    int ReliabilityScore,
    int ConfidencePercent,
    string StandupSummary,
    string CurrentFocusSummary,
    string RecentOutputSummary,
    string HistoricalOutputSummary,
    string RiskSummary,
    string EvidenceSummary);

public sealed record AssistantMemberDailySignal(
    DateTime Date,
    ulong DiscordUserId,
    bool ExpectedStandup,
    bool SubmittedStandup,
    bool WasLate,
    int? LateMinutes,
    bool HasBlocker,
    int CompletedTasks,
    int FixedBugs,
    int ActivityCount,
    int OpenTaskCount,
    int OpenBugCount,
    int OpenPoints,
    int ReliabilityScore,
    IReadOnlyList<string> Evidence);

public sealed record AssistantTopicSummary(
    string TopicKey,
    int MentionCount,
    int DistinctAuthorCount,
    IReadOnlyList<string> TopChannels,
    IReadOnlyList<string> TopAuthors,
    string Summary);

public sealed record AssistantDecisionEntry(
    DateTime Date,
    string TopicKey,
    string Summary,
    string Evidence,
    int ConfidencePercent,
    string? SourceChannelName);

public sealed record AssistantRiskEntry(
    DateTime Date,
    string RiskKey,
    string Severity,
    string Summary,
    string Evidence,
    int ConfidencePercent);

public sealed record AssistantSprintTrendPoint(
    DateTime Date,
    int DeliveryProgressPercent,
    int? ScheduleProgressPercent,
    int OpenBugCount,
    int StalledTaskCount,
    string HealthLabel,
    int? HealthDeltaPercent);

public sealed record AssistantRiskTrendPoint(
    DateTime Date,
    int RiskScore,
    int OpenRiskCount,
    int OverdueTaskCount,
    int StalledTaskCount,
    int MissingStandupCount,
    int OpenBugCount,
    int BlockerCount,
    string Summary);

internal sealed record TaskCreationSeed(int TaskItemId, ulong? AssigneeId, DateTimeOffset CreatedAtUtc);
internal sealed record CompletedWorkSeed(ulong AssigneeId, TaskItemType TaskType, int TaskItemId, string Title, int Points, int? SprintId);
