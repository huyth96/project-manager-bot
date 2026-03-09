using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using ProjectManagerBot.Options;

namespace ProjectManagerBot.Services;

public sealed class BotAssistantService(
    ProjectInsightService projectInsightService,
    IHttpClientFactory httpClientFactory,
    IOptions<AssistantOptions> options,
    ILogger<BotAssistantService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ProjectInsightService _projectInsightService = projectInsightService;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly AssistantOptions _options = options.Value;
    private readonly ILogger<BotAssistantService> _logger = logger;

    public async Task<string> GenerateReplyAsync(
        SocketUserMessage message,
        string question,
        CancellationToken cancellationToken = default)
    {
        var insight = await _projectInsightService.BuildContextAsync(message, question, cancellationToken);
        if (insight is null)
        {
            return "Kênh này chưa nằm trong project nào nên tôi chưa có ngữ cảnh sprint/task để trả lời. Hãy hỏi trong một kênh thuộc khu project hoặc chạy `/project setup` trước.";
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            return BuildHelpResponse(insight);
        }

        var intent = ClassifyIntent(question);
        if (intent == AssistantIntent.Greeting)
        {
            return BuildHelpResponse(insight);
        }

        var deterministicReply = TryBuildDeterministicResponse(question, insight, intent);
        if (!string.IsNullOrWhiteSpace(deterministicReply))
        {
            return ClampForDiscord(deterministicReply);
        }

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return ClampForDiscord(BuildFallbackResponse(question, insight, aiAvailable: false, intent));
        }

        try
        {
            var aiReply = await GenerateAiReplyAsync(question, insight, intent, cancellationToken);
            if (!string.IsNullOrWhiteSpace(aiReply))
            {
                return ClampForDiscord(aiReply.Trim());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant AI failed for channel {ChannelId}", message.Channel.Id);
        }

        return ClampForDiscord(BuildFallbackResponse(question, insight, aiAvailable: true, intent));
    }

    private async Task<string?> GenerateAiReplyAsync(
        string question,
        ProjectAssistantContext insight,
        AssistantIntent intent,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildChatCompletionsEndpoint(_options.BaseUrl);
        var contextEnvelope = BuildAiContextEnvelope(insight, intent);
        var payload = new
        {
            model = _options.Model,
            temperature = Math.Clamp(_options.Temperature, 0, 1),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildSystemPromptV2()
                },
                new
                {
                    role = "system",
                    content = BuildIntentGuidanceV2(intent)
                },
                new
                {
                    role = "system",
                    content = "Structured project context JSON:\n" + JsonSerializer.Serialize(contextEnvelope, JsonOptions)
                },
                new
                {
                    role = "user",
                    content = question.Trim()
                }
            }
        };

        var client = _httpClientFactory.CreateClient("Assistant");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Assistant API returned {StatusCode}. Body: {Body}",
                (int)response.StatusCode,
                Truncate(responseBody, 600));
            return null;
        }

        using var document = JsonDocument.Parse(responseBody);
        return TryReadAssistantContent(document.RootElement);
    }

    private string BuildHelpResponse(ProjectAssistantContext insight)
    {
        if (!insight.Sprint.HasActiveSprint)
        {
            return
                $"Hiện chưa có sprint active cho dự án `{insight.Scope.ProjectName}`. " +
                $"Backlog đang có `{insight.Sprint.ProjectBacklogCount}` task và `{insight.Sprint.OpenBugCount}` bug mở.\n" +
                "Bạn có thể hỏi như: `@bot backlog hiện ra sao`, `@bot ai hay trễ hoặc chưa nộp báo cáo`, `@bot task nào có dấu hiệu đình trệ`, `@bot tuần qua team bàn gì`.";
        }

        return
            $"Sprint `{insight.Sprint.Name}` đang có `{insight.Sprint.DoneTasks}/{insight.Sprint.TotalTasks}` task done " +
            $"và `{insight.Sprint.DonePoints}/{Math.Max(insight.Sprint.TotalPoints, 0)}` points hoàn thành.\n" +
            "Bạn có thể hỏi như: `@bot tiến độ sprint thế nào`, `@bot ai hay trễ hoặc chưa nộp báo cáo`, `@bot task nào đang bị đình trệ`, `@bot tuần qua team bàn về vấn đề gì`.";
    }

    private string BuildFallbackResponse(
        string question,
        ProjectAssistantContext insight,
        bool aiAvailable,
        AssistantIntent intent)
    {
        var builder = new StringBuilder();

        if (!aiAvailable)
        {
            builder.AppendLine("Assistant AI chua duoc cau hinh, nen toi dang tra loi theo du lieu project hien co.");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Toi tam tra loi theo du lieu project hien co vi service AI chua phan hoi duoc.");
            builder.AppendLine();
        }

        AppendSprintSummary(builder, insight);

        switch (intent)
        {
            case AssistantIntent.StandupDiscipline:
                AppendLateReportersV2(builder, insight);
                AppendMissingReportersV2(builder, insight);
                AppendMemberSignalsV2(builder, insight, maxItems: 3);
                break;

            case AssistantIntent.MemberInsights:
                AppendMemberProfilesV2(builder, insight);
                AppendMemberSignalsV2(builder, insight);
                AppendMemberWorkloadsV2(builder, insight, maxItems: 3);
                break;

            case AssistantIntent.DecisionHistory:
                AppendDecisionSummary(builder, insight);
                AppendTopicSummary(builder, insight, maxItems: 3);
                AppendRelevantMessages(builder, insight, maxItems: 2);
                break;

            case AssistantIntent.TopicHistory:
                AppendTaskFlowSummaryV2(builder, insight);
                AppendTopicSummary(builder, insight);
                AppendDecisionSummary(builder, insight, maxItems: 3);
                AppendRiskSummary(builder, insight, maxItems: 3);
                AppendMemoryOverview(builder, insight);
                AppendRelevantMessages(builder, insight);
                break;

            case AssistantIntent.RiskReview:
                AppendRiskSummary(builder, insight);
                AppendTrendSummary(builder, insight);
                AppendAttentionItemsV2(builder, insight, maxItems: 3);
                break;

            case AssistantIntent.TaskExecution:
                AppendTaskFlowSummaryV2(builder, insight);
                AppendStalledTasksV2(builder, insight);
                AppendAttentionItemsV2(builder, insight);
                break;

            case AssistantIntent.ProgressReview:
                AppendHealthSummary(builder, insight);
                AppendTrendSummary(builder, insight);
                AppendAttentionItemsV2(builder, insight, maxItems: 3);
                break;

            case AssistantIntent.Estimation:
                builder.AppendLine();
                builder.Append("De goi y diem tot hon, hay gui ro ten task, mo ta, do phuc tap ky thuat, dependency va pham vi UI/API. ");
                builder.Append("Khi co AI on dinh, bot se dien giai estimate tu nhien hon.");
                break;

            default:
                AppendHealthSummary(builder, insight);
                AppendTaskFlowSummaryV2(builder, insight);
                AppendLateReportersV2(builder, insight, maxItems: 2);
                AppendMissingReportersV2(builder, insight, maxItems: 2);
                AppendStalledTasksV2(builder, insight, maxItems: 2);
                AppendMemberWorkloadsV2(builder, insight, maxItems: 2);
                AppendTopicSummary(builder, insight, maxItems: 2);
                AppendRiskSummary(builder, insight, maxItems: 2);
                AppendAttentionItemsV2(builder, insight, maxItems: 3);
                break;
        }

        return builder.ToString().Trim();
    }

    private string BuildFallbackResponse(
        string question,
        ProjectAssistantContext insight,
        bool aiAvailable)
    {
        var lowerQuestion = question.Trim().ToLowerInvariant();
        var builder = new StringBuilder();

        if (!aiAvailable)
        {
            builder.AppendLine("Assistant AI chưa được cấu hình, nên tôi đang trả lời theo dữ liệu project hiện có.");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Tôi tạm trả lời theo dữ liệu project hiện có vì service AI chưa phản hồi được.");
            builder.AppendLine();
        }

        AppendSprintSummary(builder, insight);

        if (ContainsAny(lowerQuestion, "trễ báo cáo", "tre bao cao", "báo cáo", "bao cao", "standup"))
        {
            AppendLateReportersV2(builder, insight);
            AppendMissingReportersV2(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "không nộp", "khong nop", "chưa nộp", "chua nop", "missing report", "miss standup"))
        {
            AppendMissingReportersV2(builder, insight);
            AppendLateReportersV2(builder, insight, maxItems: 2);
        }
        else if (ContainsAny(lowerQuestion, "đình trệ", "dinh tre", "stalled", "kẹt", "ket", "tắc", "tac"))
        {
            AppendStalledTasksV2(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "member", "thanh vien", "qua tai", "phu hop", "ai dang qua tai", "ai hop", "ai hop task"))
        {
            AppendMemberProfilesV2(builder, insight);
            AppendMemberSignalsV2(builder, insight);
            AppendMemberWorkloadsV2(builder, insight, maxItems: 3);
        }
        else if (ContainsAny(lowerQuestion, "workload", "qua tai", "dang giu", "ai dang lam nhieu", "ai lam nhieu", "ganh"))
        {
            AppendMemberWorkloadsV2(builder, insight);
            AppendTaskFlowSummaryV2(builder, insight);
            AppendMemberProfilesV2(builder, insight, maxItems: 3);
        }
        else if (ContainsAny(lowerQuestion, "quyet dinh", "decision", "chot gi", "chot", "thong nhat"))
        {
            AppendDecisionSummary(builder, insight);
            AppendTopicSummary(builder, insight, maxItems: 3);
        }
        else if (ContainsAny(lowerQuestion, "rui ro", "risk", "lech", "tre", "cham", "van de nong", "hot issue"))
        {
            AppendRiskSummary(builder, insight);
            AppendTrendSummary(builder, insight);
            AppendAttentionItemsV2(builder, insight, maxItems: 3);
        }
        else if (ContainsAny(lowerQuestion, "chu de", "topic", "ban nhieu", "nhac nhieu", "hot topic"))
        {
            AppendTopicSummary(builder, insight);
            AppendDecisionSummary(builder, insight, maxItems: 2);
            AppendRelevantMessages(builder, insight, maxItems: 2);
        }
        else if (ContainsAny(lowerQuestion, "tuần qua", "tuan qua", "gần đây", "gan day", "đã bàn", "da ban", "thảo luận", "thao luan", "lịch sử", "lich su", "nhắc lại", "nho lai", "vấn đề gì", "van de gi"))
        {
            AppendTaskFlowSummaryV2(builder, insight);
            AppendTopicSummary(builder, insight);
            AppendDecisionSummary(builder, insight, maxItems: 3);
            AppendRiskSummary(builder, insight, maxItems: 3);
            AppendMemoryOverview(builder, insight);
            AppendRelevantMessages(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "task", "xử lý", "xu ly", "ưu tiên", "uu tien", "cần làm", "can lam", "bug"))
        {
            AppendAttentionItemsV2(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "tích cực", "tich cuc", "tiêu cực", "tieu cuc", "tiến độ", "tien do", "sprint", "health"))
        {
            AppendHealthSummary(builder, insight);
            AppendTrendSummary(builder, insight);
            AppendAttentionItemsV2(builder, insight, maxItems: 3);
        }
        else if (ContainsAny(lowerQuestion, "point", "điểm", "ước lượng", "uoc luong", "estimate"))
        {
            builder.AppendLine();
            builder.Append("Để gợi ý điểm tốt hơn, hãy gửi rõ tên task, mô tả, độ phức tạp kỹ thuật, dependency và phạm vi UI/API. ");
            builder.Append("Khi có AI ổn định, bot sẽ diễn giải estimate tự nhiên hơn.");
        }
        else
        {
            AppendHealthSummary(builder, insight);
            AppendTaskFlowSummaryV2(builder, insight);
            AppendLateReportersV2(builder, insight, maxItems: 2);
            AppendMissingReportersV2(builder, insight, maxItems: 2);
            AppendStalledTasksV2(builder, insight, maxItems: 2);
            AppendMemberWorkloadsV2(builder, insight, maxItems: 2);
            AppendTopicSummary(builder, insight, maxItems: 2);
            AppendRiskSummary(builder, insight, maxItems: 2);
            AppendAttentionItemsV2(builder, insight, maxItems: 3);
        }

        return builder.ToString().Trim();
    }

    private static void AppendSprintSummary(StringBuilder builder, ProjectAssistantContext insight)
    {
        builder.AppendLine($"Dự án: `{insight.Scope.ProjectName}`");

        if (!insight.Sprint.HasActiveSprint)
        {
            builder.AppendLine($"Hiện chưa có sprint active. Backlog: `{insight.Sprint.ProjectBacklogCount}` task, bug mở: `{insight.Sprint.OpenBugCount}`.");
            return;
        }

        builder.AppendLine(
            $"Sprint: `{insight.Sprint.Name}` | Done `{insight.Sprint.DoneTasks}/{insight.Sprint.TotalTasks}` task | " +
            $"Points `{insight.Sprint.DonePoints}/{insight.Sprint.TotalPoints}` | Bug mở `{insight.Sprint.OpenBugCount}`.");

        if (insight.Sprint.ScheduleProgressPercent.HasValue)
        {
            builder.AppendLine(
                $"Timeline `{insight.Sprint.ScheduleProgressPercent.Value}%`, delivery `{insight.Sprint.DeliveryProgressPercent}%`, " +
                $"health `{insight.Sprint.Health.Label}`.");
        }
    }

    private static void AppendHealthSummary(StringBuilder builder, ProjectAssistantContext insight)
    {
        builder.AppendLine();
        builder.AppendLine($"Đánh giá: {insight.Sprint.Health.Summary}");

        if (insight.Sprint.Health.DeltaPercent.HasValue)
        {
            builder.AppendLine($"Chênh lệch delivery so với timeline: `{insight.Sprint.Health.DeltaPercent.Value}%`.");
        }

        var blockerCount = insight.Standups.Count(x => x.HasBlockers);
        if (blockerCount > 0)
        {
            builder.AppendLine($"Standup gần đây có `{blockerCount}` báo cáo blocker.");
        }
    }

    private static void AppendTaskFlowSummary(StringBuilder builder, ProjectAssistantContext insight)
    {
        builder.AppendLine();
        builder.AppendLine(
            $"Trong {insight.TaskFlow.LookbackDays} ngÃ y gáº§n Ä‘Ã¢y cÃ³ `{insight.TaskFlow.TotalEvents}` task event: " +
            $"táº¡o `{insight.TaskFlow.CreatedTasks}` task, hoÃ n thÃ nh `{insight.TaskFlow.CompletedTasks}` task, " +
            $"má»Ÿ `{insight.TaskFlow.CreatedBugs}` bug, Ä‘Ã³ng `{insight.TaskFlow.FixedBugs}` bug, tráº£ vá» backlog `{insight.TaskFlow.ReturnedToBacklog}` task.");

        var topActors = insight.TaskFlow.TopActors.Take(3).ToList();
        if (topActors.Count == 0)
        {
            builder.AppendLine("ChÆ°a cÃ³ dá»¯ liá»‡u thao tÃ¡c task Ä‘á»§ Ä‘á»ƒ suy ra ai Ä‘ang hoáº¡t Ä‘á»™ng nhiá»u.");
            return;
        }

        builder.AppendLine("NgÆ°á»i cÃ³ nhiá»u thay Ä‘á»•i task/bug nháº¥t gáº§n Ä‘Ã¢y:");
        foreach (var actor in topActors)
        {
            builder.AppendLine(
                $"- <@{actor.DiscordUserId}>: `{actor.EventCount}` event | done `{actor.CompletedTasks}` task | fix `{actor.FixedBugs}` bug | nháº­n/giao `{actor.ClaimedOrAssignedTasks}` láº§n");
        }
    }

    private static void AppendLateReporters(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 3)
    {
        var lateReporters = insight.StandupDiscipline.LateReporters
            .Where(x => x.LateReports > 0)
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (lateReporters.Count == 0)
        {
            builder.AppendLine($"Trong {insight.StandupDiscipline.LookbackDays} ngày gần đây, chưa thấy ai có báo cáo nộp sau `{insight.StandupDiscipline.DueTimeLocal:hh\\:mm}`.");
            return;
        }

        builder.AppendLine($"Người hay trễ báo cáo trong {insight.StandupDiscipline.LookbackDays} ngày gần đây:");
        foreach (var reporter in lateReporters)
        {
            var avgLateText = reporter.AverageLateMinutes.HasValue ? $" | trễ TB {reporter.AverageLateMinutes.Value} phút" : string.Empty;
            builder.AppendLine($"- <@{reporter.DiscordUserId}>: trễ `{reporter.LateReports}/{reporter.TotalReports}` lần ({reporter.LateRatePercent}%){avgLateText}");
        }
    }

    private static void AppendMissingReporters(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 3)
    {
        var missingReporters = insight.StandupDiscipline.MissingReporters
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (insight.StandupDiscipline.ExpectedReporterCount == 0)
        {
            builder.AppendLine("Chưa đủ dữ liệu để suy ra ai là người bắt buộc phải nộp standup.");
            return;
        }

        if (missingReporters.Count == 0)
        {
            builder.AppendLine($"Chưa thấy ai bị thiếu báo cáo trong nhóm `{insight.StandupDiscipline.ExpectedReporterCount}` người đang được theo dõi.");
            return;
        }

        builder.AppendLine($"Người đang thiếu báo cáo trong nhóm `{insight.StandupDiscipline.ExpectedReporterCount}` người được theo dõi:");
        foreach (var reporter in missingReporters)
        {
            var lastMissing = reporter.LastMissingDate.HasValue ? $" | lần gần nhất {reporter.LastMissingDate:yyyy-MM-dd}" : string.Empty;
            var missingToday = reporter.MissingToday ? " | thiếu hôm nay" : string.Empty;
            builder.AppendLine(
                $"- <@{reporter.DiscordUserId}>: thiếu `{reporter.MissingDays}` ngày, đã nộp `{reporter.SubmittedDays}` ngày | {reporter.BasisSummary}{lastMissing}{missingToday}");
        }

        builder.AppendLine("Lưu ý: danh sách này là heuristic dựa trên task đang mở, lịch sử standup và mức hoạt động gần đây trong project.");
    }

    private static void AppendStalledTasks(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 5)
    {
        var stalledTasks = insight.StalledTasks.Take(Math.Max(1, maxItems)).ToList();

        builder.AppendLine();
        if (stalledTasks.Count == 0)
        {
            builder.AppendLine("Hiện chưa thấy task nào có dấu hiệu đình trệ theo rule hiện tại.");
            return;
        }

        builder.AppendLine("Task có dấu hiệu đình trệ:");
        foreach (var task in stalledTasks)
        {
            var owner = task.AssigneeId.HasValue ? $" | owner <@{task.AssigneeId.Value}>" : string.Empty;
            builder.AppendLine(
                $"- #{task.TaskId} {task.Title}: {task.Reason} | trạng thái `{task.Status}` | tuổi `{task.AgeDays}` ngày | {task.Points}đ{owner}");
        }
    }

    private static void AppendMemberWorkloads(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var workloads = insight.MemberWorkloads.Take(Math.Max(1, maxItems)).ToList();

        builder.AppendLine();
        if (workloads.Count == 0)
        {
            builder.AppendLine("Hiá»‡n chÆ°a tháº¥y ai Ä‘ang giá»¯ task/bug má»Ÿ Ä‘á»ƒ Ä‘Ã¡nh giÃ¡ workload.");
            return;
        }

        builder.AppendLine("Workload hiá»‡n táº¡i theo ngÆ°á»i Ä‘ang giá»¯ viá»‡c:");
        foreach (var workload in workloads)
        {
            builder.AppendLine(
                $"- <@{workload.DiscordUserId}>: `{workload.OpenTaskCount}` task má»Ÿ | `in-progress {workload.InProgressTaskCount}` | `bug {workload.OpenBugCount}` | `{workload.OpenPoints}` Ä‘iá»ƒm | hoáº¡t Ä‘á»™ng gáº§n Ä‘Ã¢y `{workload.RecentActivityCount}`");
        }
    }

    private static void AppendMemoryOverview(
        StringBuilder builder,
        ProjectAssistantContext insight,
        int maxDays = 4,
        bool includeDailyBullets = true)
    {
        builder.AppendLine();

        if (insight.Memory.ArchivedMessageCount == 0)
        {
            builder.AppendLine("Bộ nhớ dài hạn của project chưa có message archive nào.");
            return;
        }

        var coverageStart = insight.Memory.OldestLocalDate?.ToString("yyyy-MM-dd") ?? "không rõ";
        var coverageEnd = insight.Memory.LatestLocalDate?.ToString("yyyy-MM-dd") ?? "không rõ";
        builder.AppendLine($"Bộ nhớ hiện có `{insight.Memory.ArchivedMessageCount}` tin nhắn archived từ `{coverageStart}` đến `{coverageEnd}`.");

        var digests = insight.Memory.DailyDigests
            .Take(Math.Max(1, maxDays))
            .ToList();

        if (digests.Count == 0)
        {
            builder.AppendLine("Chưa có daily digest nào để tóm tắt lịch sử thảo luận.");
            return;
        }

        var topTopics = digests
            .SelectMany(x => x.TopKeywords)
            .GroupBy(x => x)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(x => x.Key)
            .ToList();

        var topChannels = digests
            .SelectMany(x => x.ActiveChannels)
            .GroupBy(x => x)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(x => x.Key)
            .ToList();

        if (topTopics.Count > 0)
        {
            builder.AppendLine($"Trong {digests.Count} ngày gần đây, team bàn nhiều về: {string.Join(", ", topTopics)}.");
        }

        if (topChannels.Count > 0)
        {
            builder.AppendLine($"Hoạt động nổi bật ở: {string.Join(", ", topChannels)}.");
        }

        if (!includeDailyBullets)
        {
            return;
        }

        builder.AppendLine("Tóm tắt theo ngày:");
        foreach (var digest in digests.Take(3))
        {
            builder.AppendLine($"- {digest.Date:yyyy-MM-dd}: {digest.Summary}");
        }
    }

    private static void AppendRelevantMessages(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var relevantMessages = insight.Memory.RelevantMessages
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (relevantMessages.Count == 0)
        {
            builder.AppendLine("Chưa tìm thấy dấu vết hội thoại lịch sử nào thật sự liên quan.");
            return;
        }

        builder.AppendLine("Dấu vết liên quan trong memory:");
        foreach (var message in relevantMessages)
        {
            var location = string.IsNullOrWhiteSpace(message.ThreadName)
                ? $"#{message.ChannelName}"
                : $"#{message.ChannelName}/{message.ThreadName}";

            builder.AppendLine(
                $"- {message.TimestampLocal:MM-dd HH:mm} | {location} | {message.AuthorName}: {Truncate(message.Content, 120)}");
        }
    }

    private static void AppendAttentionItems(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 5)
    {
        var items = insight.AttentionItems.Take(Math.Max(1, maxItems)).ToList();
        if (items.Count == 0)
        {
            builder.AppendLine();
            builder.Append("Hiện chưa thấy mục nào nổi bật cần escalate ngay trong snapshot.");
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Mục cần chú ý:");
        foreach (var item in items)
        {
            var owner = item.AssigneeId.HasValue ? $" | owner <@{item.AssigneeId.Value}>" : string.Empty;
            var points = item.Points.HasValue ? $" | {item.Points.Value}đ" : string.Empty;
            builder.AppendLine($"- {item.Title}: {item.Summary}{points}{owner}");
        }
    }

    private static void AppendMemberProfiles(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var members = insight.Knowledge.Members
            .OrderBy(x => x.ReliabilityScore)
            .ThenByDescending(x => x.OpenPoints)
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (members.Count == 0)
        {
            builder.AppendLine("Chua co member profile du de danh gia nang luc va do tin cay.");
            return;
        }

        builder.AppendLine("Member profile noi bat:");
        foreach (var member in members)
        {
            var skills = member.SkillKeywords.Count == 0 ? "chua ro" : string.Join(", ", member.SkillKeywords);
            builder.AppendLine(
                $"- <@{member.DiscordUserId}> `{member.RoleSummary}` | reliability `{member.ReliabilityScore}` | open `{member.OpenTaskCount}` task / `{member.OpenBugCount}` bug / `{member.OpenPoints}` points | skill `{skills}` | confidence `{member.ConfidencePercent}`");
        }
    }

    private static void AppendMemberSignals(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var signals = insight.Knowledge.MemberSignals
            .OrderByDescending(x => x.Date)
            .ThenBy(x => x.ReliabilityScore)
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (signals.Count == 0)
        {
            builder.AppendLine("Chua co member daily signal gan day.");
            return;
        }

        builder.AppendLine("Tin hieu member gan day:");
        foreach (var signal in signals)
        {
            var evidence = signal.Evidence.Count == 0 ? "khong ro" : string.Join(", ", signal.Evidence);
            var lateText = signal.WasLate ? $" | late `{signal.LateMinutes ?? 0}`m" : string.Empty;
            builder.AppendLine(
                $"- {signal.Date:yyyy-MM-dd} <@{signal.DiscordUserId}> | reliability `{signal.ReliabilityScore}` | standup `{(signal.SubmittedStandup ? "co" : "thieu")}`{lateText} | blocker `{(signal.HasBlocker ? "co" : "khong")}` | completed `{signal.CompletedTasks}` | evidence {evidence}");
        }
    }

    private static void AppendTopicSummary(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var topics = insight.Knowledge.Topics.Take(Math.Max(1, maxItems)).ToList();

        builder.AppendLine();
        if (topics.Count == 0)
        {
            builder.AppendLine("Chua co topic summary ro rang trong memory.");
            return;
        }

        builder.AppendLine("Chu de noi bat:");
        foreach (var topic in topics)
        {
            var channels = topic.TopChannels.Count == 0 ? "khong ro" : string.Join(", ", topic.TopChannels);
            builder.AppendLine(
                $"- `{topic.TopicKey}`: `{topic.MentionCount}` lan nhac, `{topic.DistinctAuthorCount}` nguoi tham gia | kenh {channels}");
        }
    }

    private static void AppendDecisionSummary(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var decisions = insight.Knowledge.Decisions.Take(Math.Max(1, maxItems)).ToList();

        builder.AppendLine();
        if (decisions.Count == 0)
        {
            builder.AppendLine("Chua thay decision log ro rang trong cua so gan day.");
            return;
        }

        builder.AppendLine("Decision log gan day:");
        foreach (var decision in decisions)
        {
            var source = string.IsNullOrWhiteSpace(decision.SourceChannelName) ? string.Empty : $" | #{decision.SourceChannelName}";
            builder.AppendLine(
                $"- {decision.Date:yyyy-MM-dd} `{decision.TopicKey}`: {decision.Summary} | confidence `{decision.ConfidencePercent}`{source} | evidence {decision.Evidence}");
        }
    }

    private static void AppendRiskSummary(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var risks = insight.Knowledge.Risks.Take(Math.Max(1, maxItems)).ToList();

        builder.AppendLine();
        if (risks.Count == 0)
        {
            builder.AppendLine("Chua co risk log noi bat trong cua so hien tai.");
            return;
        }

        builder.AppendLine("Risk log gan day:");
        foreach (var risk in risks)
        {
            builder.AppendLine(
                $"- {risk.Date:yyyy-MM-dd} [{risk.Severity}] `{risk.RiskKey}`: {risk.Summary} | confidence `{risk.ConfidencePercent}` | evidence {risk.Evidence}");
        }
    }

    private static void AppendTrendSummary(StringBuilder builder, ProjectAssistantContext insight)
    {
        var sprintTrend = insight.Knowledge.SprintTrend.OrderBy(x => x.Date).TakeLast(3).ToList();
        var riskTrend = insight.Knowledge.RiskTrend.OrderBy(x => x.Date).TakeLast(3).ToList();

        builder.AppendLine();
        if (sprintTrend.Count == 0 && riskTrend.Count == 0)
        {
            builder.AppendLine("Chua co lich su trend du de so sanh theo ngay.");
            return;
        }

        if (sprintTrend.Count > 0)
        {
            builder.AppendLine("Sprint trend:");
            foreach (var point in sprintTrend)
            {
                builder.AppendLine(
                    $"- {point.Date:MM-dd}: delivery `{point.DeliveryProgressPercent}%`, schedule `{point.ScheduleProgressPercent?.ToString() ?? "?"}%`, stalled `{point.StalledTaskCount}`, bugs `{point.OpenBugCount}`, health `{point.HealthLabel}`");
            }
        }

        if (riskTrend.Count > 0)
        {
            builder.AppendLine("Risk trend:");
            foreach (var point in riskTrend)
            {
                builder.AppendLine(
                    $"- {point.Date:MM-dd}: risk `{point.RiskScore}`, overdue `{point.OverdueTaskCount}`, stalled `{point.StalledTaskCount}`, missing standup `{point.MissingStandupCount}`, blockers `{point.BlockerCount}`");
            }
        }
    }

    private static void AppendTaskFlowSummaryV2(StringBuilder builder, ProjectAssistantContext insight)
    {
        builder.AppendLine();
        builder.AppendLine(
            $"Trong `{insight.TaskFlow.LookbackDays}d` gan day co `{insight.TaskFlow.TotalEvents}` task event: " +
            $"tao `{insight.TaskFlow.CreatedTasks}` task, hoan thanh `{insight.TaskFlow.CompletedTasks}` task, " +
            $"mo `{insight.TaskFlow.CreatedBugs}` bug, dong `{insight.TaskFlow.FixedBugs}` bug, tra backlog `{insight.TaskFlow.ReturnedToBacklog}` task.");

        var topActors = insight.TaskFlow.TopActors.Take(3).ToList();
        if (topActors.Count == 0)
        {
            builder.AppendLine("Chua co du task event de suy ra ai dang thao tac nhieu nhat.");
            return;
        }

        builder.AppendLine("Nguoi co nhieu thay doi task/bug nhat:");
        foreach (var actor in topActors)
        {
            builder.AppendLine(
                $"- {FormatMemberLabelV2(insight, actor.DiscordUserId)}: `{actor.EventCount}` event | done `{actor.CompletedTasks}` task | fix `{actor.FixedBugs}` bug | assign/claim `{actor.ClaimedOrAssignedTasks}`");
        }
    }

    private static void AppendLateReportersV2(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 3)
    {
        var lateReporters = insight.StandupDiscipline.LateReporters
            .Where(x => x.LateReports > 0)
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (lateReporters.Count == 0)
        {
            builder.AppendLine($"Trong `{insight.StandupDiscipline.LookbackDays}d` gan day, chua thay ai nop sau `{insight.StandupDiscipline.DueTimeLocal:hh\\:mm}`.");
            return;
        }

        builder.AppendLine($"Nguoi hay tre bao cao trong `{insight.StandupDiscipline.LookbackDays}d` gan day:");
        foreach (var reporter in lateReporters)
        {
            var avgLateText = reporter.AverageLateMinutes.HasValue ? $" | tre TB `{reporter.AverageLateMinutes.Value}`m" : string.Empty;
            var lastReportText = reporter.LastReportedAtLocal.HasValue ? $" | lan gan nhat `{reporter.LastReportedAtLocal:MM-dd HH:mm}`" : string.Empty;
            builder.AppendLine(
                $"- {FormatMemberLabelV2(insight, reporter.DiscordUserId)}: tre `{reporter.LateReports}/{reporter.TotalReports}` lan ({reporter.LateRatePercent}%){avgLateText}{lastReportText}{FormatMemberConfidenceV2(insight, reporter.DiscordUserId)}");
        }
    }

    private static void AppendMissingReportersV2(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 3)
    {
        var missingReporters = insight.StandupDiscipline.MissingReporters
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (insight.StandupDiscipline.ExpectedReporterCount == 0)
        {
            builder.AppendLine("Chua du du lieu de suy ra ai la nguoi bat buoc phai nop standup.");
            return;
        }

        if (missingReporters.Count == 0)
        {
            builder.AppendLine($"Chua thay ai thieu bao cao trong nhom `{insight.StandupDiscipline.ExpectedReporterCount}` nguoi dang duoc theo doi.");
            return;
        }

        builder.AppendLine($"Nguoi dang thieu bao cao trong nhom `{insight.StandupDiscipline.ExpectedReporterCount}` nguoi duoc theo doi:");
        foreach (var reporter in missingReporters)
        {
            var lastMissing = reporter.LastMissingDate.HasValue ? $" | lan gan nhat `{reporter.LastMissingDate:yyyy-MM-dd}`" : string.Empty;
            var missingToday = reporter.MissingToday ? " | thieu hom nay" : string.Empty;
            builder.AppendLine(
                $"- {FormatMemberLabelV2(insight, reporter.DiscordUserId)}: thieu `{reporter.MissingDays}` ngay, da nop `{reporter.SubmittedDays}` ngay | basis {reporter.BasisSummary} | range `{insight.StandupDiscipline.LookbackDays}d`{lastMissing}{missingToday}{FormatMemberConfidenceV2(insight, reporter.DiscordUserId)}");
        }

        builder.AppendLine("Luu y: day la heuristic dua tren task dang mo, lich su standup va muc hoat dong gan day trong project.");
    }

    private static void AppendStalledTasksV2(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 5)
    {
        var stalledTasks = insight.StalledTasks.Take(Math.Max(1, maxItems)).ToList();

        builder.AppendLine();
        if (stalledTasks.Count == 0)
        {
            builder.AppendLine("Hien chua thay task nao co dau hieu dinh tre theo rule hien tai.");
            return;
        }

        builder.AppendLine("Task co dau hieu dinh tre:");
        foreach (var task in stalledTasks)
        {
            var owner = task.AssigneeId.HasValue ? $" | owner {FormatMemberLabelV2(insight, task.AssigneeId.Value)}" : string.Empty;
            builder.AppendLine(
                $"- #{task.TaskId} {task.Title}: {task.Reason} | status `{task.Status}` | age `{task.AgeDays}d` | no-change `{task.DaysWithoutChange}d` | {task.Points}d{owner} | evidence {task.Evidence}");
        }
    }

    private static void AppendMemberWorkloadsV2(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var workloads = insight.MemberWorkloads.Take(Math.Max(1, maxItems)).ToList();

        builder.AppendLine();
        if (workloads.Count == 0)
        {
            builder.AppendLine("Hien chua thay ai dang giu task/bug mo de danh gia workload.");
            return;
        }

        builder.AppendLine("Workload hien tai theo nguoi dang giu viec:");
        foreach (var workload in workloads)
        {
            builder.AppendLine(
                $"- {FormatMemberLabelV2(insight, workload.DiscordUserId)}: open `{workload.OpenTaskCount}` task | in-progress `{workload.InProgressTaskCount}` | bug `{workload.OpenBugCount}` | points `{workload.OpenPoints}` | recent activity `{workload.RecentActivityCount}`{FormatMemberConfidenceV2(insight, workload.DiscordUserId)}");
        }
    }

    private static void AppendAttentionItemsV2(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 5)
    {
        var items = insight.AttentionItems.Take(Math.Max(1, maxItems)).ToList();
        if (items.Count == 0)
        {
            builder.AppendLine();
            builder.Append("Hien chua thay muc nao noi bat can escalate ngay trong snapshot.");
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Muc can chu y:");
        foreach (var item in items)
        {
            var owner = item.AssigneeId.HasValue ? $" | owner {FormatMemberLabelV2(insight, item.AssigneeId.Value)}" : string.Empty;
            var points = item.Points.HasValue ? $" | {item.Points.Value}d" : string.Empty;
            builder.AppendLine($"- {item.Title}: {item.Summary}{points}{owner}");
        }
    }

    private static void AppendMemberProfilesV2(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var members = insight.Knowledge.Members
            .OrderBy(x => x.ReliabilityScore)
            .ThenByDescending(x => x.OpenPoints)
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (members.Count == 0)
        {
            builder.AppendLine("Chua co member profile du de danh gia nang luc va do tin cay.");
            return;
        }

        builder.AppendLine("Member profile noi bat:");
        foreach (var member in members)
        {
            var skills = member.SkillKeywords.Count == 0 ? "chua ro" : string.Join(", ", member.SkillKeywords);
            var topics = member.DominantTopics.Count == 0 ? "chua ro" : string.Join(", ", member.DominantTopics);
            builder.AppendLine(
                $"- {FormatMemberLabelV2(insight, member.DiscordUserId)} `{member.RoleSummary}` | reliability `{member.ReliabilityScore}` | open `{member.OpenTaskCount}` task / `{member.OpenBugCount}` bug / `{member.OpenPoints}` points | skill `{skills}` | topic `{topics}` | confidence `{member.ConfidencePercent}`");
            builder.AppendLine($"  focus: {member.CurrentFocusSummary}");
            builder.AppendLine($"  standup: {member.StandupSummary}");
            builder.AppendLine($"  output: {member.RecentOutputSummary}");
            builder.AppendLine($"  risk: {member.RiskSummary}");
        }
    }

    private static void AppendMemberSignalsV2(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 4)
    {
        var signals = insight.Knowledge.MemberSignals
            .OrderByDescending(x => x.Date)
            .ThenBy(x => x.ReliabilityScore)
            .Take(Math.Max(1, maxItems))
            .ToList();

        builder.AppendLine();
        if (signals.Count == 0)
        {
            builder.AppendLine("Chua co member daily signal gan day.");
            return;
        }

        builder.AppendLine("Tin hieu member gan day:");
        foreach (var signal in signals)
        {
            var evidence = signal.Evidence.Count == 0 ? "khong ro" : string.Join(", ", signal.Evidence);
            var lateText = signal.WasLate ? $" | late `{signal.LateMinutes ?? 0}`m" : string.Empty;
            builder.AppendLine(
                $"- {signal.Date:yyyy-MM-dd} {FormatMemberLabelV2(insight, signal.DiscordUserId)} | reliability `{signal.ReliabilityScore}` | standup `{(signal.SubmittedStandup ? "co" : "thieu")}`{lateText} | blocker `{(signal.HasBlocker ? "co" : "khong")}` | completed `{signal.CompletedTasks}` | evidence {evidence}{FormatMemberConfidenceV2(insight, signal.DiscordUserId)}");
        }
    }

    private static string ResolveMemberDisplayNameV2(ProjectAssistantContext insight, ulong userId)
    {
        var profileName = insight.Knowledge.Members
            .FirstOrDefault(x => x.DiscordUserId == userId)?
            .DisplayName;

        if (!string.IsNullOrWhiteSpace(profileName))
        {
            return profileName;
        }

        var recentConversationName = insight.RecentConversation
            .LastOrDefault(x => x.AuthorId == userId && !string.IsNullOrWhiteSpace(x.AuthorName))
            ?.AuthorName;

        if (!string.IsNullOrWhiteSpace(recentConversationName))
        {
            return recentConversationName;
        }

        var memoryName = insight.Memory.RelevantMessages
            .FirstOrDefault(x => x.AuthorId == userId && !string.IsNullOrWhiteSpace(x.AuthorName))
            ?.AuthorName;

        return string.IsNullOrWhiteSpace(memoryName)
            ? $"<@{userId}>"
            : memoryName;
    }

    private static string FormatMemberLabelV2(ProjectAssistantContext insight, ulong userId)
    {
        var displayName = ResolveMemberDisplayNameV2(insight, userId);
        var mention = $"<@{userId}>";
        return string.Equals(displayName, mention, StringComparison.Ordinal)
            ? mention
            : $"{displayName} ({mention})";
    }

    private static string FormatMemberConfidenceV2(ProjectAssistantContext insight, ulong userId)
    {
        var confidence = insight.Knowledge.Members
            .FirstOrDefault(x => x.DiscordUserId == userId)?
            .ConfidencePercent;

        return confidence.HasValue
            ? $" | confidence `{confidence.Value}`"
            : string.Empty;
    }

    private static object BuildStandupSliceV2(
        ProjectAssistantContext insight,
        int maxRecentStandups = 6,
        int maxLateReporters = 4,
        int maxMissingReporters = 4)
    {
        return new
        {
            insight.StandupDiscipline.LookbackDays,
            insight.StandupDiscipline.DueTimeLocal,
            insight.StandupDiscipline.ExpectedReporterCount,
            recentStandups = insight.Standups
                .Take(maxRecentStandups)
                .Select(x => new
                {
                    x.Date,
                    x.DiscordUserId,
                    DisplayName = ResolveMemberDisplayNameV2(insight, x.DiscordUserId),
                    x.ReportedAtLocal,
                    x.Yesterday,
                    x.Today,
                    x.Blockers,
                    x.HasBlockers
                })
                .ToList(),
            lateReporters = insight.StandupDiscipline.LateReporters
                .Take(maxLateReporters)
                .Select(x => new
                {
                    x.DiscordUserId,
                    DisplayName = ResolveMemberDisplayNameV2(insight, x.DiscordUserId),
                    x.TotalReports,
                    x.LateReports,
                    x.OnTimeReports,
                    x.LateRatePercent,
                    x.AverageLateMinutes,
                    x.LastReportedAtLocal,
                    x.WasLateLastReport
                })
                .ToList(),
            missingReporters = insight.StandupDiscipline.MissingReporters
                .Take(maxMissingReporters)
                .Select(x => new
                {
                    x.DiscordUserId,
                    DisplayName = ResolveMemberDisplayNameV2(insight, x.DiscordUserId),
                    x.MissingDays,
                    x.SubmittedDays,
                    x.LastMissingDate,
                    x.MissingToday,
                    x.BasisSummary
                })
                .ToList()
        };
    }

    private static object BuildMemberSliceV2(
        ProjectAssistantContext insight,
        int maxProfiles = 5,
        int maxSignals = 6,
        int maxWorkloads = 5)
    {
        return new
        {
            memberProfiles = insight.Knowledge.Members
                .OrderBy(x => x.ReliabilityScore)
                .ThenByDescending(x => x.OpenPoints)
                .Take(maxProfiles)
                .Select(x => new
                {
                    x.DiscordUserId,
                    x.DisplayName,
                    x.RoleSummary,
                    x.SkillKeywords,
                    x.DominantTopics,
                    x.ActiveChannels,
                    x.MissingStandupDays,
                    x.LateStandupRatePercent,
                    x.AverageLateMinutes,
                    x.BlockerDays,
                    x.CompletedTasksRecent,
                    x.FixedBugsRecent,
                    x.OpenTaskCount,
                    x.OpenBugCount,
                    x.OpenPoints,
                    x.ReliabilityScore,
                    x.ConfidencePercent,
                    x.StandupSummary,
                    x.CurrentFocusSummary,
                    x.RecentOutputSummary,
                    x.RiskSummary,
                    x.EvidenceSummary
                })
                .ToList(),
            memberSignals = insight.Knowledge.MemberSignals
                .OrderByDescending(x => x.Date)
                .ThenBy(x => x.ReliabilityScore)
                .Take(maxSignals)
                .Select(x => new
                {
                    x.Date,
                    x.DiscordUserId,
                    DisplayName = ResolveMemberDisplayNameV2(insight, x.DiscordUserId),
                    x.ExpectedStandup,
                    x.SubmittedStandup,
                    x.WasLate,
                    x.LateMinutes,
                    x.HasBlocker,
                    x.CompletedTasks,
                    x.FixedBugs,
                    x.ActivityCount,
                    x.OpenTaskCount,
                    x.OpenBugCount,
                    x.OpenPoints,
                    x.ReliabilityScore,
                    ConfidencePercent = insight.Knowledge.Members.FirstOrDefault(member => member.DiscordUserId == x.DiscordUserId)?.ConfidencePercent,
                    x.Evidence
                })
                .ToList(),
            memberWorkloads = insight.MemberWorkloads
                .OrderByDescending(x => x.OpenPoints)
                .ThenByDescending(x => x.OpenTaskCount)
                .ThenByDescending(x => x.OpenBugCount)
                .Take(maxWorkloads)
                .Select(x => new
                {
                    x.DiscordUserId,
                    DisplayName = ResolveMemberDisplayNameV2(insight, x.DiscordUserId),
                    x.OpenTaskCount,
                    x.InProgressTaskCount,
                    x.OpenBugCount,
                    x.OpenPoints,
                    x.RecentActivityCount,
                    ConfidencePercent = insight.Knowledge.Members.FirstOrDefault(member => member.DiscordUserId == x.DiscordUserId)?.ConfidencePercent
                })
                .ToList()
        };
    }

    private static object BuildTaskExecutionSliceV2(
        ProjectAssistantContext insight,
        int maxStalledTasks = 5,
        int maxAttentionItems = 5)
    {
        return new
        {
            taskFlow = new
            {
                insight.TaskFlow.LookbackDays,
                insight.TaskFlow.TotalEvents,
                insight.TaskFlow.CreatedTasks,
                insight.TaskFlow.CompletedTasks,
                insight.TaskFlow.CreatedBugs,
                insight.TaskFlow.FixedBugs,
                insight.TaskFlow.ReturnedToBacklog,
                topActors = insight.TaskFlow.TopActors
                    .Take(4)
                    .Select(x => new
                    {
                        x.DiscordUserId,
                        DisplayName = ResolveMemberDisplayNameV2(insight, x.DiscordUserId),
                        x.EventCount,
                        x.CompletedTasks,
                        x.FixedBugs,
                        x.ClaimedOrAssignedTasks
                    })
                    .ToList()
            },
            completedTasks = insight.CompletedTasks
                .Take(8)
                .Select(x => new
                {
                    x.TaskId,
                    x.Title,
                    x.Points,
                    x.AssigneeId,
                    AssigneeName = x.AssigneeId.HasValue ? ResolveMemberDisplayNameV2(insight, x.AssigneeId.Value) : null,
                    x.IsInActiveSprint
                })
                .ToList(),
            sprints = insight.SprintCatalog
                .Take(4)
                .Select(x => new
                {
                    x.SprintId,
                    x.Name,
                    x.Goal,
                    x.IsActive,
                    x.StartDateLocal,
                    x.EndDateLocal,
                    x.TotalTasks,
                    x.DoneTasks,
                    x.TodoTasks,
                    x.InProgressTasks,
                    tasks = x.TaskItems
                        .Take(6)
                        .Select(task => new
                        {
                            task.TaskId,
                            task.Title,
                            task.Status,
                            task.Points,
                            task.AssigneeId,
                            AssigneeName = task.AssigneeId.HasValue ? ResolveMemberDisplayNameV2(insight, task.AssigneeId.Value) : null
                        })
                        .ToList()
                })
                .ToList(),
            stalledTasks = insight.StalledTasks
                .Take(maxStalledTasks)
                .Select(x => new
                {
                    x.TaskId,
                    x.Title,
                    x.Status,
                    x.Points,
                    x.AssigneeId,
                    AssigneeName = x.AssigneeId.HasValue ? ResolveMemberDisplayNameV2(insight, x.AssigneeId.Value) : null,
                    x.AgeDays,
                    x.DaysWithoutChange,
                    x.Reason,
                    x.IsOverdue,
                    x.Evidence
                })
                .ToList(),
            attentionItems = insight.AttentionItems
                .Take(maxAttentionItems)
                .Select(x => new
                {
                    x.Kind,
                    x.Title,
                    x.Summary,
                    x.TaskId,
                    x.Status,
                    x.Points,
                    x.AssigneeId,
                    AssigneeName = x.AssigneeId.HasValue ? ResolveMemberDisplayNameV2(insight, x.AssigneeId.Value) : null
                })
                .ToList()
        };
    }

    private static string BuildIntentGuidanceV2(AssistantIntent intent)
    {
        return intent switch
        {
            AssistantIntent.ProgressReview =>
                "Current intent: progress_review. Prioritize sprint health, delivery vs timeline, task flow, stalled work, risk trend, and the top actionable items. Keep conclusions tied to metrics and recent evidence.",
            AssistantIntent.StandupDiscipline =>
                "Current intent: standup_discipline. Prioritize due time, recent standups, late reporters, missing reporters, blocker evidence, time range, and member daily signals. Prefer display names over raw IDs when available.",
            AssistantIntent.MemberInsights =>
                "Current intent: member_insights. Prioritize member profiles, member daily signals, current workloads, recent task activity, and evidence-backed reliability. Do not imply someone is struggling unless there is blocker, repeated lateness, stalled work, or weak activity evidence.",
            AssistantIntent.TopicHistory =>
                "Current intent: topic_history. Prioritize daily digests, topic summaries, decision logs, risk logs, and only a few relevant historical messages as evidence. Mention the time range explicitly.",
            AssistantIntent.DecisionHistory =>
                "Current intent: decision_history. Prioritize decision logs first, then supporting topics and relevant messages. Include evidence and confidence when available.",
            AssistantIntent.RiskReview =>
                "Current intent: risk_review. Prioritize risk logs, risk trend, sprint trend, standup blockers, stalled work, and the top current risks. Separate facts from inferences.",
            AssistantIntent.TaskExecution =>
                "Current intent: task_execution. Prioritize stalled tasks, open bugs, task flow, owners, and actionable attention items. Use evidence before saying a task or person is blocked.",
            AssistantIntent.Estimation =>
                "Current intent: estimation. Be explicit that estimate confidence depends on task description, dependencies, and technical unknowns.",
            _ =>
                "Current intent: general_project_assistant. Use the available context slice and keep the answer concise, evidence-based, and actionable. Prefer display names over raw IDs when available."
        };
    }

    private static string BuildSystemPromptV2()
    {
        return
            "You are a project assistant inside a Discord scrum bot. " +
            "Always answer in Vietnamese. " +
            "Use only the provided context. If the data is missing, say that clearly instead of guessing. " +
            "Be concise, natural, and actionable. " +
            "Separate facts from inferences. " +
            "Do not say someone seems stuck, overloaded, or problematic unless blockers, stalled tasks, overdue work, repeated missing standups, or repeated late reports explicitly support that. " +
            "The JSON context is an intent-specific slice, not the full project graph. " +
            "The project context is project-wide, not limited to the current channel, as long as the current channel belongs to that project. " +
            "The memory section contains archived project messages, daily digests, and relevant historical traces. " +
            "The knowledge section contains structured member profiles, member daily signals, topic mentions, decision logs, risk logs, sprint trend points, and risk trend points. " +
            "For progress questions, cite the important metrics. " +
            "For prioritization questions, focus on blockers, overdue work, stalled tasks, unassigned work, open bugs, and high-point unfinished tasks. " +
            "For late or missing standup questions, use the standupDiscipline summary and do not invent missing reports that are not in the data. " +
            "Missing reporters are inferred heuristically from open task assignees, recent standup reporters, and repeated recent project participants. " +
            "For stalled task questions, use the stalledTasks list, daysWithoutChange, evidence, and recent taskFlow data before concluding that a task is stuck. " +
            "Use memberWorkloads and member profiles for questions about who is carrying the most open work right now. " +
            "For weekly discussion or history questions, rely on topics, decisions, risks, and daily digests first, then use relevant historical messages as evidence. " +
            "Prefer display names over raw Discord IDs when available. " +
            "When answering about people, topics, decisions, or risks, mention evidence, time range, and confidence when the context provides them. " +
            "For story point questions, provide an estimate with rationale and uncertainty. " +
            "Do not claim you searched the whole Discord if the context only contains summaries or recent messages.";
    }

    private static AssistantIntent ClassifyIntent(string question)
    {
        var lower = question.Trim().ToLowerInvariant();
        if (LooksLikeGreeting(lower))
        {
            return AssistantIntent.Greeting;
        }

        if (ContainsAny(lower, "point", "uoc luong", "estimate"))
        {
            return AssistantIntent.Estimation;
        }

        if (ContainsAny(lower, "bao cao", "standup", "tre bao cao", "khong nop", "chua nop", "missing report", "miss standup"))
        {
            return AssistantIntent.StandupDiscipline;
        }

        if (ContainsAny(lower, "member", "thanh vien", "workload", "qua tai", "dang giu", "ai dang lam nhieu", "ai lam nhieu", "phu hop", "ai hop task"))
        {
            return AssistantIntent.MemberInsights;
        }

        if (ContainsAny(lower, "quyet dinh", "decision", "chot gi", "thong nhat", "chot"))
        {
            return AssistantIntent.DecisionHistory;
        }

        if (ContainsAny(lower, "chu de", "topic", "ban nhieu", "nhac nhieu", "hot topic", "tuan qua", "gan day", "da ban", "thao luan", "lich su", "nho lai", "van de gi"))
        {
            return AssistantIntent.TopicHistory;
        }

        if (ContainsAny(lower, "rui ro", "risk", "hot issue", "van de nong", "lech"))
        {
            return AssistantIntent.RiskReview;
        }

        if (ContainsAny(lower, "dinh tre", "stalled", "ket", "tac", "task", "xu ly", "uu tien", "can lam", "bug"))
        {
            return AssistantIntent.TaskExecution;
        }

        if (ContainsAny(lower, "tich cuc", "tieu cuc", "tien do", "sprint", "health"))
        {
            return AssistantIntent.ProgressReview;
        }

        return AssistantIntent.General;
    }

    private static bool LooksLikeGreeting(string lowerQuestion)
    {
        var normalized = lowerQuestion.Trim();
        return normalized is "hi" or "hello" or "hey" or "alo" or "yo" or "chao" or "xin chao"
            || normalized.StartsWith("hello ", StringComparison.Ordinal)
            || normalized.StartsWith("hey ", StringComparison.Ordinal)
            || normalized.StartsWith("alo ", StringComparison.Ordinal)
            || normalized.StartsWith("chao ", StringComparison.Ordinal)
            || normalized.StartsWith("xin chao ", StringComparison.Ordinal);
    }

    private static string BuildIntentGuidance(AssistantIntent intent)
    {
        return intent switch
        {
            AssistantIntent.ProgressReview =>
                "Current intent: progress_review. Prioritize sprint health, delivery vs timeline, task flow, stalled work, risk trend, and the top actionable items.",
            AssistantIntent.StandupDiscipline =>
                "Current intent: standup_discipline. Prioritize due time, recent standups, late reporters, missing reporters, blocker evidence, and member daily signals.",
            AssistantIntent.MemberInsights =>
                "Current intent: member_insights. Prioritize member profiles, member daily signals, current workloads, recent task activity, and evidence-backed reliability.",
            AssistantIntent.TopicHistory =>
                "Current intent: topic_history. Prioritize daily digests, topic summaries, decision logs, risk logs, and only a few relevant historical messages as evidence.",
            AssistantIntent.DecisionHistory =>
                "Current intent: decision_history. Prioritize decision logs first, then supporting topics and relevant messages.",
            AssistantIntent.RiskReview =>
                "Current intent: risk_review. Prioritize risk logs, risk trend, sprint trend, standup blockers, stalled work, and the top current risks.",
            AssistantIntent.TaskExecution =>
                "Current intent: task_execution. Prioritize stalled tasks, open bugs, task flow, owners, and actionable attention items.",
            AssistantIntent.Estimation =>
                "Current intent: estimation. Be explicit that estimate confidence depends on task description, dependencies, and technical unknowns.",
            _ =>
                "Current intent: general_project_assistant. Use the available context slice and keep the answer concise, evidence-based, and actionable."
        };
    }

    private static object BuildAiContextEnvelope(ProjectAssistantContext insight, AssistantIntent intent)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["intent"] = intent.ToString().ToLowerInvariant(),
            ["generatedAtLocal"] = insight.GeneratedAtLocal,
            ["scope"] = new
            {
                insight.Scope.ProjectId,
                insight.Scope.ProjectName,
                insight.Scope.ChannelId,
                insight.Scope.ChannelName,
                insight.Scope.ParentChannelId,
                insight.Scope.ParentChannelName,
                insight.Scope.AskingUserId,
                insight.Scope.AskingUserName
            },
            ["recentConversation"] = insight.RecentConversation
                .TakeLast(6)
                .Select(x => new
                {
                    x.TimestampLocal,
                    x.AuthorId,
                    x.AuthorName,
                    x.IsBot,
                    x.Content
                })
                .ToList()
        };

        switch (intent)
        {
            case AssistantIntent.ProgressReview:
                envelope["sprint"] = BuildSprintSlice(insight);
                envelope["taskExecution"] = BuildTaskExecutionSliceV2(insight);
                envelope["risk"] = BuildRiskSlice(insight);
                break;

            case AssistantIntent.StandupDiscipline:
                envelope["sprint"] = BuildSprintSlice(insight);
                envelope["standup"] = BuildStandupSliceV2(insight);
                envelope["members"] = BuildMemberSliceV2(insight, maxProfiles: 3, maxSignals: 5, maxWorkloads: 3);
                break;

            case AssistantIntent.MemberInsights:
                envelope["sprint"] = BuildSprintSlice(insight);
                envelope["members"] = BuildMemberSliceV2(insight);
                envelope["taskExecution"] = BuildTaskExecutionSliceV2(insight, maxStalledTasks: 3, maxAttentionItems: 3);
                break;

            case AssistantIntent.DecisionHistory:
                envelope["history"] = BuildHistorySlice(insight, maxDigests: 2, maxMessages: 2, maxTopics: 3, maxDecisions: 4, maxRisks: 2);
                break;

            case AssistantIntent.TopicHistory:
                envelope["history"] = BuildHistorySlice(insight);
                envelope["taskExecution"] = BuildTaskExecutionSliceV2(insight, maxStalledTasks: 3, maxAttentionItems: 3);
                break;

            case AssistantIntent.RiskReview:
                envelope["sprint"] = BuildSprintSlice(insight);
                envelope["standup"] = BuildStandupSliceV2(insight, maxRecentStandups: 4, maxLateReporters: 3, maxMissingReporters: 3);
                envelope["risk"] = BuildRiskSlice(insight);
                break;

            case AssistantIntent.TaskExecution:
                envelope["sprint"] = BuildSprintSlice(insight);
                envelope["taskExecution"] = BuildTaskExecutionSliceV2(insight);
                envelope["members"] = BuildMemberSliceV2(insight, maxProfiles: 3, maxSignals: 3, maxWorkloads: 4);
                break;

            case AssistantIntent.Estimation:
                envelope["sprint"] = BuildSprintSlice(insight);
                envelope["members"] = BuildMemberSliceV2(insight, maxProfiles: 3, maxSignals: 2, maxWorkloads: 3);
                break;

            default:
                envelope["sprint"] = BuildSprintSlice(insight);
                envelope["standup"] = BuildStandupSliceV2(insight, maxRecentStandups: 3, maxLateReporters: 2, maxMissingReporters: 2);
                envelope["taskExecution"] = BuildTaskExecutionSliceV2(insight, maxStalledTasks: 3, maxAttentionItems: 3);
                envelope["risk"] = BuildRiskSlice(insight, maxRisks: 3, maxTrendPoints: 3);
                break;
        }

        return envelope;
    }

    private static object BuildSprintSlice(ProjectAssistantContext insight)
    {
        return new
        {
            insight.Sprint.HasActiveSprint,
            insight.Sprint.SprintId,
            insight.Sprint.Name,
            insight.Sprint.Goal,
            insight.Sprint.StartDateLocal,
            insight.Sprint.EndDateLocal,
            insight.Sprint.TotalTasks,
            insight.Sprint.DoneTasks,
            insight.Sprint.TodoTasks,
            insight.Sprint.InProgressTasks,
            insight.Sprint.ProjectBacklogCount,
            insight.Sprint.OpenBugCount,
            insight.Sprint.TotalPoints,
            insight.Sprint.DonePoints,
            insight.Sprint.InProgressPoints,
            insight.Sprint.DeliveryProgressPercent,
            insight.Sprint.ScheduleProgressPercent,
            health = new
            {
                insight.Sprint.Health.Label,
                insight.Sprint.Health.Summary,
                insight.Sprint.Health.DeltaPercent
            }
        };
    }

    private static object BuildStandupSlice(
        ProjectAssistantContext insight,
        int maxRecentStandups = 6,
        int maxLateReporters = 4,
        int maxMissingReporters = 4)
    {
        return new
        {
            insight.StandupDiscipline.LookbackDays,
            insight.StandupDiscipline.DueTimeLocal,
            insight.StandupDiscipline.ExpectedReporterCount,
            recentStandups = insight.Standups
                .Take(maxRecentStandups)
                .Select(x => new
                {
                    x.Date,
                    x.DiscordUserId,
                    x.ReportedAtLocal,
                    x.Yesterday,
                    x.Today,
                    x.Blockers,
                    x.HasBlockers
                })
                .ToList(),
            lateReporters = insight.StandupDiscipline.LateReporters
                .Take(maxLateReporters)
                .Select(x => new
                {
                    x.DiscordUserId,
                    x.TotalReports,
                    x.LateReports,
                    x.OnTimeReports,
                    x.LateRatePercent,
                    x.AverageLateMinutes,
                    x.LastReportedAtLocal,
                    x.WasLateLastReport
                })
                .ToList(),
            missingReporters = insight.StandupDiscipline.MissingReporters
                .Take(maxMissingReporters)
                .Select(x => new
                {
                    x.DiscordUserId,
                    x.MissingDays,
                    x.SubmittedDays,
                    x.LastMissingDate,
                    x.MissingToday,
                    x.BasisSummary
                })
                .ToList()
        };
    }

    private static object BuildMemberSlice(
        ProjectAssistantContext insight,
        int maxProfiles = 5,
        int maxSignals = 6,
        int maxWorkloads = 5)
    {
        return new
        {
            memberProfiles = insight.Knowledge.Members
                .OrderBy(x => x.ReliabilityScore)
                .ThenByDescending(x => x.OpenPoints)
                .Take(maxProfiles)
                .Select(x => new
                {
                    x.DiscordUserId,
                    x.DisplayName,
                    x.RoleSummary,
                    x.SkillKeywords,
                    x.ActiveChannels,
                    x.OpenTaskCount,
                    x.OpenBugCount,
                    x.OpenPoints,
                    x.ReliabilityScore,
                    x.ConfidencePercent,
                    x.EvidenceSummary
                })
                .ToList(),
            memberSignals = insight.Knowledge.MemberSignals
                .OrderByDescending(x => x.Date)
                .ThenBy(x => x.ReliabilityScore)
                .Take(maxSignals)
                .Select(x => new
                {
                    x.Date,
                    x.DiscordUserId,
                    x.ExpectedStandup,
                    x.SubmittedStandup,
                    x.WasLate,
                    x.LateMinutes,
                    x.HasBlocker,
                    x.CompletedTasks,
                    x.FixedBugs,
                    x.ActivityCount,
                    x.OpenTaskCount,
                    x.OpenBugCount,
                    x.OpenPoints,
                    x.ReliabilityScore,
                    x.Evidence
                })
                .ToList(),
            memberWorkloads = insight.MemberWorkloads
                .OrderByDescending(x => x.OpenPoints)
                .ThenByDescending(x => x.OpenTaskCount)
                .ThenByDescending(x => x.OpenBugCount)
                .Take(maxWorkloads)
                .Select(x => new
                {
                    x.DiscordUserId,
                    x.OpenTaskCount,
                    x.InProgressTaskCount,
                    x.OpenBugCount,
                    x.OpenPoints,
                    x.RecentActivityCount
                })
                .ToList()
        };
    }

    private static object BuildTaskExecutionSlice(
        ProjectAssistantContext insight,
        int maxStalledTasks = 5,
        int maxAttentionItems = 5)
    {
        return new
        {
            taskFlow = new
            {
                insight.TaskFlow.LookbackDays,
                insight.TaskFlow.TotalEvents,
                insight.TaskFlow.CreatedTasks,
                insight.TaskFlow.CompletedTasks,
                insight.TaskFlow.CreatedBugs,
                insight.TaskFlow.FixedBugs,
                insight.TaskFlow.ReturnedToBacklog,
                topActors = insight.TaskFlow.TopActors
                    .Take(4)
                    .Select(x => new
                    {
                        x.DiscordUserId,
                        x.EventCount,
                        x.CompletedTasks,
                        x.FixedBugs,
                        x.ClaimedOrAssignedTasks
                    })
                    .ToList()
            },
            completedTasks = insight.CompletedTasks
                .Take(8)
                .Select(x => new
                {
                    x.TaskId,
                    x.Title,
                    x.Points,
                    x.AssigneeId,
                    x.IsInActiveSprint
                })
                .ToList(),
            sprints = insight.SprintCatalog
                .Take(4)
                .Select(x => new
                {
                    x.SprintId,
                    x.Name,
                    x.Goal,
                    x.IsActive,
                    x.StartDateLocal,
                    x.EndDateLocal,
                    x.TotalTasks,
                    x.DoneTasks,
                    x.TodoTasks,
                    x.InProgressTasks,
                    tasks = x.TaskItems
                        .Take(6)
                        .Select(task => new
                        {
                            task.TaskId,
                            task.Title,
                            task.Status,
                            task.Points,
                            task.AssigneeId
                        })
                        .ToList()
                })
                .ToList(),
            stalledTasks = insight.StalledTasks
                .Take(maxStalledTasks)
                .Select(x => new
                {
                    x.TaskId,
                    x.Title,
                    x.Status,
                    x.Points,
                    x.AssigneeId,
                    x.AgeDays,
                    x.DaysWithoutChange,
                    x.Reason,
                    x.IsOverdue,
                    x.Evidence
                })
                .ToList(),
            attentionItems = insight.AttentionItems
                .Take(maxAttentionItems)
                .Select(x => new
                {
                    x.Kind,
                    x.Title,
                    x.Summary,
                    x.TaskId,
                    x.Status,
                    x.Points,
                    x.AssigneeId
                })
                .ToList()
        };
    }

    private static object BuildHistorySlice(
        ProjectAssistantContext insight,
        int maxDigests = 3,
        int maxMessages = 4,
        int maxTopics = 5,
        int maxDecisions = 4,
        int maxRisks = 4)
    {
        return new
        {
            memoryCoverage = new
            {
                insight.Memory.ArchivedMessageCount,
                insight.Memory.OldestLocalDate,
                insight.Memory.LatestLocalDate
            },
            dailyDigests = insight.Memory.DailyDigests
                .Take(maxDigests)
                .Select(x => new
                {
                    x.Date,
                    x.Summary,
                    x.MessageCount,
                    x.DistinctAuthorCount,
                    x.BlockerCount,
                    x.TopKeywords,
                    x.ActiveChannels,
                    x.Highlights
                })
                .ToList(),
            relevantMessages = insight.Memory.RelevantMessages
                .Take(maxMessages)
                .Select(x => new
                {
                    x.TimestampLocal,
                    x.ChannelName,
                    x.ThreadName,
                    x.AuthorName,
                    x.Content
                })
                .ToList(),
            topics = insight.Knowledge.Topics
                .Take(maxTopics)
                .Select(x => new
                {
                    x.TopicKey,
                    x.MentionCount,
                    x.DistinctAuthorCount,
                    x.TopChannels,
                    x.TopAuthors,
                    x.Summary
                })
                .ToList(),
            decisions = insight.Knowledge.Decisions
                .Take(maxDecisions)
                .Select(x => new
                {
                    x.Date,
                    x.TopicKey,
                    x.Summary,
                    x.Evidence,
                    x.ConfidencePercent,
                    x.SourceChannelName
                })
                .ToList(),
            risks = insight.Knowledge.Risks
                .Take(maxRisks)
                .Select(x => new
                {
                    x.Date,
                    x.RiskKey,
                    x.Severity,
                    x.Summary,
                    x.Evidence,
                    x.ConfidencePercent
                })
                .ToList()
        };
    }

    private static object BuildRiskSlice(
        ProjectAssistantContext insight,
        int maxRisks = 5,
        int maxTrendPoints = 4)
    {
        return new
        {
            currentRisks = insight.Knowledge.Risks
                .Take(maxRisks)
                .Select(x => new
                {
                    x.Date,
                    x.RiskKey,
                    x.Severity,
                    x.Summary,
                    x.Evidence,
                    x.ConfidencePercent
                })
                .ToList(),
            sprintTrend = insight.Knowledge.SprintTrend
                .OrderBy(x => x.Date)
                .TakeLast(maxTrendPoints)
                .Select(x => new
                {
                    x.Date,
                    x.DeliveryProgressPercent,
                    x.ScheduleProgressPercent,
                    x.OpenBugCount,
                    x.StalledTaskCount,
                    x.HealthLabel,
                    x.HealthDeltaPercent
                })
                .ToList(),
            riskTrend = insight.Knowledge.RiskTrend
                .OrderBy(x => x.Date)
                .TakeLast(maxTrendPoints)
                .Select(x => new
                {
                    x.Date,
                    x.RiskScore,
                    x.OpenRiskCount,
                    x.OverdueTaskCount,
                    x.StalledTaskCount,
                    x.MissingStandupCount,
                    x.OpenBugCount,
                    x.BlockerCount,
                    x.Summary
                })
                .ToList(),
            attentionItems = insight.AttentionItems
                .Take(4)
                .Select(x => new
                {
                    x.Kind,
                    x.Title,
                    x.Summary,
                    x.TaskId,
                    x.Status,
                    x.Points,
                    x.AssigneeId
                })
                .ToList()
        };
    }

    private static string BuildSystemPrompt()
    {
        return
            "You are a project assistant inside a Discord scrum bot. " +
            "Always answer in Vietnamese. " +
            "Use only the provided context. If the data is missing, say that clearly instead of guessing. " +
            "Be concise, natural, and actionable. " +
            "The JSON context is an intent-specific slice, not the full project graph. " +
            "The project context is project-wide, not limited to the current channel, as long as the current channel belongs to that project. " +
            "The memory section contains archived project messages, daily digests, and relevant historical traces. " +
            "The knowledge section contains structured member profiles, member daily signals, topic mentions, decision logs, risk logs, sprint trend points, and risk trend points. " +
            "For progress questions, cite the important metrics. " +
            "For prioritization questions, focus on blockers, overdue work, stalled tasks, unassigned work, open bugs, and high-point unfinished tasks. " +
            "For late or missing standup questions, use the standupDiscipline summary and do not invent missing reports that are not in the data. " +
            "Missing reporters are inferred heuristically from open task assignees, recent standup reporters, and repeated recent project participants. " +
            "For stalled task questions, use the stalledTasks list, daysWithoutChange, evidence, and recent taskFlow data before concluding that a task is stuck. " +
            "Use memberWorkloads and member profiles for questions about who is carrying the most open work right now. " +
            "For weekly discussion or history questions, rely on topics, decisions, risks, and daily digests first, then use relevant historical messages as evidence. " +
            "When answering about people, topics, decisions, or risks, mention evidence, time range, and confidence when the context provides them. " +
            "For story point questions, provide an estimate with rationale and uncertainty. " +
            "Do not claim you searched the whole Discord if the context only contains summaries or recent messages.";
    }

    private static string BuildChatCompletionsEndpoint(string baseUrl)
    {
        return $"{baseUrl.TrimEnd('/')}/chat/completions";
    }

    private static string? TryReadAssistantContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var messageElement))
        {
            return null;
        }

        if (!messageElement.TryGetProperty("content", out var contentElement))
        {
            return null;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString();
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.Append(item.GetString());
                continue;
            }

            if (!item.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.TryGetProperty("text", out var textElement))
            {
                builder.Append(textElement.GetString());
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? TryBuildDeterministicResponse(
        string question,
        ProjectAssistantContext insight,
        AssistantIntent intent)
    {
        var lowerQuestion = question.Trim().ToLowerInvariant();
        if (!LooksLikeDeterministicFactQuery(lowerQuestion, intent))
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Du an: `{insight.Scope.ProjectName}`");
        builder.AppendLine($"Snapshot luc: `{insight.GeneratedAtLocal:yyyy-MM-dd HH:mm}`");

        switch (intent)
        {
            case AssistantIntent.StandupDiscipline:
                AppendDeterministicStandupFacts(builder, insight, lowerQuestion);
                break;

            case AssistantIntent.TaskExecution:
                AppendDeterministicTaskFacts(builder, insight, lowerQuestion);
                break;

            case AssistantIntent.MemberInsights:
                AppendDeterministicMemberFacts(builder, insight);
                break;

            case AssistantIntent.ProgressReview:
                AppendDeterministicProgressFacts(builder, insight, lowerQuestion);
                break;

            default:
                if (ContainsAny(lowerQuestion, "bao cao", "standup"))
                {
                    AppendDeterministicStandupFacts(builder, insight, lowerQuestion);
                }
                else if (ContainsAny(lowerQuestion, "task", "bug", "backlog", "dinh tre", "stalled"))
                {
                    AppendDeterministicTaskFacts(builder, insight, lowerQuestion);
                }
                else if (ContainsAny(lowerQuestion, "member", "thanh vien", "workload", "qua tai"))
                {
                    AppendDeterministicMemberFacts(builder, insight);
                }
                else
                {
                    AppendDeterministicProgressFacts(builder, insight, lowerQuestion);
                }

                break;
        }

        builder.AppendLine();
        builder.Append("Luu y: day la cau tra loi tat dinh tu snapshot DB/memory hien co, khong qua dien giai AI.");
        return builder.ToString().Trim();
    }

    private static bool LooksLikeDeterministicFactQuery(string lowerQuestion, AssistantIntent intent)
    {
        var asksCompletedTaskList = WantsCompletedTasksOnly(lowerQuestion);
        var asksSprintTaskList = LooksLikeSprintTaskQuery(lowerQuestion);
        var asksStandupReliability = LooksLikeStandupReliabilityQuery(lowerQuestion);

        var hasFactCue = ContainsAny(
            lowerQuestion,
            "bao nhieu",
            "co bao nhieu",
            "con bao nhieu",
            "so luong",
            "tong so",
            "tong cong",
            "trang thai",
            "status",
            "count",
            "ti le",
            "phan tram",
            "con lai",
            "done bao nhieu",
            "xong bao nhieu",
            "mo bao nhieu",
            "dong bao nhieu")
            || asksCompletedTaskList
            || asksSprintTaskList
            || asksStandupReliability;

        if (!hasFactCue)
        {
            return false;
        }

        return intent is AssistantIntent.General
            or AssistantIntent.ProgressReview
            or AssistantIntent.StandupDiscipline
            or AssistantIntent.TaskExecution
            or AssistantIntent.MemberInsights;
    }

    private static void AppendDeterministicProgressFacts(
        StringBuilder builder,
        ProjectAssistantContext insight,
        string lowerQuestion)
    {
        builder.AppendLine();
        if (WantsPerSprintBreakdown(lowerQuestion))
        {
            if (insight.SprintCatalog.Count == 0)
            {
                builder.AppendLine("Khong tim thay sprint nao trong project.");
                return;
            }

            builder.AppendLine("Tong quan tung sprint:");
            foreach (var sprint in insight.SprintCatalog.Take(6))
            {
                builder.AppendLine(
                    $"- `{sprint.Name}`{(sprint.IsActive ? " (active)" : string.Empty)} | total `{sprint.TotalTasks}` | done `{sprint.DoneTasks}` | todo `{sprint.TodoTasks}` | in-progress `{sprint.InProgressTasks}`");
            }

            return;
        }

        var requestedSprint = TryResolveRequestedSprint(insight, lowerQuestion);
        if (requestedSprint is not null && !LooksLikeSprintTaskQuery(lowerQuestion))
        {
            builder.AppendLine($"Sprint: `{requestedSprint.Name}`{(requestedSprint.IsActive ? " (active)" : string.Empty)}");
            builder.AppendLine($"- Task: total `{requestedSprint.TotalTasks}` | done `{requestedSprint.DoneTasks}` | todo `{requestedSprint.TodoTasks}` | in-progress `{requestedSprint.InProgressTasks}`");
            if (requestedSprint.StartDateLocal.HasValue || requestedSprint.EndDateLocal.HasValue)
            {
                builder.AppendLine($"- Timeline: `{requestedSprint.StartDateLocal:yyyy-MM-dd}` -> `{requestedSprint.EndDateLocal:yyyy-MM-dd}`");
            }

            if (!string.IsNullOrWhiteSpace(requestedSprint.Goal))
            {
                builder.AppendLine($"- Goal: {requestedSprint.Goal}");
            }

            return;
        }

        if (!insight.Sprint.HasActiveSprint)
        {
            builder.AppendLine("Khong co sprint active.");
            builder.AppendLine($"- Project backlog: `{insight.Sprint.ProjectBacklogCount}` task");
            builder.AppendLine($"- Bug mo: `{insight.Sprint.OpenBugCount}`");
            return;
        }

        builder.AppendLine($"Sprint: `{insight.Sprint.Name}`");
        builder.AppendLine($"- Task: total `{insight.Sprint.TotalTasks}` | done `{insight.Sprint.DoneTasks}` | todo `{insight.Sprint.TodoTasks}` | in-progress `{insight.Sprint.InProgressTasks}` | backlog-trong-sprint `{insight.Sprint.BacklogTasksInSprint}`");
        builder.AppendLine($"- Point: total `{insight.Sprint.TotalPoints}` | done `{insight.Sprint.DonePoints}` | in-progress `{insight.Sprint.InProgressPoints}`");
        builder.AppendLine($"- Project backlog ngoai sprint: `{insight.Sprint.ProjectBacklogCount}` task");
        builder.AppendLine($"- Bug mo: `{insight.Sprint.OpenBugCount}`");
        builder.AppendLine($"- Delivery: `{insight.Sprint.DeliveryProgressPercent}%`");

        if (insight.Sprint.ScheduleProgressPercent.HasValue)
        {
            builder.AppendLine($"- Timeline: `{insight.Sprint.ScheduleProgressPercent.Value}%`");
        }

        builder.AppendLine($"- Health: `{insight.Sprint.Health.Label}`");

        if (insight.Sprint.Health.DeltaPercent.HasValue)
        {
            builder.AppendLine($"- Delta delivery vs timeline: `{insight.Sprint.Health.DeltaPercent.Value}%`");
        }

        if (insight.Sprint.EndDateLocal.HasValue)
        {
            var remainingDays = Math.Max(0, (insight.Sprint.EndDateLocal.Value.Date - insight.GeneratedAtLocal.Date).Days);
            builder.AppendLine($"- Con lai: `{remainingDays}` ngay den `{insight.Sprint.EndDateLocal.Value:yyyy-MM-dd}`");
        }
    }

    private static void AppendDeterministicStandupFacts(
        StringBuilder builder,
        ProjectAssistantContext insight,
        string lowerQuestion)
    {
        var today = insight.GeneratedAtLocal.Date;
        var reportsToday = insight.Standups
            .Where(x => x.Date.Date == today)
            .ToList();
        var submittedToday = reportsToday
            .Select(x => x.DiscordUserId)
            .Distinct()
            .Count();
        var lateToday = reportsToday.Count(x => x.ReportedAtLocal.TimeOfDay > insight.StandupDiscipline.DueTimeLocal);
        var blockerToday = reportsToday.Count(x => x.HasBlockers);
        var missingToday = insight.StandupDiscipline.MissingReporters.Count(x => x.MissingToday);
        var lateReporterCount = insight.StandupDiscipline.LateReporters.Count(x => x.LateReports > 0);
        var asksMissingOnly = AsksAboutMissingStandups(lowerQuestion) && !AsksAboutLateStandups(lowerQuestion);
        var asksLateOnly = AsksAboutLateStandups(lowerQuestion) && !AsksAboutMissingStandups(lowerQuestion);

        builder.AppendLine();
        builder.AppendLine($"Standup due: `{insight.StandupDiscipline.DueTimeLocal:hh\\:mm}` | cua so: `{insight.StandupDiscipline.LookbackDays}` ngay");
        builder.AppendLine($"- Expected reporters: `{insight.StandupDiscipline.ExpectedReporterCount}`");
        builder.AppendLine($"- Da nop hom nay: `{submittedToday}`");
        builder.AppendLine($"- Chua nop hom nay: `{missingToday}`");
        builder.AppendLine($"- Bao cao co blocker hom nay: `{blockerToday}`");
        builder.AppendLine($"- Bao cao tre hom nay: `{lateToday}`");
        builder.AppendLine($"- So nguoi tung tre trong cua so theo doi: `{lateReporterCount}`");

        if (asksMissingOnly)
        {
            var missingLines = insight.StandupDiscipline.MissingReporters
                .Take(5)
                .Select(x => $"- Thieu: {FormatMemberLabelV2(insight, x.DiscordUserId)} | missing `{x.MissingDays}` ngay | da nop `{x.SubmittedDays}` ngay | basis `{x.BasisSummary}`")
                .ToList();

            builder.AppendLine("Nguoi hay thieu bao cao:");
            if (missingLines.Count == 0)
            {
                builder.AppendLine("- Chua thay ai bi thieu bao cao trong cua so theo doi hien tai.");
            }
            else
            {
                foreach (var line in missingLines)
                {
                    builder.AppendLine(line);
                }
            }

            builder.AppendLine("Ghi chu: danh sach nay chi tinh ngay thieu bao cao, khong cong ngay nop tre vao missing.");
            return;
        }

        if (asksLateOnly)
        {
            var lateLines = insight.StandupDiscipline.LateReporters
                .Where(x => x.LateReports > 0)
                .Take(5)
                .Select(x => $"- Tre: {FormatMemberLabelV2(insight, x.DiscordUserId)} | tre `{x.LateReports}/{x.TotalReports}` lan | dung gio `{x.OnTimeReports}` lan | rate `{x.LateRatePercent}%`")
                .ToList();

            builder.AppendLine("Nguoi hay tre bao cao:");
            if (lateLines.Count == 0)
            {
                builder.AppendLine("- Chua thay ai nop tre trong cua so theo doi hien tai.");
            }
            else
            {
                foreach (var line in lateLines)
                {
                    builder.AppendLine(line);
                }
            }

            return;
        }

        if (ContainsAny(lowerQuestion, "ai", "nguoi nao"))
        {
            var lateLines = insight.StandupDiscipline.LateReporters
                .Where(x => x.LateReports > 0)
                .Take(3)
                .Select(x => $"- Tre: {FormatMemberLabelV2(insight, x.DiscordUserId)} | `{x.LateReports}/{x.TotalReports}` lan | rate `{x.LateRatePercent}%`")
                .ToList();
            var missingLines = insight.StandupDiscipline.MissingReporters
                .Where(x => x.MissingToday)
                .Take(3)
                .Select(x => $"- Chua nop hom nay: {FormatMemberLabelV2(insight, x.DiscordUserId)} | missing `{x.MissingDays}` ngay | basis `{x.BasisSummary}`")
                .ToList();

            if (lateLines.Count > 0 || missingLines.Count > 0)
            {
                builder.AppendLine("Danh sach noi bat:");
                foreach (var line in lateLines.Concat(missingLines))
                {
                    builder.AppendLine(line);
                }
            }
        }

        builder.AppendLine("Ghi chu: missing reporters la heuristic tu open task assignees, recent standup reporters, va repeated project participants.");
    }

    private static void AppendDeterministicTaskFacts(
        StringBuilder builder,
        ProjectAssistantContext insight,
        string lowerQuestion)
    {
        var overdueCount = insight.AttentionItems.Count(x => x.Kind == "overdue_task");
        var unassignedCount = insight.AttentionItems.Count(x => x.Kind == "unassigned_task");
        var highPointNotStartedCount = insight.AttentionItems.Count(x => x.Kind == "high_point_not_started");
        var openBugAttentionCount = insight.AttentionItems.Count(x => x.Kind == "open_bug");

        builder.AppendLine();
        if (insight.Sprint.HasActiveSprint)
        {
            builder.AppendLine($"Task trong sprint `{insight.Sprint.Name}`:");
            builder.AppendLine($"- Total `{insight.Sprint.TotalTasks}` | done `{insight.Sprint.DoneTasks}` | todo `{insight.Sprint.TodoTasks}` | in-progress `{insight.Sprint.InProgressTasks}` | backlog-trong-sprint `{insight.Sprint.BacklogTasksInSprint}`");
        }

        builder.AppendLine($"- Project backlog ngoai sprint: `{insight.Sprint.ProjectBacklogCount}`");
        builder.AppendLine($"- Bug mo: `{insight.Sprint.OpenBugCount}`");
        builder.AppendLine($"- Stalled task: `{insight.StalledTasks.Count}`");
        builder.AppendLine($"- Overdue task: `{overdueCount}`");
        builder.AppendLine($"- Unassigned task: `{unassignedCount}`");
        builder.AppendLine($"- High-point chua start: `{highPointNotStartedCount}`");
        builder.AppendLine($"- Open-bug attention item: `{openBugAttentionCount}`");
        builder.AppendLine($"- Task flow {insight.TaskFlow.LookbackDays} ngay: tao `{insight.TaskFlow.CreatedTasks}` | done `{insight.TaskFlow.CompletedTasks}` | mo bug `{insight.TaskFlow.CreatedBugs}` | dong bug `{insight.TaskFlow.FixedBugs}` | tra backlog `{insight.TaskFlow.ReturnedToBacklog}`");

        var asksCompletedTaskList = WantsCompletedTasksOnly(lowerQuestion);
        var asksPerSprintBreakdown = WantsPerSprintBreakdown(lowerQuestion);
        var requestedSprint = TryResolveRequestedSprint(insight, lowerQuestion);
        var requestedStatus = TryResolveRequestedTaskStatus(lowerQuestion);

        if (asksPerSprintBreakdown)
        {
            if (insight.SprintCatalog.Count == 0)
            {
                builder.AppendLine("- Khong tim thay sprint nao trong project.");
                return;
            }

            builder.AppendLine("Task theo tung sprint:");
            foreach (var sprint in insight.SprintCatalog.Take(4))
            {
                var sprintTasks = FilterSprintTaskItems(sprint, requestedStatus);
                builder.AppendLine(
                    $"- `{sprint.Name}`{(sprint.IsActive ? " (active)" : string.Empty)} | total `{sprint.TotalTasks}` | done `{sprint.DoneTasks}` | todo `{sprint.TodoTasks}` | in-progress `{sprint.InProgressTasks}`");

                var lines = sprintTasks
                    .Take(5)
                    .Select(task => FormatSprintTaskLine(insight, task))
                    .ToList();

                if (lines.Count == 0)
                {
                    builder.AppendLine("  - Khong co task nao khop bo loc trong sprint nay.");
                }
                else
                {
                    foreach (var line in lines)
                    {
                        builder.AppendLine($"  {line}");
                    }
                }
            }

            return;
        }

        if (requestedSprint is not null)
        {
            var sprintTasks = FilterSprintTaskItems(requestedSprint, requestedStatus);
            builder.AppendLine($"Task trong `{requestedSprint.Name}`{(requestedSprint.IsActive ? " (active)" : string.Empty)}:");
            builder.AppendLine(
                $"- Total `{requestedSprint.TotalTasks}` | done `{requestedSprint.DoneTasks}` | todo `{requestedSprint.TodoTasks}` | in-progress `{requestedSprint.InProgressTasks}`");

            var lines = sprintTasks
                .Take(10)
                .Select(task => FormatSprintTaskLine(insight, task))
                .ToList();

            if (lines.Count == 0)
            {
                builder.AppendLine("- Khong tim thay task nao khop trong sprint nay.");
            }
            else
            {
                foreach (var line in lines)
                {
                    builder.AppendLine(line);
                }
            }

            return;
        }

        if (asksCompletedTaskList)
        {
            var completedTasks = insight.CompletedTasks.Take(8).ToList();
            if (completedTasks.Count == 0)
            {
                builder.AppendLine("- Hien chua thay task nao o trang thai `Done` trong pham vi snapshot nay.");
            }
            else
            {
                builder.AppendLine("Task da hoan thanh:");
                foreach (var task in completedTasks)
                {
                    var owner = task.AssigneeId.HasValue
                        ? $" | owner {FormatMemberLabelV2(insight, task.AssigneeId.Value)}"
                        : string.Empty;
                    var scope = task.IsInActiveSprint ? " | active sprint" : string.Empty;
                    builder.AppendLine($"- #{task.TaskId} {task.Title} | `{task.Points}d`{owner}{scope}");
                }
            }
        }

        if (ContainsAny(lowerQuestion, "ai", "owner", "nguoi nao"))
        {
            var topOwners = insight.MemberWorkloads
                .Take(3)
                .Select(x => $"- {FormatMemberLabelV2(insight, x.DiscordUserId)}: open `{x.OpenTaskCount}` task | bug `{x.OpenBugCount}` | points `{x.OpenPoints}`")
                .ToList();

            if (topOwners.Count > 0)
            {
                builder.AppendLine("Nguoi dang giu viec nhieu nhat:");
                foreach (var line in topOwners)
                {
                    builder.AppendLine(line);
                }
            }
        }
    }

    private static void AppendDeterministicMemberFacts(StringBuilder builder, ProjectAssistantContext insight)
    {
        builder.AppendLine();
        builder.AppendLine($"Member profile tracked: `{insight.Knowledge.Members.Count}`");
        builder.AppendLine($"Member dang giu viec: `{insight.MemberWorkloads.Count}`");

        var topMembers = insight.MemberWorkloads
            .Take(5)
            .ToList();

        if (topMembers.Count == 0)
        {
            builder.AppendLine("- Chua thay member nao dang giu task/bug mo.");
            return;
        }

        foreach (var member in topMembers)
        {
            builder.AppendLine(
                $"- {FormatMemberLabelV2(insight, member.DiscordUserId)}: open `{member.OpenTaskCount}` task | in-progress `{member.InProgressTaskCount}` | bug `{member.OpenBugCount}` | points `{member.OpenPoints}` | recent-activity `{member.RecentActivityCount}`{FormatMemberConfidenceV2(insight, member.DiscordUserId)}");
        }
    }

    private static bool LooksLikeSprintTaskQuery(string lowerQuestion)
    {
        var mentionsSprint = lowerQuestion.Contains("sprint", StringComparison.Ordinal);
        var mentionsTask = ContainsAny(
            lowerQuestion,
            "task",
            "cong viec",
            "công việc",
            "bug",
            "backlog");
        var mentionsStatus = TryResolveRequestedTaskStatus(lowerQuestion) is not null;

        return WantsPerSprintBreakdown(lowerQuestion)
            || (mentionsSprint && (mentionsTask || mentionsStatus));
    }

    private static bool WantsPerSprintBreakdown(string lowerQuestion)
    {
        return ContainsAny(
            lowerQuestion,
            "tung sprint",
            "từng sprint",
            "moi sprint",
            "mỗi sprint",
            "cac sprint",
            "các sprint",
            "theo sprint",
            "tat ca sprint",
            "tất cả sprint");
    }

    private static bool WantsCompletedTasksOnly(string lowerQuestion)
    {
        return ContainsAny(lowerQuestion, "hoan thanh", "hoàn thành", "da xong", "đã xong", "done")
            && ContainsAny(lowerQuestion, "task", "nhung task nao", "những task nào", "task nao", "task nào");
    }

    private static bool LooksLikeStandupReliabilityQuery(string lowerQuestion)
    {
        return ContainsAny(lowerQuestion, "bao cao", "báo cáo", "standup")
            && (ContainsAny(lowerQuestion, "ai", "nguoi nao", "người nào")
                || AsksAboutMissingStandups(lowerQuestion)
                || AsksAboutLateStandups(lowerQuestion)
                || ContainsAny(lowerQuestion, "thuong xuyen", "thường xuyên", "hay"));
    }

    private static bool AsksAboutMissingStandups(string lowerQuestion)
    {
        return ContainsAny(
            lowerQuestion,
            "bo bao cao",
            "bỏ báo cáo",
            "khong nop",
            "không nộp",
            "chua nop",
            "chưa nộp",
            "thieu bao cao",
            "thiếu báo cáo",
            "vang mat",
            "vắng mặt",
            "miss");
    }

    private static bool AsksAboutLateStandups(string lowerQuestion)
    {
        return ContainsAny(
            lowerQuestion,
            "tre bao cao",
            "trễ báo cáo",
            "nop tre",
            "nộp trễ",
            "muon",
            "muộn",
            "dung gio",
            "đúng giờ");
    }

    private static string? TryResolveRequestedTaskStatus(string lowerQuestion)
    {
        if (ContainsAny(lowerQuestion, "hoan thanh", "hoàn thành", "da xong", "đã xong", "done"))
        {
            return "Done";
        }

        if (ContainsAny(lowerQuestion, "in-progress", "in progress", "dang lam", "đang làm"))
        {
            return "InProgress";
        }

        if (ContainsAny(lowerQuestion, "todo", "chua bat dau", "chưa bắt đầu"))
        {
            return "Todo";
        }

        if (ContainsAny(lowerQuestion, "backlog"))
        {
            return "Backlog";
        }

        return null;
    }

    private static AssistantSprintTaskList? TryResolveRequestedSprint(ProjectAssistantContext insight, string lowerQuestion)
    {
        if (insight.SprintCatalog.Count == 0)
        {
            return null;
        }

        if (ContainsAny(lowerQuestion, "sprint nay", "sprint hiện tại", "sprint hien tai", "active sprint", "current sprint"))
        {
            return insight.SprintCatalog.FirstOrDefault(x => x.IsActive) ?? insight.SprintCatalog.FirstOrDefault();
        }

        var idMatch = Regex.Match(lowerQuestion, @"\bsprint\s*#?\s*(\d+)\b", RegexOptions.IgnoreCase);
        if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out var sprintNumber))
        {
            return insight.SprintCatalog.FirstOrDefault(x =>
                x.SprintId == sprintNumber
                || x.Name.Contains($"sprint {sprintNumber}", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains($"#{sprintNumber}", StringComparison.OrdinalIgnoreCase));
        }

        return insight.SprintCatalog.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.Name)
            && lowerQuestion.Contains(x.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<AssistantSprintTaskItem> FilterSprintTaskItems(
        AssistantSprintTaskList sprint,
        string? requestedStatus)
    {
        var taskQuery = sprint.TaskItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(requestedStatus))
        {
            taskQuery = taskQuery.Where(x => string.Equals(x.Status, requestedStatus, StringComparison.OrdinalIgnoreCase));
        }

        return taskQuery
            .OrderByDescending(x => x.Status.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Status.Equals("Todo", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Status.Equals("Done", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.TaskId)
            .ToList();
    }

    private static string FormatSprintTaskLine(ProjectAssistantContext insight, AssistantSprintTaskItem task)
    {
        var owner = task.AssigneeId.HasValue
            ? $" | owner {FormatMemberLabelV2(insight, task.AssigneeId.Value)}"
            : " | chua assign";

        return $"- #{task.TaskId} {task.Title} | `{task.Status}` | `{task.Points}d`{owner}";
    }

    private static bool ContainsAny(string source, params string[] keywords)
    {
        return keywords.Any(source.Contains);
    }

    private static string ClampForDiscord(string value)
    {
        return value;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }
}

internal enum AssistantIntent
{
    General,
    Greeting,
    ProgressReview,
    StandupDiscipline,
    MemberInsights,
    DecisionHistory,
    TopicHistory,
    RiskReview,
    TaskExecution,
    Estimation
}
