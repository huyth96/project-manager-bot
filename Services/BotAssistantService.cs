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
        var insight = await _projectInsightService.BuildContextAsync(message, cancellationToken);
        if (insight is null)
        {
            return "Kênh này chưa gắn với dự án nào nên tôi chưa có dữ liệu sprint/task để trả lời. Hãy hỏi trong kênh project hoặc chạy `/project setup` trước.";
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
                "Bạn có thể hỏi như: `@bot backlog hiện ra sao`, `@bot task nào cần ưu tiên`, `@bot khi nào nên bắt đầu sprint mới`.";
        }

        return
            $"Sprint `{insight.Sprint.Name}` đang có `{insight.Sprint.DoneTasks}/{insight.Sprint.TotalTasks}` task done " +
            $"và `{insight.Sprint.DonePoints}/{Math.Max(insight.Sprint.TotalPoints, 0)}` points hoàn thành.\n" +
            "Bạn có thể hỏi như: `@bot tiến độ sprint thế nào`, `@bot task nào cần xử lý ngay`, `@bot team đang tích cực hay tiêu cực`.";
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
            builder.AppendLine("Assistant AI chưa được cấu hình, nên tôi đang trả lời theo snapshot hiện tại.");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Tôi tạm trả lời theo snapshot hiện tại vì service AI chưa phản hồi được.");
            builder.AppendLine();
        }

        AppendSprintSummary(builder, insight);

        if (ContainsAny(lowerQuestion, "task", "xử lý", "xu ly", "ưu tiên", "uu tien", "cần làm", "can lam", "bug"))
        {
            AppendAttentionItems(builder, insight);
        }
        else if (ContainsAny(lowerQuestion, "tích cực", "tich cuc", "tiêu cực", "tieu cuc", "tiến độ", "tien do", "sprint", "health"))
        {
            AppendHealthSummary(builder, insight);
            AppendAttentionItems(builder, insight, maxItems: 3);
        }
        else if (ContainsAny(lowerQuestion, "point", "điểm", "uớc lượng", "ước lượng", "estimate"))
        {
            builder.AppendLine();
            builder.Append("Để gợi ý điểm tốt hơn, hãy gửi rõ tên task, mô tả, độ phức tạp kỹ thuật, dependency và phạm vi UI/API. ");
            builder.Append("Khi có API key cho assistant, bot sẽ diễn giải estimate tự nhiên hơn.");
        }
        else
        {
            AppendHealthSummary(builder, insight);
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

    private static void AppendAttentionItems(StringBuilder builder, ProjectAssistantContext insight, int maxItems = 5)
    {
        var items = insight.AttentionItems.Take(Math.Max(1, maxItems)).ToList();
        if (items.Count == 0)
        {
            builder.AppendLine();
            builder.Append("Hiện chưa thấy mục nào nổi bật cần escalte ngay trong snapshot.");
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
            "For progress questions, cite the important metrics. " +
            "For prioritization questions, focus on blockers, overdue work, unassigned work, open bugs, and high-point unfinished tasks. " +
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
