using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;

namespace ProjectManagerBot.Services;

public sealed class NotificationService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    DiscordSocketClient client,
    ILogger<NotificationService> logger)
{
    private const string GlobalTaskFeedChannelName = "global-task-feed";

    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly DiscordSocketClient _client = client;
    private readonly ILogger<NotificationService> _logger = logger;

    public async Task NotifyTaskClaimedAsync(
        int projectId,
        ulong actorDiscordId,
        IReadOnlyCollection<TaskItem> tasks,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        await SendAsync(
            projectId,
            new EmbedBuilder()
                .WithTitle("📯 Sứ Giả Báo Tin • Nhận Quest")
                .WithColor(Color.DarkBlue)
                .WithDescription(
                    "⚔️ Cập Nhật Chiến Trường\n" +
                    $"**Người nhận:** <@{actorDiscordId}>\n" +
                    $"**Số quest:** `{tasks.Count}`\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "📜 Danh sách\n" +
                    BuildTaskList(tasks))
                .WithCurrentTimestamp()
                .Build(),
            cancellationToken);
    }

    public async Task NotifyTaskCompletedAsync(
        int projectId,
        ulong actorDiscordId,
        IReadOnlyCollection<TaskItem> tasks,
        int awardedXp,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        await SendAsync(
            projectId,
            new EmbedBuilder()
                .WithTitle("🏆 Sứ Giả Báo Tin • Hoàn Thành Quest")
                .WithColor(Color.DarkGreen)
                .WithDescription(
                    "🛡️ Thành Tích Mới\n" +
                    $"**Người hoàn thành:** <@{actorDiscordId}>\n" +
                    $"**Số quest:** `{tasks.Count}`\n" +
                    $"**XP nhận được:** `+{awardedXp}`\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "📜 Danh sách\n" +
                    BuildTaskList(tasks))
                .WithCurrentTimestamp()
                .Build(),
            cancellationToken);
    }

    public async Task NotifyTaskAssignedAsync(
        int projectId,
        ulong assignedByDiscordId,
        ulong assigneeDiscordId,
        TaskItem task,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(
            projectId,
            new EmbedBuilder()
                .WithTitle("👑 Lệnh Triệu Hồi • Giao Nhiệm Vụ")
                .WithColor(Color.Gold)
                .WithDescription(
                    "🎯 Phân Công\n" +
                    $"- **Giao bởi:** <@{assignedByDiscordId}>\n" +
                    $"- **Người nhận:** <@{assigneeDiscordId}>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    $"📌 Quest #{task.Id}\n" +
                    $"- **Tên:** **{task.Title}**\n" +
                    $"- **Điểm:** `{task.Points}`\n" +
                    $"- **Trạng thái:** `{task.Status}`")
                .WithCurrentTimestamp()
                .Build(),
            cancellationToken);
    }

    public async Task NotifyOverdueTaskAsync(
        int projectId,
        TaskItem task,
        TimeSpan overdueBy,
        CancellationToken cancellationToken = default)
    {
        var overdueHours = Math.Max(1, (int)Math.Round(overdueBy.TotalHours));

        await SendAsync(
            projectId,
            new EmbedBuilder()
                .WithTitle("⏳ Cảnh Báo Quá Hạn")
                .WithColor(Color.DarkOrange)
                .WithDescription(
                    "🚨 Nhiệm vụ quá hạn\n" +
                    $"📌 Quest #{task.Id}\n" +
                    $"- **Quá hạn:** `{overdueHours}h`\n" +
                    $"- **Tên:** **{task.Title}**\n" +
                    $"- **Trạng thái:** `{task.Status}`\n" +
                    $"- **Người xử lý:** {(task.AssigneeId.HasValue ? $"<@{task.AssigneeId.Value}>" : "`Chưa có`")}\n\n" +
                    "> ⚠️ Vui lòng cập nhật tiến độ hoặc đóng nhiệm vụ ngay.")
                .WithCurrentTimestamp()
                .Build(),
            cancellationToken);
    }

    public async Task NotifySprintStartedAsync(
        int projectId,
        ulong actorDiscordId,
        Sprint sprint,
        CancellationToken cancellationToken = default)
    {
        var startText = sprint.StartDateLocal?.ToString("yyyy-MM-dd") ?? "N/A";
        var endText = sprint.EndDateLocal?.ToString("yyyy-MM-dd") ?? "N/A";

        await SendAsync(
            projectId,
            new EmbedBuilder()
                .WithTitle("🚩 Khởi Chạy Chiến Dịch Mới")
                .WithColor(Color.DarkPurple)
                .WithDescription(
                    "⚔️ Sprint Đã Bắt Đầu\n" +
                    $"- **Kích hoạt bởi:** <@{actorDiscordId}>\n" +
                    $"- **Tên sprint:** **{sprint.Name}**\n" +
                    $"- **Mục tiêu:** **{sprint.Goal}**\n" +
                    $"- **Thời gian:** `{startText} -> {endText}`")
                .WithCurrentTimestamp()
                .Build(),
            cancellationToken);
    }

    public async Task NotifySprintEndedAsync(
        int projectId,
        ulong actorDiscordId,
        Sprint sprint,
        int velocity,
        int completedCount,
        int rolledBackCount,
        CancellationToken cancellationToken = default)
    {
        var startText = sprint.StartDateLocal?.ToString("yyyy-MM-dd") ?? "N/A";
        var endText = sprint.EndDateLocal?.ToString("yyyy-MM-dd") ?? "N/A";

        await SendAsync(
            projectId,
            new EmbedBuilder()
                .WithTitle("🏁 Đóng Chiến Dịch")
                .WithColor(Color.Orange)
                .WithDescription(
                    "📊 Tổng kết sprint\n" +
                    $"- **Thực hiện bởi:** <@{actorDiscordId}>\n" +
                    $"- **Sprint:** **{sprint.Name}**\n" +
                    $"- **Thời gian:** `{startText} -> {endText}`\n" +
                    $"- **Velocity:** `{velocity}`\n" +
                    $"- **Hoàn thành:** `{completedCount}`\n" +
                    $"- **Trả về backlog:** `{rolledBackCount}`")
                .WithCurrentTimestamp()
                .Build(),
            cancellationToken);
    }

    private async Task SendAsync(int projectId, Embed embed, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        var channel = await ResolveGlobalChannelAsync(db, project, cancellationToken);
        if (channel is null)
        {
            _logger.LogWarning("Global task feed channel missing for project {ProjectId}", projectId);
            return;
        }

        await channel.SendMessageAsync(embed: embed);
    }

    private async Task<ITextChannel?> ResolveGlobalChannelAsync(
        BotDbContext db,
        Project project,
        CancellationToken cancellationToken)
    {
        if (project.GlobalNotificationChannelId.HasValue)
        {
            var mapped = _client.GetChannel(project.GlobalNotificationChannelId.Value) as ITextChannel;
            if (mapped is not null)
            {
                return mapped;
            }
        }

        SocketGuild? guild;
        if (_client.GetChannel(project.ChannelId) is SocketGuildChannel guildChannel)
        {
            guild = guildChannel.Guild;
        }
        else
        {
            guild = _client.Guilds.FirstOrDefault();
        }

        if (guild is null)
        {
            return null;
        }

        var discovered = guild.TextChannels.FirstOrDefault(x =>
            x.Name.Equals(GlobalTaskFeedChannelName, StringComparison.OrdinalIgnoreCase))
            ?? guild.TextChannels.FirstOrDefault(x =>
                x.Name.Contains("global", StringComparison.OrdinalIgnoreCase) &&
                x.Name.Contains("task", StringComparison.OrdinalIgnoreCase));

        if (discovered is null)
        {
            return null;
        }

        project.GlobalNotificationChannelId = discovered.Id;
        await db.SaveChangesAsync(cancellationToken);
        return discovered;
    }

    private static string BuildTaskList(IReadOnlyCollection<TaskItem> tasks)
    {
        var preview = tasks.Take(8).ToList();
        var lines = preview.Select(x =>
            $"- `#{x.Id}` **{x.Title}** • `{x.Points} điểm`");

        var text = string.Join("\n", lines);
        if (tasks.Count > preview.Count)
        {
            text += $"\n- ...và `{tasks.Count - preview.Count}` quest khác";
        }

        return text;
    }
}


