using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;

namespace ProjectManagerBot.Services;

public sealed class ProjectService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    DiscordSocketClient client,
    StudioTimeService studioTime,
    ILogger<ProjectService> logger)
{
    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly DiscordSocketClient _client = client;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly ILogger<ProjectService> _logger = logger;

    public static MessageComponent BuildDashboardComponents()
    {
        return new ComponentBuilder()
            .WithButton("📜 Backlog", "dashboard:add_backlog", ButtonStyle.Secondary)
            .WithButton("🧙 Nhiệm Vụ Của Tôi", "dashboard:my_tasks", ButtonStyle.Primary)
            .WithButton("🐞 Báo Bug", "dashboard:report_bug", ButtonStyle.Danger)
            .WithButton("🗺️ Quest Board", "dashboard:view_board", ButtonStyle.Secondary)
            .WithButton("👑 Bảng Điều Phối", "dashboard:admin_panel", ButtonStyle.Success)
            .Build();
    }

    public async Task<Project> UpsertProjectAsync(
        string name,
        ulong channelId,
        ulong bugChannelId,
        ulong standupChannelId,
        ulong? globalNotificationChannelId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var project = await db.Projects.FirstOrDefaultAsync(x => x.ChannelId == channelId, cancellationToken);
        if (project is null)
        {
            project = new Project
            {
                Name = name,
                ChannelId = channelId,
                BugChannelId = bugChannelId,
                StandupChannelId = standupChannelId,
                GlobalNotificationChannelId = globalNotificationChannelId
            };

            db.Projects.Add(project);
        }
        else
        {
            project.Name = name;
            project.BugChannelId = bugChannelId;
            project.StandupChannelId = standupChannelId;
            project.GlobalNotificationChannelId = globalNotificationChannelId ?? project.GlobalNotificationChannelId;
        }

        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task<Project?> GetProjectByChannelAsync(ulong channelId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ChannelId == channelId || x.BugChannelId == channelId || x.StandupChannelId == channelId,
                cancellationToken);
    }

    public async Task<Project?> GetProjectByIdAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
    }

    public async Task<Sprint?> GetActiveSprintAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sprints
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive, cancellationToken);
    }

    public async Task<Embed> BuildDashboardEmbedAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await BuildDashboardEmbedInternalAsync(db, projectId, cancellationToken);
    }

    public async Task RefreshDashboardMessageAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        var channel = _client.GetChannel(project.ChannelId) as ITextChannel;
        if (channel is null)
        {
            _logger.LogWarning("Dashboard channel {ChannelId} not found for project {ProjectId}", project.ChannelId, projectId);
            return;
        }

        var embed = await BuildDashboardEmbedInternalAsync(db, projectId, cancellationToken);
        var components = BuildDashboardComponents();

        IUserMessage? dashboardMessage = null;
        if (project.DashboardMessageId.HasValue)
        {
            dashboardMessage = await channel.GetMessageAsync(project.DashboardMessageId.Value) as IUserMessage;
        }

        if (dashboardMessage is null)
        {
            dashboardMessage = await channel.SendMessageAsync(embed: embed, components: components);
            project.DashboardMessageId = dashboardMessage.Id;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        await dashboardMessage.ModifyAsync(properties =>
        {
            properties.Embed = embed;
            properties.Components = components;
        });
    }

    public async Task<int> AwardXpAsync(ulong discordId, int xp, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(x => x.DiscordId == discordId, cancellationToken);
        if (user is null)
        {
            user = new User
            {
                DiscordId = discordId,
                XP = 0
            };

            db.Users.Add(user);
        }

        user.XP += xp;
        await db.SaveChangesAsync(cancellationToken);
        return user.XP;
    }

    public async Task SaveStandupReportAsync(
        int projectId,
        ulong discordUserId,
        string yesterday,
        string today,
        string blockers,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var localDate = _studioTime.LocalDate;
        var existing = await db.StandupReports.FirstOrDefaultAsync(
            x => x.ProjectId == projectId && x.DiscordUserId == discordUserId && x.LocalDate == localDate,
            cancellationToken);

        if (existing is null)
        {
            existing = new StandupReport
            {
                ProjectId = projectId,
                DiscordUserId = discordUserId,
                LocalDate = localDate,
                Yesterday = yesterday,
                Today = today,
                Blockers = blockers
            };

            db.StandupReports.Add(existing);
        }
        else
        {
            existing.Yesterday = yesterday;
            existing.Today = today;
            existing.Blockers = blockers;
            existing.ReportedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RefreshStandupSummaryAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null || project.DailySummaryMessageId is null)
        {
            return;
        }

        var channel = _client.GetChannel(project.StandupChannelId) as ITextChannel;
        if (channel is null)
        {
            return;
        }

        var summaryMessage = await channel.GetMessageAsync(project.DailySummaryMessageId.Value) as IUserMessage;
        if (summaryMessage is null)
        {
            return;
        }

        var embed = await BuildStandupSummaryEmbedInternalAsync(db, projectId, _studioTime.LocalDate, cancellationToken);
        await summaryMessage.ModifyAsync(properties =>
        {
            properties.Embed = embed;
            properties.Components = new ComponentBuilder()
                .WithButton("🧾 Nộp Standup", $"standup:report:{projectId}", ButtonStyle.Primary)
                .Build();
        });
    }

    public async Task<(ulong? MessageId, DateTime LocalDate)> OpenDailyStandupAsync(
        int projectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null)
        {
            return (null, _studioTime.LocalDate);
        }

        var standupChannel = _client.GetChannel(project.StandupChannelId) as ITextChannel;
        if (standupChannel is null)
        {
            _logger.LogWarning("Standup channel {ChannelId} not found for project {ProjectId}", project.StandupChannelId, projectId);
            return (null, _studioTime.LocalDate);
        }

        var localDate = _studioTime.LocalDate;
        var summaryEmbed = await BuildStandupSummaryEmbedInternalAsync(db, projectId, localDate, cancellationToken);

        var message = await standupChannel.SendMessageAsync(
            embed: summaryEmbed,
            components: new ComponentBuilder()
                .WithButton("🧾 Nộp Standup", $"standup:report:{projectId}", ButtonStyle.Primary)
                .Build());

        project.DailySummaryMessageId = message.Id;
        project.LastStandupDateLocal = localDate;
        await db.SaveChangesAsync(cancellationToken);

        return (message.Id, localDate);
    }

    private static string BuildProgressLine(int total, int done)
    {
        if (total <= 0)
        {
            return $"{BuildProgressBar(0, 0)}\n- **Hoàn thành:** `0/0`";
        }

        var percent = (int)Math.Round((double)done / total * 100, MidpointRounding.AwayFromZero);
        return $"{BuildProgressBar(done, total)}\n- **Hoàn thành:** `{done}/{total}` (`{percent}%`)";
    }

    private static string BuildProgressBar(int done, int total, int width = 14)
    {
        if (total <= 0)
        {
            return $"[{new string('░', width)}] 0%";
        }

        var ratio = (double)done / total;
        var filled = (int)Math.Round(ratio * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);

        return $"[{new string('█', filled)}{new string('░', width - filled)}] {(int)Math.Round(ratio * 100)}%";
    }

    private static Color ResolveProgressColor(int done, int total)
    {
        if (total <= 0)
        {
            return Color.DarkGrey;
        }

        var ratio = (double)done / total;
        if (ratio >= 0.8)
        {
            return Color.Green;
        }

        if (ratio >= 0.45)
        {
            return Color.Gold;
        }

        return Color.Orange;
    }

    private async Task<Embed> BuildDashboardEmbedInternalAsync(
        BotDbContext db,
        int projectId,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == projectId, cancellationToken);
        var activeSprint = await db.Sprints
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive, cancellationToken);

        var activeSprintId = activeSprint?.Id;
        var sprintTasksQuery = db.TaskItems.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (activeSprintId.HasValue)
        {
            sprintTasksQuery = sprintTasksQuery.Where(x => x.SprintId == activeSprintId.Value);
        }
        else
        {
            sprintTasksQuery = sprintTasksQuery.Where(x => x.SprintId == null);
        }

        var totalTasks = await sprintTasksQuery.CountAsync(cancellationToken);
        var doneTasks = await sprintTasksQuery.CountAsync(x => x.Status == TaskItemStatus.Done, cancellationToken);
        var backlogCount = await db.TaskItems.CountAsync(
            x => x.ProjectId == projectId && x.SprintId == null && x.Type == TaskItemType.Task,
            cancellationToken);
        var bugOpenCount = await db.TaskItems.CountAsync(
            x => x.ProjectId == projectId && x.Type == TaskItemType.Bug && x.Status != TaskItemStatus.Done,
            cancellationToken);

        var sprintWindow = activeSprint?.StartDateLocal.HasValue == true && activeSprint.EndDateLocal.HasValue
            ? $"{activeSprint.StartDateLocal:yyyy-MM-dd} -> {activeSprint.EndDateLocal:yyyy-MM-dd} (UTC+7)"
            : "Chưa thiết lập";

        var activeQuestline = activeSprint is null
            ? "🛌 Chưa có sprint đang chạy\n\n> ⚠️ Lead/Admin hãy vào **Bảng Điều Phối** để bắt đầu sprint."
            : "⚔️ Sprint đang chạy\n\n" +
              $"- **Tên sprint:** `{activeSprint.Name}`\n" +
              $"- **Mục tiêu:** **{activeSprint.Goal}**\n" +
              $"- **Thời gian:** `{sprintWindow}`";

        var resourceSnapshot =
            "- **Backlog:** " + $"`{backlogCount}`\n" +
            "- **Bug đang mở:** " + $"`{bugOpenCount}`\n" +
            "- **Đã hoàn thành:** " + $"`{doneTasks}`/`{Math.Max(totalTasks, 0)}`";

        const string missionFlow =
            "- **1.** Thêm việc vào **Backlog**\n" +
            "- **2.** Lead/Admin mở **Bảng Điều Phối** và bắt đầu sprint\n" +
            "- **3.** Team nhận việc trong **Quest Board**\n" +
            "- **4.** Kết thúc sprint để chốt velocity";

        const string accessGrid =
            "- **Lead/Admin**: bắt đầu sprint, kết thúc sprint, giao nhiệm vụ\n" +
            "- **Thành viên**: nhận việc, cập nhật tiến độ, báo/sửa bug";

        var builder = new EmbedBuilder()
            .WithTitle($"🏰 Đại Sảnh Dự Án • {project.Name}")
            .WithColor(ResolveProgressColor(doneTasks, totalTasks))
            .WithDescription(
                "🎮 Tổng Quan\n" +
                $"**Chế độ:** `{(activeSprint is null ? "Nghỉ giữa chiến dịch" : "Đang hành quân")}`\n" +
                $"**Project ID:** `{project.Id}`\n" +
                "━━━━━━━━━━━━━━━━━━━━")
            .WithCurrentTimestamp()
            .AddField("🛡️ Tình Hình Sprint", activeQuestline, false)
            .AddField("📈 Tiến Độ Chiến Dịch", BuildProgressLine(totalTasks, doneTasks), false)
            .AddField("🎒 Tài Nguyên Hiện Có", resourceSnapshot, true)
            .AddField("🧭 Luồng Xử Lý", missionFlow, true)
            .AddField("👑 Phân Quyền", accessGrid + "\n\n> ⚠️ Chỉ Lead/Admin mới được Start/End sprint.", false);

        return builder.Build();
    }

    private async Task<Embed> BuildStandupSummaryEmbedInternalAsync(
        BotDbContext db,
        int projectId,
        DateTime localDate,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == projectId, cancellationToken);
        var reports = await db.StandupReports.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.LocalDate == localDate)
            .ToListAsync(cancellationToken);

        reports = reports
            .OrderBy(x => x.ReportedAtUtc)
            .ThenBy(x => x.Id)
            .ToList();

        var summaryLines = reports.Count == 0
            ? "📣 Chưa có báo cáo standup\n\n> ⚠️ Hãy bấm **Nộp Standup** trước 09:30 (UTC+7)."
            : string.Join(
                "\n\n━━━━━━━━━━━━━━━━━━━━\n\n",
                reports.Select((x, i) =>
                    $"🧙 Báo cáo #{i + 1} • <@{x.DiscordUserId}>\n" +
                    $"- **Hôm qua:** {x.Yesterday}\n" +
                    $"- **Hôm nay:** {x.Today}\n" +
                    $"- **Vướng mắc:** {x.Blockers}"));

        return new EmbedBuilder()
            .WithTitle($"🛡️ Nhật Ký Standup • {project.Name}")
            .WithColor(Color.DarkBlue)
            .AddField("📅 Ngày", $"{localDate:yyyy-MM-dd} (UTC+7)", true)
            .AddField("👥 Số báo cáo", reports.Count.ToString(), true)
            .WithDescription(summaryLines)
            .WithCurrentTimestamp()
            .Build();
    }
}





