using System.Text.RegularExpressions;
using Discord;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;

namespace ProjectManagerBot.Services;

public sealed class ProjectDailyLeadReportService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    ProjectInsightService projectInsightService,
    NotificationService notificationService,
    StudioTimeService studioTime,
    ILogger<ProjectDailyLeadReportService> logger)
{
    private static readonly Regex TaskIdRegex = new(@"#(?<id>\d+)", RegexOptions.Compiled);
    private static readonly Regex UserMentionRegex = new(@"<@!?(?<id>\d+)>", RegexOptions.Compiled);

    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly ProjectInsightService _projectInsightService = projectInsightService;
    private readonly NotificationService _notificationService = notificationService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly ILogger<ProjectDailyLeadReportService> _logger = logger;

    public async Task<bool> SendDailyLeadReportAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var embed = await BuildDailyLeadReportEmbedAsync(projectId, cancellationToken);
        if (embed is null)
        {
            return false;
        }

        await _notificationService.SendDailyLeadReportAsync(projectId, embed, cancellationToken);
        return true;
    }

    public async Task<Embed?> BuildDailyLeadReportEmbedAsync(
        int projectId,
        CancellationToken cancellationToken = default)
    {
        var context = await _projectInsightService.BuildProjectContextAsync(
            projectId,
            question: "bao cao dieu phoi hang ngay",
            cancellationToken);

        if (context is null)
        {
            return null;
        }

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var (startUtc, endUtc) = BuildUtcRangeForLocalDate(_studioTime.LocalDate);
            var createdToday = await db.TaskItems
                .AsNoTracking()
                .Where(x =>
                    x.ProjectId == projectId &&
                    x.CreatedAtUtc >= startUtc &&
                    x.CreatedAtUtc < endUtc)
                .OrderByDescending(x => x.Id)
                .ToListAsync(cancellationToken);

            var todayMemoryMessages = await db.ProjectMemoryMessages
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.LocalDate == _studioTime.LocalDate)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            var todayDigest = context.Memory.DailyDigests
                .FirstOrDefault(x => x.Date.Date == _studioTime.LocalDate.Date);

            var todayStandups = context.Standups
                .Where(x => x.Date.Date == _studioTime.LocalDate.Date)
                .OrderBy(x => x.DiscordUserId)
                .ToList();

            var color = ResolveReportColor(context.Sprint.Health.Label);
            var description = BuildDescription(context, todayStandups);

            return new EmbedBuilder()
                .WithTitle($"📊 Báo Cáo Điều Phối Hằng Ngày • {context.Scope.ProjectName}")
                .WithColor(color)
                .WithDescription(description)
                .AddField("📈 Tình Trạng Sprint", BuildSprintSection(context), false)
                .AddField("🧾 Kỷ Luật Standup", BuildStandupSection(context, todayStandups), false)
                .AddField("🚨 Task Cần Chú Ý", BuildAttentionSection(context), false)
                .AddField("🔄 Biến Động Hôm Nay", BuildDailyChangesSection(createdToday, todayMemoryMessages), false)
                .AddField("💬 Tín Hiệu Từ Chat", BuildChatSignalsSection(todayDigest), false)
                .AddField("🎯 Hành Động Ngay", BuildActionSection(context, todayStandups), false)
                .WithFooter("Thiếu báo cáo được suy ra từ task đang mở, lịch sử standup và hoạt động gần đây trong project.")
                .WithCurrentTimestamp()
                .Build();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể dựng daily lead report cho project {ProjectId}", projectId);
            return null;
        }
    }

    private string BuildDescription(ProjectAssistantContext context, IReadOnlyList<AssistantStandupEntry> todayStandups)
    {
        var lines = new List<string>
        {
            $"Ngày `{_studioTime.LocalDate:yyyy-MM-dd}`"
        };

        if (context.Sprint.HasActiveSprint)
        {
            var schedule = context.Sprint.ScheduleProgressPercent?.ToString() ?? "?";
            lines.Add(
                $"Sprint `{context.Sprint.Name}` đang ở `timeline {schedule}%` và `delivery {context.Sprint.DeliveryProgressPercent}%`.");
        }
        else
        {
            lines.Add("Hiện chưa có sprint active.");
        }

        var missingTodayCount = context.StandupDiscipline.MissingReporters.Count(x => x.MissingToday);
        var blockerTodayCount = todayStandups.Count(x => x.HasBlockers);
        lines.Add(
            $"Hôm nay có `{todayStandups.Count}` standup đã nộp, `{missingTodayCount}` người chưa nộp và `{blockerTodayCount}` blocker nổi bật.");

        return string.Join("\n", lines);
    }

    private static string BuildSprintSection(ProjectAssistantContext context)
    {
        var lines = new List<string>();

        if (!context.Sprint.HasActiveSprint)
        {
            lines.Add("- Chưa có sprint active để đánh giá health.");
            lines.Add($"- Backlog hiện có `{context.Sprint.ProjectBacklogCount}` task.");
            lines.Add($"- Bug mở hiện có `{context.Sprint.OpenBugCount}`.");
            return JoinBulletLines(lines);
        }

        lines.Add($"- Timeline đã trôi: `{context.Sprint.ScheduleProgressPercent ?? 0}%`.");
        lines.Add($"- Done points / total points: `{context.Sprint.DonePoints}/{context.Sprint.TotalPoints}`.");
        lines.Add($"- Sprint health: `{context.Sprint.Health.Label}`.");
        lines.Add($"- Đánh giá: {context.Sprint.Health.Summary}");
        return JoinBulletLines(lines);
    }

    private static string BuildStandupSection(
        ProjectAssistantContext context,
        IReadOnlyList<AssistantStandupEntry> todayStandups)
    {
        var submittedToday = todayStandups.Select(x => $"<@{x.DiscordUserId}>").Distinct().Take(6).ToList();
        var lateToday = context.StandupDiscipline.LateReporters
            .Where(x => x.WasLateLastReport && x.LastReportedAtLocal?.Date == context.GeneratedAtLocal.Date)
            .Take(5)
            .ToList();
        var missingToday = context.StandupDiscipline.MissingReporters
            .Where(x => x.MissingToday)
            .Take(5)
            .ToList();
        var blockersToday = todayStandups
            .Where(x => x.HasBlockers)
            .Take(3)
            .Select(x => $"<@{x.DiscordUserId}>: {Truncate(x.Blockers, 80)}")
            .ToList();

        var lines = new List<string>
        {
            $"- Đã nộp: `{todayStandups.Count}/{context.StandupDiscipline.ExpectedReporterCount}`" +
            (submittedToday.Count > 0 ? $" | {string.Join(", ", submittedToday)}" : string.Empty)
        };

        lines.Add(lateToday.Count == 0
            ? "- Nộp trễ hôm nay: `0`."
            : $"- Nộp trễ hôm nay: {string.Join(", ", lateToday.Select(x => $"<@{x.DiscordUserId}>"))}.");

        lines.Add(missingToday.Count == 0
            ? "- Chưa nộp hôm nay: `0`."
            : $"- Chưa nộp hôm nay: {string.Join(", ", missingToday.Select(x => $"<@{x.DiscordUserId}>"))}.");

        lines.Add(blockersToday.Count == 0
            ? "- Blocker hôm nay: `0`."
            : $"- Blocker hôm nay: {string.Join(" | ", blockersToday)}");

        return JoinBulletLines(lines);
    }

    private static string BuildAttentionSection(ProjectAssistantContext context)
    {
        var overdue = context.AttentionItems.Where(x => x.Kind == "overdue_task").Take(3).ToList();
        var stalled = context.StalledTasks.Take(3).ToList();
        var highPoint = context.AttentionItems.Where(x => x.Kind == "high_point_not_started").Take(2).ToList();
        var unassigned = context.AttentionItems.Where(x => x.Kind == "unassigned_task").Take(2).ToList();
        var openBugs = context.AttentionItems.Where(x => x.Kind == "open_bug").Take(3).ToList();

        var lines = new List<string>
        {
            overdue.Count == 0
                ? "- Task overdue: `0`."
                : $"- Task overdue: {string.Join(", ", overdue.Select(FormatTaskRef))}.",
            stalled.Count == 0
                ? "- Task đình trệ: `0`."
                : $"- Task đình trệ: {string.Join(", ", stalled.Select(x => $"#{x.TaskId}"))}.",
            highPoint.Count == 0
                ? "- Task điểm cao chưa bắt đầu: `0`."
                : $"- Điểm cao chưa bắt đầu: {string.Join(", ", highPoint.Select(FormatTaskRef))}.",
            unassigned.Count == 0
                ? "- Task chưa assign: `0`."
                : $"- Chưa assign: {string.Join(", ", unassigned.Select(FormatTaskRef))}.",
            openBugs.Count == 0
                ? "- Bug mở quan trọng: `0`."
                : $"- Bug mở quan trọng: {string.Join(", ", openBugs.Select(FormatTaskRef))}."
        };

        return JoinBulletLines(lines);
    }

    private static string BuildDailyChangesSection(
        IReadOnlyList<TaskItem> createdToday,
        IReadOnlyList<ProjectMemoryMessage> todayMemoryMessages)
    {
        var newTasks = createdToday.Where(x => x.Type == TaskItemType.Task).Take(5).ToList();
        var newBugs = createdToday.Where(x => x.Type == TaskItemType.Bug).Take(5).ToList();

        var completionMessages = todayMemoryMessages
            .Where(x => x.IsBot && x.NormalizedContent.Contains("hoan thanh nhiem vu", StringComparison.Ordinal))
            .ToList();
        var assignmentMessages = todayMemoryMessages
            .Where(x =>
                x.IsBot &&
                (x.NormalizedContent.Contains("giao nhiem vu", StringComparison.Ordinal) ||
                 x.NormalizedContent.Contains("nhan nhiem vu", StringComparison.Ordinal)))
            .ToList();
        var fixedBugMessages = todayMemoryMessages
            .Where(x =>
                x.IsBot &&
                (x.NormalizedContent.Contains("dong loi", StringComparison.Ordinal) ||
                 x.NormalizedContent.Contains("loi da duoc xu ly", StringComparison.Ordinal)))
            .ToList();

        var completedTaskIds = ExtractDistinctTaskIds(completionMessages.Select(x => x.Content));
        var fixedBugIds = ExtractDistinctTaskIds(fixedBugMessages.Select(x => x.Content));
        var assigneeIds = ExtractPrimaryAssigneeIds(assignmentMessages.Select(x => x.Content)).Take(5).ToList();

        var lines = new List<string>
        {
            $"- Task mới tạo: `{newTasks.Count}`" +
            (newTasks.Count > 0 ? $" | {string.Join(", ", newTasks.Select(x => $"#{x.Id}"))}" : string.Empty),
            $"- Bug mới mở: `{newBugs.Count}`" +
            (newBugs.Count > 0 ? $" | {string.Join(", ", newBugs.Select(x => $"#{x.Id}"))}" : string.Empty),
            $"- Task chuyển Done: `{completedTaskIds.Count}`" +
            (completedTaskIds.Count > 0 ? $" | {string.Join(", ", completedTaskIds.Select(x => $"#{x}"))}" : string.Empty),
            $"- Bug đã đóng: `{fixedBugIds.Count}`" +
            (fixedBugIds.Count > 0 ? $" | {string.Join(", ", fixedBugIds.Select(x => $"#{x}"))}" : string.Empty),
            $"- Người nhận task mới: `{assigneeIds.Count}`" +
            (assigneeIds.Count > 0 ? $" | {string.Join(", ", assigneeIds.Select(x => $"<@{x}>"))}" : string.Empty)
        };

        return JoinBulletLines(lines);
    }

    private static string BuildChatSignalsSection(AssistantDailyMemoryDigest? todayDigest)
    {
        if (todayDigest is null)
        {
            return JoinBulletLines([
                "- Chưa có đủ memory digest cho hôm nay.",
                "- Chủ đề chat và highlights sẽ rõ hơn sau khi bot archive thêm message."
            ]);
        }

        var lines = new List<string>
        {
            todayDigest.TopKeywords.Count == 0
                ? "- Chủ đề nổi bật: chưa rõ."
                : $"- Chủ đề nổi bật: {string.Join(", ", todayDigest.TopKeywords.Take(5))}.",
            todayDigest.ActiveChannels.Count == 0
                ? "- Kênh/thread hoạt động mạnh: chưa rõ."
                : $"- Kênh/thread hoạt động mạnh: {string.Join(", ", todayDigest.ActiveChannels.Take(3))}.",
            todayDigest.Highlights.Count == 0
                ? "- Highlight/rủi ro từ chat: chưa có."
                : $"- Highlight/rủi ro: {string.Join(" | ", todayDigest.Highlights.Take(2).Select(x => Truncate(x, 90)))}"
        };

        return JoinBulletLines(lines);
    }

    private static string BuildActionSection(
        ProjectAssistantContext context,
        IReadOnlyList<AssistantStandupEntry> todayStandups)
    {
        var actions = new List<string>();

        var missingToday = context.StandupDiscipline.MissingReporters
            .Where(x => x.MissingToday)
            .Take(3)
            .ToList();
        if (missingToday.Count > 0)
        {
            actions.Add($"Nhắc {string.Join(", ", missingToday.Select(x => $"<@{x.DiscordUserId}>"))} nộp standup trước khi chốt ngày.");
        }

        var blockerToday = todayStandups.FirstOrDefault(x => x.HasBlockers);
        if (blockerToday is not null)
        {
            actions.Add($"Follow blocker của <@{blockerToday.DiscordUserId}>: {Truncate(blockerToday.Blockers, 80)}");
        }

        var overdue = context.AttentionItems.FirstOrDefault(x => x.Kind == "overdue_task");
        if (overdue?.TaskId is int overdueTaskId)
        {
            actions.Add($"Chốt owner hoặc cập nhật tiến độ ngay cho task `#{overdueTaskId}`.");
        }

        var stalled = context.StalledTasks.FirstOrDefault();
        if (stalled is not null)
        {
            actions.Add($"Kiểm tra task đình trệ `#{stalled.TaskId}` vì {stalled.Reason.ToLowerInvariant()}.");
        }

        var unassigned = context.AttentionItems.FirstOrDefault(x => x.Kind == "unassigned_task");
        if (unassigned?.TaskId is int unassignedTaskId)
        {
            actions.Add($"Giao owner cho task `#{unassignedTaskId}` để tránh tiếp tục treo.");
        }

        var highPoint = context.AttentionItems.FirstOrDefault(x => x.Kind == "high_point_not_started");
        if (highPoint?.TaskId is int highPointTaskId && context.Sprint.ScheduleProgressPercent.GetValueOrDefault() >= 50)
        {
            actions.Add($"Bắt đầu hoặc tách nhỏ task điểm cao `#{highPointTaskId}` vì sprint đã đi qua nửa chặng.");
        }

        if (context.Sprint.Health.Label == "negative")
        {
            actions.Add("Rà lại commitment hoặc ưu tiên lại sprint vì delivery đang chậm hơn timeline.");
        }

        if (context.Sprint.OpenBugCount >= 5)
        {
            actions.Add("Dành thêm slot xử lý bug mở để tránh ảnh hưởng delivery cuối sprint.");
        }

        if (actions.Count == 0)
        {
            actions.Add("Không có rủi ro nổi bật cần escalate ngay; tiếp tục giữ nhịp sprint hiện tại.");
        }

        return JoinBulletLines(actions.Take(5).Select(x => $"- {x}").ToList());
    }

    private static Color ResolveReportColor(string healthLabel)
    {
        return healthLabel switch
        {
            "positive" => Color.Green,
            "negative" => Color.Orange,
            _ => Color.Blue
        };
    }

    private DateTimeOffset BuildUtcStart(DateTime localDate)
    {
        var localStart = new DateTime(localDate.Year, localDate.Month, localDate.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, _studioTime.TimeZone));
    }

    private (DateTimeOffset StartUtc, DateTimeOffset EndUtc) BuildUtcRangeForLocalDate(DateTime localDate)
    {
        var startUtc = BuildUtcStart(localDate);
        var endUtc = BuildUtcStart(localDate.AddDays(1));
        return (startUtc, endUtc);
    }

    private static string JoinBulletLines(IReadOnlyList<string> lines, int maxLength = 1000)
    {
        var combined = string.Join('\n', lines.Where(x => !string.IsNullOrWhiteSpace(x)));
        return combined.Length <= maxLength ? combined : $"{combined[..maxLength]}...";
    }

    private static string FormatTaskRef(AssistantAttentionItem item)
    {
        return item.TaskId.HasValue ? $"#{item.TaskId.Value}" : Truncate(item.Title, 24);
    }

    private static List<int> ExtractDistinctTaskIds(IEnumerable<string> texts)
    {
        return texts
            .SelectMany(text => TaskIdRegex.Matches(text).Select(match => int.Parse(match.Groups["id"].Value)))
            .Distinct()
            .Take(6)
            .ToList();
    }

    private static List<ulong> ExtractPrimaryAssigneeIds(IEnumerable<string> texts)
    {
        return texts
            .Select(text =>
            {
                var ids = UserMentionRegex.Matches(text)
                    .Select(match => ulong.Parse(match.Groups["id"].Value))
                    .ToList();

                return ids.Count == 0 ? (ulong?)null : ids[^1];
            })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }
}
