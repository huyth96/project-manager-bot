using Discord;

namespace ProjectManagerBot.Services;

public sealed class ProjectWeeklyReviewService(
    ProjectInsightService projectInsightService,
    NotificationService notificationService,
    StudioTimeService studioTime,
    ILogger<ProjectWeeklyReviewService> logger)
{
    private readonly ProjectInsightService _projectInsightService = projectInsightService;
    private readonly NotificationService _notificationService = notificationService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly ILogger<ProjectWeeklyReviewService> _logger = logger;

    public async Task<bool> SendWeeklyReviewAsync(int projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var embed = await BuildWeeklyReviewEmbedAsync(projectId, cancellationToken);
            if (embed is null)
            {
                return false;
            }

            await _notificationService.SendProjectFeedAsync(projectId, embed, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the gui weekly review cho project {ProjectId}", projectId);
            return false;
        }
    }

    public async Task<Embed?> BuildWeeklyReviewEmbedAsync(int projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await _projectInsightService.BuildProjectContextAsync(
                projectId,
                question: "weekly sprint review va project review",
                cancellationToken);

            if (context is null)
            {
                return null;
            }

            return new EmbedBuilder()
                .WithTitle($"Weekly Review • {context.Scope.ProjectName}")
                .WithColor(ResolveColor(context.Sprint.Health.Label))
                .WithDescription(BuildDescription(context))
                .AddField("Sprint Trend", BuildSprintTrend(context), false)
                .AddField("Member Signals", BuildMemberSignals(context), false)
                .AddField("Topics And Decisions", BuildTopicAndDecisionSection(context), false)
                .AddField("Risks", BuildRiskSection(context), false)
                .AddField("Actions", BuildActionSection(context), false)
                .WithCurrentTimestamp()
                .Build();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the dung weekly review cho project {ProjectId}", projectId);
            return null;
        }
    }

    private string BuildDescription(ProjectAssistantContext context)
    {
        var start = _studioTime.LocalDate.AddDays(-6);
        var health = context.Sprint.HasActiveSprint
            ? $"Sprint `{context.Sprint.Name}` health `{context.Sprint.Health.Label}`."
            : "Chua co sprint active.";

        return $"Window `{start:yyyy-MM-dd}` -> `{_studioTime.LocalDate:yyyy-MM-dd}`.\n" +
               $"{health} Done `{context.Sprint.DonePoints}/{context.Sprint.TotalPoints}` points, open bug `{context.Sprint.OpenBugCount}`.";
    }

    private static string BuildSprintTrend(ProjectAssistantContext context)
    {
        if (!context.Knowledge.SprintTrend.Any())
        {
            return "- Chua co sprint snapshot lich su.";
        }

        var lines = context.Knowledge.SprintTrend
            .OrderBy(x => x.Date)
            .TakeLast(5)
            .Select(x =>
                $"- {x.Date:MM-dd}: delivery `{x.DeliveryProgressPercent}%`, schedule `{x.ScheduleProgressPercent?.ToString() ?? "?"}%`, stalled `{x.StalledTaskCount}`, bugs `{x.OpenBugCount}`, health `{x.HealthLabel}`")
            .ToList();

        return string.Join("\n", lines);
    }

    private static string BuildMemberSignals(ProjectAssistantContext context)
    {
        var riskyMembers = context.Knowledge.Members
            .OrderBy(x => x.ReliabilityScore)
            .ThenByDescending(x => x.OpenPoints)
            .Take(3)
            .ToList();

        if (riskyMembers.Count == 0)
        {
            return "- Chua co du lieu member profile.";
        }

        return string.Join("\n", riskyMembers.Select(x =>
            $"- {x.DisplayName} (<@{x.DiscordUserId}>) `{x.RoleSummary}` | reliability `{x.ReliabilityScore}` | open `{x.OpenTaskCount}` task / `{x.OpenBugCount}` bug / `{x.OpenPoints}` points | standup `{x.StandupSummary}` | risk {x.RiskSummary}"));
    }

    private static string BuildTopicAndDecisionSection(ProjectAssistantContext context)
    {
        var topicLines = context.Knowledge.Topics
            .Take(3)
            .Select(x => $"- Topic `{x.TopicKey}`: `{x.MentionCount}` mentions, authors `{x.DistinctAuthorCount}`")
            .ToList();

        var decisionLines = context.Knowledge.Decisions
            .Take(3)
            .Select(x => $"- {x.Date:MM-dd} `{x.TopicKey}`: {x.Summary}")
            .ToList();

        if (topicLines.Count == 0 && decisionLines.Count == 0)
        {
            return "- Chua co topic/decision noi bat trong 7 ngay gan day.";
        }

        return string.Join("\n", topicLines.Concat(decisionLines));
    }

    private static string BuildRiskSection(ProjectAssistantContext context)
    {
        var riskLines = context.Knowledge.Risks
            .Take(4)
            .Select(x => $"- [{x.Severity}] {x.RiskKey}: {x.Summary}")
            .ToList();

        var latestSnapshot = context.Knowledge.RiskTrend.FirstOrDefault();
        if (latestSnapshot is not null)
        {
            riskLines.Insert(0, $"- Risk score `{latestSnapshot.RiskScore}` | overdue `{latestSnapshot.OverdueTaskCount}` | stalled `{latestSnapshot.StalledTaskCount}` | missing standup `{latestSnapshot.MissingStandupCount}` | blockers `{latestSnapshot.BlockerCount}`");
        }

        return riskLines.Count == 0
            ? "- Khong co risk log noi bat."
            : string.Join("\n", riskLines);
    }

    private static string BuildActionSection(ProjectAssistantContext context)
    {
        var actions = new List<string>();

        var missingToday = context.StandupDiscipline.MissingReporters.Where(x => x.MissingToday).Take(2).ToList();
        if (missingToday.Count > 0)
        {
            actions.Add($"- Nhac {string.Join(", ", missingToday.Select(x => $"<@{x.DiscordUserId}>"))} nop standup.");
        }

        var stalled = context.StalledTasks.Take(2).ToList();
        if (stalled.Count > 0)
        {
            actions.Add($"- Giai quyet stalled: {string.Join(", ", stalled.Select(x => $"#{x.TaskId}"))}.");
        }

        var overdue = context.AttentionItems.Where(x => x.Kind == "overdue_task").Take(2).ToList();
        if (overdue.Count > 0)
        {
            actions.Add($"- Xu ly overdue task: {string.Join(", ", overdue.Select(x => $"#{x.TaskId}"))}.");
        }

        var decisions = context.Knowledge.Decisions.Take(2).ToList();
        if (decisions.Count > 0)
        {
            actions.Add($"- Xac nhan quyet dinh moi: {string.Join(" | ", decisions.Select(x => x.Summary))}");
        }

        if (actions.Count == 0)
        {
            actions.Add("- Tiep tuc theo doi sprint health va cap nhat standup day du.");
        }

        return string.Join("\n", actions);
    }

    private static Color ResolveColor(string healthLabel) => healthLabel.ToLowerInvariant() switch
    {
        "negative" => Color.DarkRed,
        "positive" => Color.DarkGreen,
        _ => Color.Blue
    };
}
