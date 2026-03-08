using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return ClampForDiscord(BuildFallbackResponse(question, insight, aiAvailable: false));
        }

        try
        {
            var aiReply = await GenerateAiReplyAsync(question, insight, cancellationToken);
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

        return ClampForDiscord(BuildFallbackResponse(question, insight, aiAvailable: true));
    }

    private async Task<string?> GenerateAiReplyAsync(
        string question,
        ProjectAssistantContext insight,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildChatCompletionsEndpoint(_options.BaseUrl);
        var payload = new
        {
            model = _options.Model,
            temperature = Math.Clamp(_options.Temperature, 0, 1),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildSystemPrompt()
                },
                new
                {
                    role = "system",
                    content = "Structured project context JSON:\n" + JsonSerializer.Serialize(insight, JsonOptions)
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
            AppendLateReporters(builder, insight);
            AppendMissingReporters(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "không nộp", "khong nop", "chưa nộp", "chua nop", "missing report", "miss standup"))
        {
            AppendMissingReporters(builder, insight);
            AppendLateReporters(builder, insight, maxItems: 2);
        }
        else if (ContainsAny(lowerQuestion, "đình trệ", "dinh tre", "stalled", "kẹt", "ket", "tắc", "tac"))
        {
            AppendStalledTasks(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "tuần qua", "tuan qua", "gần đây", "gan day", "đã bàn", "da ban", "thảo luận", "thao luan", "lịch sử", "lich su", "nhắc lại", "nho lai", "vấn đề gì", "van de gi"))
        {
            AppendMemoryOverview(builder, insight);
            AppendRelevantMessages(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "task", "xử lý", "xu ly", "ưu tiên", "uu tien", "cần làm", "can lam", "bug"))
        {
            AppendAttentionItems(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "tích cực", "tich cuc", "tiêu cực", "tieu cuc", "tiến độ", "tien do", "sprint", "health"))
        {
            AppendHealthSummary(builder, insight);
            AppendAttentionItems(builder, insight, maxItems: 3);
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
            AppendLateReporters(builder, insight, maxItems: 2);
            AppendMissingReporters(builder, insight, maxItems: 2);
            AppendStalledTasks(builder, insight, maxItems: 2);
            AppendAttentionItems(builder, insight, maxItems: 3);
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

    private static string BuildSystemPrompt()
    {
        return
            "You are a project assistant inside a Discord scrum bot. " +
            "Always answer in Vietnamese. " +
            "Use only the provided context. If the data is missing, say that clearly instead of guessing. " +
            "Be concise, natural, and actionable. " +
            "The project context is project-wide, not limited to the current channel, as long as the current channel belongs to that project. " +
            "The memory section contains archived project messages, daily digests, and relevant historical traces. " +
            "For progress questions, cite the important metrics. " +
            "For prioritization questions, focus on blockers, overdue work, stalled tasks, unassigned work, open bugs, and high-point unfinished tasks. " +
            "For late or missing standup questions, use the standupDiscipline summary and do not invent missing reports that are not in the data. " +
            "Missing reporters are inferred heuristically from open task assignees, recent standup reporters, and repeated recent project participants. " +
            "For stalled task questions, use the stalledTasks list and mention that the heuristic is based on task age and current status because there is no status-change history yet. " +
            "For weekly discussion or history questions, rely on the daily digests first, then use relevant historical messages as evidence. " +
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

    private static bool ContainsAny(string source, params string[] keywords)
    {
        return keywords.Any(source.Contains);
    }

    private static string ClampForDiscord(string value)
    {
        const int maxLength = 1900;
        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }
}
