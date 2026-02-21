using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;
using ProjectManagerBot.Services;
using System.Globalization;

namespace ProjectManagerBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class InteractionModule(
    InitialSetupService initialSetupService,
    ProjectService projectService,
    NotificationService notificationService,
    IDbContextFactory<BotDbContext> dbContextFactory,
    ILogger<InteractionModule> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;

    private readonly InitialSetupService _initialSetupService = initialSetupService;
    private readonly ProjectService _projectService = projectService;
    private readonly NotificationService _notificationService = notificationService;
    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<InteractionModule> _logger = logger;

    [SlashCommand("studio-init", "Create studio infrastructure: roles, categories, channels and permissions.")]
    public async Task StudioInitAsync()
    {
        if (Context.Guild.OwnerId != Context.User.Id)
        {
            await RespondAsync("Only the server owner can run `/studio-init`.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var setupResult = await _initialSetupService.InitializeStudioAsync(Context.Guild);

        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
        }

        var project = await _projectService.UpsertProjectAsync(
            name: "PROJECT A: [GAME NAME]",
            channelId: setupResult.P1DashboardChannelId,
            bugChannelId: setupResult.P1BugsChannelId,
            standupChannelId: setupResult.DailyStandupChannelId,
            globalNotificationChannelId: setupResult.GlobalTaskFeedChannelId);

        await _projectService.RefreshDashboardMessageAsync(project.Id);

        await FollowupAsync(
            $"Studio initialized.\n" +
            $"- Deleted channels: `{setupResult.DeletedChannelsCount}`\n" +
            "- Database: `reset (deleted + recreated)`\n" +
            $"- Dashboard: <#{setupResult.P1DashboardChannelId}>\n" +
            $"- Bugs: <#{setupResult.P1BugsChannelId}>\n" +
            $"- Daily Standup: <#{setupResult.DailyStandupChannelId}>\n" +
            $"- Global Task Feed: <#{setupResult.GlobalTaskFeedChannelId}>\n" +
            $"- Project ID: `{project.Id}`",
            ephemeral: true);
    }

    [ComponentInteraction("dashboard:add_backlog", true)]
    public async Task AddBacklogAsync()
    {
        var project = await ResolveProjectFromChannelAsync();
        if (project is null)
        {
            return;
        }

        await RespondWithModalAsync<AddBacklogModal>($"backlog:add:{project.Id}");
    }

    [ModalInteraction("backlog:add:*", true)]
    public async Task HandleAddBacklogModalAsync(string projectIdRaw, AddBacklogModal modal)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        var points = ParsePoints(modal.Points, fallback: 1);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var taskItem = new TaskItem
        {
            ProjectId = projectId,
            SprintId = null,
            Type = TaskItemType.Task,
            Status = TaskItemStatus.Backlog,
            Title = modal.TaskTitle.Trim(),
            Description = modal.Description?.Trim(),
            Points = points,
            CreatedById = Context.User.Id
        };

        db.TaskItems.Add(taskItem);

        await db.SaveChangesAsync();

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project is not null)
        {
            var backlogChannel = ResolveBacklogChannel(Context.Guild, project.ChannelId);
            if (backlogChannel is not null)
            {
                await backlogChannel.SendMessageAsync(embed: BuildBacklogItemEmbed(taskItem));
            }
            else
            {
                _logger.LogWarning("Backlog channel was not found for project {ProjectId}", projectId);
            }
        }

        await _projectService.RefreshDashboardMessageAsync(projectId);

        await RespondAsync("Backlog item added.", ephemeral: true);
    }

    [ComponentInteraction("dashboard:start_sprint", true)]
    public async Task StartSprintAsync()
    {
        var project = await ResolveProjectFromChannelAsync();
        if (project is null)
        {
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Only Studio Lead/Admin can start a sprint.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<StartSprintModal>($"sprint:start:{project.Id}");
    }

    [ComponentInteraction("dashboard:admin_panel", true)]
    public async Task OpenAdminPanelAsync()
    {
        var project = await ResolveProjectFromChannelAsync();
        if (project is null)
        {
            return;
        }

        var isAdmin = IsLeadOrAdmin();
        var components = BuildAdminPanelComponents(project.Id, isAdmin);
        var embed = new EmbedBuilder()
            .WithTitle($"👑 Bảng Điều Phối • {project.Name}")
            .WithColor(isAdmin ? Color.Gold : Color.DarkGrey)
            .WithDescription(
                isAdmin
                    ? "🔐 Quyền quản trị đã kích hoạt\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "⚔️ Chức năng\n" +
                      "- **Bắt Đầu Sprint**: tạo sprint mới\n" +
                      "- **Kết Thúc Sprint**: đóng sprint, tính velocity\n" +
                      "- **Giao Nhiệm Vụ**: chỉ định người xử lý\n\n" +
                      "> ⚠️ Việc chưa xong khi kết thúc sprint sẽ quay lại backlog."
                    : "⛔ Không đủ quyền\n\n> ⚠️ Chỉ **Studio Lead/Admin** mới được thao tác tại đây.")
            .AddField(
                "🧭 Hướng Dẫn Nhanh",
                "- Mở sprint từ đầu mỗi chu kỳ\n" +
                "- Theo dõi bug/quest trong Quest Board\n" +
                "- Chốt sprint đúng hạn để bảo toàn nhịp team",
                false)
            .Build();

        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [ComponentInteraction("admin:start_sprint:*", true)]
    public async Task StartSprintFromAdminPanelAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Only Studio Lead/Admin can start a sprint.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<StartSprintModal>($"sprint:start:{projectId}");
    }

    [ModalInteraction("sprint:start:*", true)]
    public async Task HandleStartSprintModalAsync(string projectIdRaw, StartSprintModal modal)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        var dateFormat = "yyyy-MM-dd";
        if (!DateTime.TryParseExact(
                modal.StartDate.Trim(),
                dateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startDateLocal))
        {
            await RespondAsync("Invalid start date. Use format `yyyy-MM-dd`.", ephemeral: true);
            return;
        }

        if (!DateTime.TryParseExact(
                modal.EndDate.Trim(),
                dateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var endDateLocal))
        {
            await RespondAsync("Invalid end date. Use format `yyyy-MM-dd`.", ephemeral: true);
            return;
        }

        startDateLocal = DateTime.SpecifyKind(startDateLocal.Date, DateTimeKind.Unspecified);
        endDateLocal = DateTime.SpecifyKind(endDateLocal.Date, DateTimeKind.Unspecified);

        if (endDateLocal < startDateLocal)
        {
            await RespondAsync("End date must be on or after start date.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var hasActiveSprint = await db.Sprints.AnyAsync(x => x.ProjectId == projectId && x.IsActive);
        if (hasActiveSprint)
        {
            await RespondAsync("An active sprint already exists. End it before starting a new one.", ephemeral: true);
            return;
        }

        var sprint = new Sprint
        {
            ProjectId = projectId,
            Name = modal.Name.Trim(),
            Goal = modal.Goal.Trim(),
            IsActive = true,
            StartDateLocal = startDateLocal,
            EndDateLocal = endDateLocal
        };

        db.Sprints.Add(sprint);
        await db.SaveChangesAsync();
        await _notificationService.NotifySprintStartedAsync(projectId, Context.User.Id, sprint);

        var backlogTasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId && x.SprintId == null && x.Type == TaskItemType.Task)
            .OrderBy(x => x.Id)
            .Take(25)
            .ToListAsync();

        if (backlogTasks.Count == 0)
        {
            await _projectService.RefreshDashboardMessageAsync(projectId);
            await RespondAsync("Sprint created with no backlog tasks assigned yet.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"sprint:select_tasks:{sprint.Id}")
            .WithPlaceholder("Choose quests to deploy into this sprint")
            .WithMinValues(1)
            .WithMaxValues(backlogTasks.Count);

        foreach (var task in backlogTasks)
        {
            menu.AddOption(new SelectMenuOptionBuilder(
                label: Truncate(task.Title, 90),
                value: task.Id.ToString(),
                description: $"Points: {task.Points}"));
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await _projectService.RefreshDashboardMessageAsync(projectId);

        await RespondTransientAsync(
            $"Sprint `{sprint.Name}` activated. Choose quests to include:",
            components: components);
    }

    [ComponentInteraction("sprint:select_tasks:*", true)]
    public async Task HandleSprintTaskSelectionAsync(string sprintIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(sprintIdRaw, out var sprintId))
        {
            await RespondAsync("Invalid sprint context.", ephemeral: true);
            return;
        }

        var taskIds = selectedTaskIds
            .Select(x => int.TryParse(x, out var parsed) ? parsed : (int?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.Id == sprintId);
        if (sprint is null)
        {
            await RespondAsync("Sprint not found.", ephemeral: true);
            return;
        }

        var tasks = await db.TaskItems
            .Where(x => x.ProjectId == sprint.ProjectId && x.SprintId == null && taskIds.Contains(x.Id))
            .ToListAsync();

        foreach (var task in tasks)
        {
            task.SprintId = sprint.Id;
            task.Status = TaskItemStatus.Todo;
        }

        await db.SaveChangesAsync();
        await _projectService.RefreshDashboardMessageAsync(sprint.ProjectId);

        await RespondTransientAsync($"Added {tasks.Count} quest(s) to sprint `{sprint.Name}`.");
    }

    [ComponentInteraction("dashboard:my_tasks", true)]
    public async Task MyTasksAsync()
    {
        var project = await ResolveProjectFromChannelAsync();
        if (project is null)
        {
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var activeSprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.IsActive);
        if (activeSprint is null)
        {
            await RespondAsync("No active sprint in this project.", ephemeral: true);
            return;
        }

        var myTasks = await db.TaskItems
            .Where(x => x.ProjectId == project.Id && x.SprintId == activeSprint.Id && x.AssigneeId == Context.User.Id)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.Id)
            .ToListAsync();

        var description = myTasks.Count == 0
            ? "💤 Chưa có nhiệm vụ đang xử lý\n\n- Mở **Quest Board** để nhận việc.\n\n> ⚠️ Nhận task tại Quest Board, không nhận ở My Tasks."
            : string.Join(
                "\n\n",
                myTasks.Select(x =>
                    $"📜 Quest #{x.Id}\n" +
                    $"- **Trạng thái:** {GetStatusBadge(x.Status)}\n" +
                    $"- **Tiêu đề:** **{x.Title}**\n" +
                    $"- **Điểm:** `{x.Points}`"));

        var embed = new EmbedBuilder()
            .WithTitle($"🧙 Sổ Nhiệm Vụ Cá Nhân • {project.Name}")
            .WithDescription(description)
            .WithColor(Color.DarkPurple)
            .AddField("👤 Người Chơi", Context.User.Mention, true)
            .AddField("🎯 Số Quest", myTasks.Count.ToString(), true)
            .AddField("⚔️ Sprint", activeSprint.Name, true)
            .Build();

        var components = new ComponentBuilder()
            .WithButton("✅ Đánh Dấu Xong", $"board:done:{project.Id}", ButtonStyle.Success)
            .WithButton("🗺️ Mở Quest Board", $"board:refresh:{project.Id}", ButtonStyle.Secondary)
            .Build();

        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [ComponentInteraction("dashboard:view_board", true)]
    public async Task ViewBoardAsync()
    {
        var project = await ResolveProjectFromChannelAsync();
        if (project is null)
        {
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var activeSprint = await db.Sprints
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.IsActive);

        var sprintId = activeSprint?.Id;
        var todo = await db.TaskItems.CountAsync(
            x => x.ProjectId == project.Id &&
                 x.SprintId == sprintId &&
                 x.Type == TaskItemType.Task &&
                 x.Status == TaskItemStatus.Todo);
        var inProgress = await db.TaskItems.CountAsync(
            x => x.ProjectId == project.Id &&
                 x.SprintId == sprintId &&
                 x.Type == TaskItemType.Task &&
                 x.Status == TaskItemStatus.InProgress);
        var done = await db.TaskItems.CountAsync(
            x => x.ProjectId == project.Id &&
                 x.SprintId == sprintId &&
                 x.Type == TaskItemType.Task &&
                 x.Status == TaskItemStatus.Done);
        var backlog = await db.TaskItems.CountAsync(
            x => x.ProjectId == project.Id &&
                 x.SprintId == null &&
                 x.Type == TaskItemType.Task);
        var openBugs = await db.TaskItems.CountAsync(
            x => x.ProjectId == project.Id &&
                 x.Type == TaskItemType.Bug &&
                 x.Status != TaskItemStatus.Done);

        var claimableTasks = await db.TaskItems
            .AsNoTracking()
            .Where(x => x.ProjectId == project.Id &&
                        x.SprintId == sprintId &&
                        x.Type == TaskItemType.Task &&
                        x.Status == TaskItemStatus.Todo &&
                        x.AssigneeId == null)
            .OrderBy(x => x.Id)
            .Take(8)
            .ToListAsync();

        var myFocusTasks = await db.TaskItems
            .AsNoTracking()
            .Where(x => x.ProjectId == project.Id &&
                        x.SprintId == sprintId &&
                        x.Type == TaskItemType.Task &&
                        x.AssigneeId == Context.User.Id &&
                        x.Status != TaskItemStatus.Done)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.Id)
            .Take(8)
            .ToListAsync();

        var totalSprintTasks = todo + inProgress + done;
        var progressBar = BuildVisualProgressBar(done, totalSprintTasks);
        var completionText = totalSprintTasks == 0
            ? "`0/0` completed"
            : $"`{done}/{totalSprintTasks}` completed (`{(int)Math.Round((double)done / totalSprintTasks * 100)}%`)";

        var laneSummary = string.Join(
            "\n",
            [
                $"- 📜 **Backlog:** `{backlog}`",
                $"- 🪓 **Todo:** `{todo}`",
                $"- ⚔️ **Đang làm:** `{inProgress}`",
                $"- 🏆 **Done:** `{done}`",
                $"- 🐞 **Bug mở:** `{openBugs}`"
            ]);

        var queueText = claimableTasks.Count == 0
            ? "💤 Chưa có quest trong hàng chờ."
            : string.Join("\n\n", claimableTasks.Select(x =>
                $"📌 Quest #{x.Id}\n" +
                $"- **Tên:** **{Truncate(x.Title, 70)}**\n" +
                $"- **Điểm:** `{x.Points}`"));

        var myFocusText = myFocusTasks.Count == 0
            ? "🍃 Bạn chưa có quest đang xử lý."
            : string.Join("\n\n", myFocusTasks.Select(x =>
                $"🛡️ Quest #{x.Id}\n" +
                $"- **Trạng thái:** {GetStatusBadge(x.Status)}\n" +
                $"- **Tên:** **{Truncate(x.Title, 65)}**"));

        var embedColor = activeSprint is null
            ? Color.DarkGrey
            : done >= Math.Max(1, totalSprintTasks / 2) ? Color.DarkGreen : Color.DarkOrange;

        var stateText = activeSprint is null
            ? "> ⚠️ Chưa có sprint đang chạy. Lead/Admin hãy bắt đầu sprint."
            : $"- **Sprint:** **{activeSprint.Name}**";

        var embed = new EmbedBuilder()
            .WithTitle($"🗺️ Bảng Nhiệm Vụ Vương Quốc • {project.Name}")
            .WithColor(embedColor)
            .WithDescription(
                "🔥 Trạng Thái Chiến Dịch\n" +
                $"**Mode:** `{(activeSprint is null ? "Nghỉ giữa chiến dịch" : "Đang giao tranh")}`\n" +
                $"{stateText}\n" +
                "━━━━━━━━━━━━━━━━━━━━")
            .AddField("📈 Tiến Độ", $"{progressBar}\n{completionText}", false)
            .AddField("🧩 Bản Đồ Trạng Thái", laneSummary, true)
            .AddField("🎯 Hàng Chờ Nhận Việc", queueText, true)
            .AddField("🧙 Việc Của Tôi", myFocusText, false)
            .WithFooter("Nhận quest tại đây để tránh trùng lặp và để theo dõi tiến độ toàn đội.")
            .WithCurrentTimestamp()
            .Build();

        var components = new ComponentBuilder()
            .WithButton(
                ButtonBuilder.CreateSuccessButton("⚔️ Nhận Quest", $"board:claim:{project.Id}")
                    .WithDisabled(activeSprint is null || claimableTasks.Count == 0))
            .WithButton(
                ButtonBuilder.CreatePrimaryButton("✅ Đánh Dấu Xong", $"board:done:{project.Id}")
                    .WithDisabled(activeSprint is null))
            .WithButton(
                ButtonBuilder.CreateSecondaryButton("🔄 Làm Mới", $"board:refresh:{project.Id}"))
            .Build();

        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [ComponentInteraction("board:refresh:*", true)]
    public async Task RefreshBoardAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out _))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        await ViewBoardAsync();
    }

    [ComponentInteraction("board:claim:*", true)]
    public async Task OpenBoardClaimPickerAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var activeSprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive);
        if (activeSprint is null)
        {
            await RespondAsync("No active sprint.", ephemeral: true);
            return;
        }

        var tasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId &&
                        x.SprintId == activeSprint.Id &&
                        x.Type == TaskItemType.Task &&
                        x.Status == TaskItemStatus.Todo &&
                        x.AssigneeId == null)
            .OrderBy(x => x.Id)
            .Take(25)
            .ToListAsync();

        if (tasks.Count == 0)
        {
            await RespondTransientAsync("Không có quest chưa giao để nhận.");
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"board:claim_select:{projectId}")
            .WithPlaceholder("Chọn quest để nhận")
            .WithMinValues(1)
            .WithMaxValues(tasks.Count);

        foreach (var task in tasks)
        {
            menu.AddOption(Truncate(task.Title, 90), task.Id.ToString(), $"#{task.Id} - {task.Points}pt");
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondTransientAsync("⚔️ Chọn quest cần nhận", components: components);
    }

    [ComponentInteraction("board:claim_select:*", true)]
    public async Task ClaimBoardTasksAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        var taskIds = ParseSelectedTaskIds(selectedTaskIds);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var tasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId &&
                        taskIds.Contains(x.Id) &&
                        x.Type == TaskItemType.Task &&
                        x.Status == TaskItemStatus.Todo &&
                        x.AssigneeId == null)
            .ToListAsync();

        foreach (var task in tasks)
        {
            task.AssigneeId = Context.User.Id;
            task.Status = TaskItemStatus.InProgress;
        }

        await db.SaveChangesAsync();
        await _projectService.RefreshDashboardMessageAsync(projectId);
        await _notificationService.NotifyTaskClaimedAsync(projectId, Context.User.Id, tasks);
        await RespondTransientAsync($"Claimed {tasks.Count} quest(s).");
    }

    [ComponentInteraction("board:done:*", true)]
    public async Task OpenBoardDonePickerAsync(string projectIdRaw)
    {
        await CompleteTaskSelectionAsync(projectIdRaw);
    }

    [ComponentInteraction("tasks:start:*", true)]
    public async Task StartTaskSelectionAsync(string projectIdRaw)
    {
        await RespondTransientAsync(
            "Nhận task đã chuyển sang **Quest Board**. Dùng `Quest Board -> Nhận Quest`.",
            ephemeral: true);
    }

    [ComponentInteraction("tasks:done:*", true)]
    public async Task CompleteTaskSelectionAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var tasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId &&
                        x.Status == TaskItemStatus.InProgress &&
                        x.AssigneeId == Context.User.Id)
            .OrderBy(x => x.Id)
            .Take(25)
            .ToListAsync();

        if (tasks.Count == 0)
        {
            await RespondAsync("Bạn chưa có task đang xử lý.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"tasks:done_select:{projectId}")
            .WithPlaceholder("Chọn quest để đánh dấu hoàn thành")
            .WithMinValues(1)
            .WithMaxValues(tasks.Count);

        foreach (var task in tasks)
        {
            menu.AddOption(Truncate(task.Title, 90), task.Id.ToString(), $"#{task.Id} - {task.Points}pt");
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondTransientAsync("✅ Chọn quest để đánh dấu hoàn thành", components: components);
    }

    [ComponentInteraction("tasks:start_select:*", true)]
    public async Task MarkTaskInProgressAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        var taskIds = ParseSelectedTaskIds(selectedTaskIds);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var tasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId &&
                        taskIds.Contains(x.Id) &&
                        x.Status == TaskItemStatus.Todo &&
                        (x.AssigneeId == null || x.AssigneeId == Context.User.Id))
            .ToListAsync();

        foreach (var task in tasks)
        {
            task.AssigneeId = Context.User.Id;
            task.Status = TaskItemStatus.InProgress;
        }

        await db.SaveChangesAsync();
        await _projectService.RefreshDashboardMessageAsync(projectId);

        await RespondTransientAsync($"Moved {tasks.Count} quest(s) to In Progress.");
    }

    [ComponentInteraction("tasks:done_select:*", true)]
    public async Task MarkTaskDoneAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        var taskIds = ParseSelectedTaskIds(selectedTaskIds);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var tasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId &&
                        taskIds.Contains(x.Id) &&
                        x.Status == TaskItemStatus.InProgress &&
                        x.AssigneeId == Context.User.Id)
            .ToListAsync();

        foreach (var task in tasks)
        {
            task.Status = TaskItemStatus.Done;
        }

        await db.SaveChangesAsync();

        var xp = tasks.Sum(x => Math.Max(10, x.Points * 10));
        var totalXp = await _projectService.AwardXpAsync(Context.User.Id, xp);
        await _projectService.RefreshDashboardMessageAsync(projectId);
        await _notificationService.NotifyTaskCompletedAsync(projectId, Context.User.Id, tasks, xp);

        await RespondTransientAsync($"Marked {tasks.Count} quest(s) completed. XP +{xp} (Total: {totalXp}).");
    }

    [ComponentInteraction("dashboard:report_bug", true)]
    public async Task ReportBugAsync()
    {
        var project = await ResolveProjectFromChannelAsync();
        if (project is null)
        {
            return;
        }

        await RespondWithModalAsync<ReportBugModal>($"bug:report:{project.Id}");
    }

    [ModalInteraction("bug:report:*", true)]
    public async Task HandleReportBugModalAsync(string projectIdRaw, ReportBugModal modal)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (project is null)
        {
            await RespondAsync("Project not found.", ephemeral: true);
            return;
        }

        var points = ParsePoints(modal.Points, fallback: 3);
        var bugTask = new TaskItem
        {
            ProjectId = projectId,
            SprintId = null,
            Type = TaskItemType.Bug,
            Status = TaskItemStatus.Todo,
            Title = modal.BugTitle.Trim(),
            Description = modal.Description.Trim(),
            Points = points,
            CreatedById = Context.User.Id
        };

        db.TaskItems.Add(bugTask);
        await db.SaveChangesAsync();

        var bugChannel = Context.Guild.GetTextChannel(project.BugChannelId);
        if (bugChannel is null)
        {
            await RespondAsync("Bug channel is not accessible.", ephemeral: true);
            return;
        }

        var bugEmbed = new EmbedBuilder()
            .WithTitle($"🐉 Quái Lỗi Xuất Hiện • #{bugTask.Id}")
            .WithDescription(
                "🚨 Bug Alert\n" +
                $"📌 {bugTask.Title}\n\n" +
                $"{(bugTask.Description ?? "Không có mô tả")}\n\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "> ⚠️ Mọi người có thể bấm **Nhận Bug** để xử lý.")
            .WithColor(Color.Red)
            .AddField("👤 Reporter", Context.User.Mention, true)
            .AddField("🎯 Điểm", bugTask.Points.ToString(), true)
            .AddField("🧭 Trạng Thái", bugTask.Status.ToString(), true)
            .WithCurrentTimestamp()
            .Build();

        var components = new ComponentBuilder()
            .WithButton("🛡️ Nhận Bug", $"bug:claim:{bugTask.Id}", ButtonStyle.Primary)
            .WithButton("✅ Đã Sửa", $"bug:fixed:{bugTask.Id}", ButtonStyle.Success)
            .Build();

        await bugChannel.SendMessageAsync(embed: bugEmbed, components: components);
        await _projectService.RefreshDashboardMessageAsync(projectId);
        await RespondAsync($"Bug reported in <#{project.BugChannelId}>.", ephemeral: true);
    }

    [ComponentInteraction("bug:claim:*", true)]
    public async Task ClaimBugAsync(string bugTaskIdRaw)
    {
        if (!int.TryParse(bugTaskIdRaw, out var bugTaskId))
        {
            await RespondAsync("Invalid bug context.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var bug = await db.TaskItems.FirstOrDefaultAsync(x => x.Id == bugTaskId && x.Type == TaskItemType.Bug);
        if (bug is null)
        {
            await RespondAsync("Bug not found.", ephemeral: true);
            return;
        }

        if (bug.AssigneeId.HasValue && bug.AssigneeId != Context.User.Id)
        {
            await RespondAsync($"This bug is already claimed by <@{bug.AssigneeId}>.", ephemeral: true);
            return;
        }

        bug.AssigneeId = Context.User.Id;
        bug.Status = TaskItemStatus.InProgress;
        await db.SaveChangesAsync();

        var embed = BuildBugStateEmbed(bug, "In Progress", Color.Orange, Context.User.Id);
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.Message.ModifyAsync(props => props.Embed = embed);
        }

        await _projectService.RefreshDashboardMessageAsync(bug.ProjectId);
        await RespondAsync("Bug claimed.", ephemeral: true);
    }

    [ComponentInteraction("bug:fixed:*", true)]
    public async Task FixBugAsync(string bugTaskIdRaw)
    {
        if (!int.TryParse(bugTaskIdRaw, out var bugTaskId))
        {
            await RespondAsync("Invalid bug context.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var bug = await db.TaskItems.FirstOrDefaultAsync(x => x.Id == bugTaskId && x.Type == TaskItemType.Bug);
        if (bug is null)
        {
            await RespondAsync("Bug not found.", ephemeral: true);
            return;
        }

        if (bug.AssigneeId.HasValue && bug.AssigneeId != Context.User.Id && !IsLeadOrAdmin())
        {
            await RespondAsync($"Only assignee <@{bug.AssigneeId}> or Lead/Admin can close this bug.", ephemeral: true);
            return;
        }

        bug.AssigneeId = Context.User.Id;
        bug.Status = TaskItemStatus.Done;
        await db.SaveChangesAsync();

        var xpAward = Math.Max(20, bug.Points * 5);
        var totalXp = await _projectService.AwardXpAsync(Context.User.Id, xpAward);

        var embed = BuildBugStateEmbed(bug, "Fixed", Color.Green, Context.User.Id);
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.Message.ModifyAsync(props => props.Embed = embed);
        }

        await _projectService.RefreshDashboardMessageAsync(bug.ProjectId);
        await RespondAsync($"Bug marked fixed. XP +{xpAward} (Total: {totalXp}).", ephemeral: true);
    }

    [ComponentInteraction("dashboard:end_sprint", true)]
    public async Task EndSprintAsync()
    {
        var project = await ResolveProjectFromChannelAsync();
        if (project is null)
        {
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Only Studio Lead/Admin can end a sprint.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.IsActive);
        if (sprint is null)
        {
            await RespondAsync("No active sprint to end.", ephemeral: true);
            return;
        }

        var sprintTasks = await db.TaskItems
            .Where(x => x.ProjectId == project.Id && x.SprintId == sprint.Id)
            .ToListAsync();

        var doneTasks = sprintTasks.Where(x => x.Status == TaskItemStatus.Done).ToList();
        var unfinishedTasks = sprintTasks.Where(x => x.Status != TaskItemStatus.Done).ToList();

        var velocity = doneTasks.Sum(x => x.Points);
        foreach (var task in unfinishedTasks)
        {
            task.SprintId = null;
            task.Status = TaskItemStatus.Backlog;
            task.AssigneeId = null;
        }

        sprint.IsActive = false;
        sprint.EndedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        await _projectService.RefreshDashboardMessageAsync(project.Id);
        await _notificationService.NotifySprintEndedAsync(
            project.Id,
            Context.User.Id,
            sprint,
            velocity,
            doneTasks.Count,
            unfinishedTasks.Count);

        await RespondAsync(
            $"Sprint ended.\n" +
            $"- Velocity: `{velocity}` points\n" +
            $"- Completed: `{doneTasks.Count}`\n" +
            $"- Returned to backlog: `{unfinishedTasks.Count}`",
            ephemeral: true);
    }

    [ComponentInteraction("admin:end_sprint:*", true)]
    public async Task EndSprintFromAdminPanelAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Only Studio Lead/Admin can end a sprint.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive);
        if (sprint is null)
        {
            await RespondAsync("No active sprint to end.", ephemeral: true);
            return;
        }

        var sprintTasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId && x.SprintId == sprint.Id)
            .ToListAsync();

        var doneTasks = sprintTasks.Where(x => x.Status == TaskItemStatus.Done).ToList();
        var unfinishedTasks = sprintTasks.Where(x => x.Status != TaskItemStatus.Done).ToList();

        var velocity = doneTasks.Sum(x => x.Points);
        foreach (var task in unfinishedTasks)
        {
            task.SprintId = null;
            task.Status = TaskItemStatus.Backlog;
            task.AssigneeId = null;
        }

        sprint.IsActive = false;
        sprint.EndedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        await _projectService.RefreshDashboardMessageAsync(projectId);
        await _notificationService.NotifySprintEndedAsync(
            projectId,
            Context.User.Id,
            sprint,
            velocity,
            doneTasks.Count,
            unfinishedTasks.Count);

        await RespondAsync(
            $"Sprint ended.\n" +
            $"- Velocity: `{velocity}` points\n" +
            $"- Completed: `{doneTasks.Count}`\n" +
            $"- Returned to backlog: `{unfinishedTasks.Count}`",
            ephemeral: true);
    }

    [ComponentInteraction("admin:assign_task:*", true)]
    public async Task OpenAssignTaskPickerAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Studio Lead/Admin mới được giao task trong sprint.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var activeSprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive);
        if (activeSprint is null)
        {
            await RespondAsync("Chưa có sprint đang chạy. Hãy bắt đầu sprint trước.", ephemeral: true);
            return;
        }

        var assignableTasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId &&
                        x.SprintId == activeSprint.Id &&
                        x.Type == TaskItemType.Task &&
                        (x.Status == TaskItemStatus.Todo || x.Status == TaskItemStatus.InProgress))
            .OrderBy(x => x.Status)
            .ThenBy(x => x.Id)
            .Take(25)
            .ToListAsync();

        if (assignableTasks.Count == 0)
        {
            await RespondAsync("Không có task nào có thể giao trong sprint hiện tại.", ephemeral: true);
            return;
        }

        var taskMenu = new SelectMenuBuilder()
            .WithCustomId($"admin:assign_pick_task:{projectId}")
            .WithPlaceholder("Chọn quest cần giao")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var task in assignableTasks)
        {
            var assigneeText = task.AssigneeId.HasValue ? $"Assignee <@{task.AssigneeId.Value}>" : "Unassigned";
            taskMenu.AddOption(
                label: $"#{task.Id} {Truncate(task.Title, 70)}",
                value: task.Id.ToString(),
                description: $"{task.Status} - {assigneeText}");
        }

        var components = new ComponentBuilder().WithSelectMenu(taskMenu).Build();
        await RespondTransientAsync("🎯 Chon quest trong sprint de giao", components: components);
    }

    [ComponentInteraction("admin:assign_pick_task:*", true)]
    public async Task OpenAssignUserPickerAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Studio Lead/Admin mới được giao task trong sprint.", ephemeral: true);
            return;
        }

        var selected = selectedTaskIds.FirstOrDefault();
        if (!int.TryParse(selected, out var taskId))
        {
            await RespondAsync("Invalid task selection.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var task = await db.TaskItems.FirstOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId && x.Type == TaskItemType.Task);
        if (task is null)
        {
            await RespondAsync("Task not found.", ephemeral: true);
            return;
        }

        var userMenu = new SelectMenuBuilder()
            .WithCustomId($"admin:assign_pick_user:{projectId}:{task.Id}")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder("Chọn thành viên được giao")
            .WithMinValues(1)
            .WithMaxValues(1);

        var components = new ComponentBuilder().WithSelectMenu(userMenu).Build();
        await RespondTransientAsync(
            $"👥 Giao quest `#{task.Id} {Truncate(task.Title, 60)}` cho:",
            components: components);
    }

    [ComponentInteraction("admin:assign_pick_user:*:*", true)]
    public async Task AssignTaskToUserAsync(string projectIdRaw, string taskIdRaw, string[] selectedUserIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId) || !int.TryParse(taskIdRaw, out var taskId))
        {
            await RespondAsync("Invalid assign context.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Studio Lead/Admin mới được giao task trong sprint.", ephemeral: true);
            return;
        }

        var selectedUser = selectedUserIds.FirstOrDefault();
        if (!ulong.TryParse(selectedUser, out var assigneeId))
        {
            await RespondAsync("Invalid assignee selection.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var task = await db.TaskItems.FirstOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId && x.Type == TaskItemType.Task);
        if (task is null)
        {
            await RespondAsync("Task not found.", ephemeral: true);
            return;
        }

        task.AssigneeId = assigneeId;
        if (task.Status == TaskItemStatus.Backlog)
        {
            task.Status = TaskItemStatus.Todo;
        }

        await db.SaveChangesAsync();
        await _projectService.RefreshDashboardMessageAsync(projectId);
        await _notificationService.NotifyTaskAssignedAsync(projectId, Context.User.Id, assigneeId, task);

        await RespondTransientAsync($"Assigned quest `#{task.Id}` to <@{assigneeId}>.");
    }

    [ComponentInteraction("standup:report:*", true)]
    public async Task OpenStandupReportModalAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<StandupReportModal>($"standup:submit:{projectId}");
    }

    [ModalInteraction("standup:submit:*", true)]
    public async Task SubmitStandupReportAsync(string projectIdRaw, StandupReportModal modal)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Invalid project context.", ephemeral: true);
            return;
        }

        await _projectService.SaveStandupReportAsync(
            projectId: projectId,
            discordUserId: Context.User.Id,
            yesterday: modal.Yesterday.Trim(),
            today: modal.Today.Trim(),
            blockers: modal.Blockers.Trim());

        await _projectService.RefreshStandupSummaryAsync(projectId);
        await RespondAsync("Standup report submitted.", ephemeral: true);
    }

    private async Task RespondAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = false)
    {
        await base.RespondAsync(
            text: text,
            embed: embed,
            components: components,
            ephemeral: ephemeral);

        if (ephemeral)
        {
            _ = DeleteOriginalResponseAfterDelayAsync(TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
        }
    }

    private async Task FollowupAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = false)
    {
        var message = await base.FollowupAsync(
            text: text,
            embed: embed,
            components: components,
            ephemeral: ephemeral);

        if (ephemeral)
        {
            _ = DeleteFollowupAfterDelayAsync(message, TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
        }
    }

    private async Task RespondTransientAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = true,
        int deleteAfterSeconds = EphemeralAutoDeleteSeconds)
    {
        await base.RespondAsync(text: text, embed: embed, components: components, ephemeral: ephemeral);
        if (ephemeral)
        {
            _ = DeleteOriginalResponseAfterDelayAsync(TimeSpan.FromSeconds(deleteAfterSeconds));
        }
    }

    private async Task DeleteOriginalResponseAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await Context.Interaction.DeleteOriginalResponseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not auto-delete interaction response");
        }
    }

    private async Task DeleteFollowupAfterDelayAsync(IUserMessage message, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not auto-delete followup interaction response");
        }
    }

    private async Task<Project?> ResolveProjectFromChannelAsync()
    {
        var project = await _projectService.GetProjectByChannelAsync(Context.Channel.Id);
        if (project is not null)
        {
            return project;
        }

        await RespondAsync(
            "This channel is not bound to any project. Use `/project setup <name>` first.",
            ephemeral: true);

        return null;
    }

    private bool IsLeadOrAdmin()
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            return false;
        }

        if (guildUser.GuildPermissions.Administrator || guildUser.GuildPermissions.ManageGuild)
        {
            return true;
        }

        return guildUser.Roles.Any(x => x.Name.Equals("Studio Lead", StringComparison.OrdinalIgnoreCase));
    }

    private static int ParsePoints(string raw, int fallback)
    {
        if (!int.TryParse(raw, out var points))
        {
            return fallback;
        }

        return Math.Clamp(points, 1, 100);
    }

    private static IReadOnlyCollection<int> ParseSelectedTaskIds(IEnumerable<string> selected)
    {
        return selected
            .Select(x => int.TryParse(x, out var parsed) ? parsed : (int?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();
    }

    private static string Truncate(string input, int maxLength)
    {
        return input.Length <= maxLength ? input : input[..maxLength];
    }

    private static string GetStatusBadge(TaskItemStatus status)
    {
        return status switch
        {
            TaskItemStatus.Backlog => "📜 Backlog",
            TaskItemStatus.Todo => "🪓 Todo",
            TaskItemStatus.InProgress => "⚔️ Đang Làm",
            TaskItemStatus.Done => "🏆 Hoàn Thành",
            _ => "❓ Unknown"
        };
    }

    private static string BuildVisualProgressBar(int done, int total, int width = 14)
    {
        if (total <= 0)
        {
            return $"[{new string('.', width)}] 0%";
        }

        var ratio = (double)done / total;
        var filled = (int)Math.Round(ratio * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);

        return $"[{new string('=', filled)}{new string('.', width - filled)}] {(int)Math.Round(ratio * 100)}%";
    }

    private static MessageComponent BuildAdminPanelComponents(int projectId, bool isAdmin)
    {
        var disable = !isAdmin;
        return new ComponentBuilder()
            .WithButton(
                ButtonBuilder.CreateSuccessButton("🔥 Bắt Đầu Sprint", $"admin:start_sprint:{projectId}")
                    .WithDisabled(disable))
            .WithButton(
                ButtonBuilder.CreateDangerButton("🏁 Kết Thúc Sprint", $"admin:end_sprint:{projectId}")
                    .WithDisabled(disable))
            .WithButton(
                ButtonBuilder.CreatePrimaryButton("🎯 Giao Nhiệm Vụ", $"admin:assign_task:{projectId}")
                    .WithDisabled(disable))
            .Build();
    }

    private Embed BuildBacklogItemEmbed(TaskItem taskItem)
    {
        return new EmbedBuilder()
            .WithTitle($"📜 Backlog Card • #{taskItem.Id}")
            .WithDescription(
                "🧾 Mô Tả Nhiệm Vụ\n" +
                $"{(string.IsNullOrWhiteSpace(taskItem.Description) ? "Chưa có mô tả." : taskItem.Description)}\n\n" +
                "━━━━━━━━━━━━━━━━━━━━")
            .WithColor(Color.Gold)
            .AddField("🗡️ Tiêu Đề", taskItem.Title, false)
            .AddField("🧭 Trạng Thái", GetStatusBadge(taskItem.Status), true)
            .AddField("🎯 Điểm", taskItem.Points.ToString(), true)
            .AddField("👤 Tạo Bởi", Context.User.Mention, true)
            .WithCurrentTimestamp()
            .Build();
    }

    private static SocketTextChannel? ResolveBacklogChannel(SocketGuild guild, ulong dashboardChannelId)
    {
        var dashboardChannel = guild.GetTextChannel(dashboardChannelId);
        var preferredName = GetBacklogNameFromDashboard(dashboardChannel?.Name);

        if (dashboardChannel?.CategoryId is ulong categoryId)
        {
            var categoryChannels = guild.TextChannels.Where(x => x.CategoryId == categoryId).ToList();

            var fromCategory = FindBacklogChannelByPriority(categoryChannels, preferredName);
            if (fromCategory is not null)
            {
                return fromCategory;
            }
        }

        return FindBacklogChannelByPriority(guild.TextChannels, preferredName);
    }

    private static string? GetBacklogNameFromDashboard(string? dashboardName)
    {
        if (string.IsNullOrWhiteSpace(dashboardName))
        {
            return null;
        }

        const string dashboardSuffix = "-dashboard";
        if (!dashboardName.EndsWith(dashboardSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var prefix = dashboardName[..^dashboardSuffix.Length];
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return null;
        }

        return $"{prefix}-backlog";
    }

    private static SocketTextChannel? FindBacklogChannelByPriority(IEnumerable<SocketTextChannel> channels, string? preferredName)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var preferred = channels.FirstOrDefault(x => x.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return channels.FirstOrDefault(x => x.Name.Contains("backlog", StringComparison.OrdinalIgnoreCase));
    }

    private static Embed BuildBugStateEmbed(TaskItem bug, string status, Color color, ulong actorUserId)
    {
        return new EmbedBuilder()
            .WithTitle($"🐲 Bug Tracker • #{bug.Id}")
            .WithDescription(
                "🧨 Chi Tiết Lỗi\n" +
                $"{(bug.Description ?? "Không có mô tả")}\n\n" +
                "━━━━━━━━━━━━━━━━━━━━")
            .WithColor(color)
            .AddField("📌 Tiêu Đề", bug.Title, false)
            .AddField("🛡️ Người Xử Lý", bug.AssigneeId.HasValue ? $"<@{bug.AssigneeId.Value}>" : "Chưa có", true)
            .AddField("✍️ Cập Nhật Bởi", $"<@{actorUserId}>", true)
            .AddField("🧭 Trạng Thái", status, true)
            .AddField("🎯 Điểm", bug.Points.ToString(), true)
            .WithCurrentTimestamp()
            .Build();
    }
}

[Group("project", "Project commands")]
[RequireContext(ContextType.Guild)]
public sealed class ProjectCommandModule(ProjectService projectService) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;

    private readonly ProjectService _projectService = projectService;

    [SlashCommand("setup", "Bind this channel to a project and initialize dashboard state.")]
    public async Task SetupProjectAsync(string name)
    {
        await DeferAsync(ephemeral: true);

        var bugChannel = Context.Guild.TextChannels
            .FirstOrDefault(x => x.Name.Contains("bugs", StringComparison.OrdinalIgnoreCase));
        var standupChannel = Context.Guild.TextChannels
            .FirstOrDefault(x => x.Name.Equals("daily-standup", StringComparison.OrdinalIgnoreCase));
        var globalTaskFeed = Context.Guild.TextChannels
            .FirstOrDefault(x => x.Name.Equals("global-task-feed", StringComparison.OrdinalIgnoreCase));

        var project = await _projectService.UpsertProjectAsync(
            name: name.Trim(),
            channelId: Context.Channel.Id,
            bugChannelId: bugChannel?.Id ?? Context.Channel.Id,
            standupChannelId: standupChannel?.Id ?? Context.Channel.Id,
            globalNotificationChannelId: globalTaskFeed?.Id);

        await _projectService.RefreshDashboardMessageAsync(project.Id);

        await FollowupAsync(
            $"Project bound successfully.\n" +
            $"- Project ID: `{project.Id}`\n" +
            $"- Dashboard Channel: <#{project.ChannelId}>\n" +
            $"- Bug Channel: <#{project.BugChannelId}>\n" +
            $"- Standup Channel: <#{project.StandupChannelId}>\n" +
            $"- Global Task Feed: {(project.GlobalNotificationChannelId.HasValue ? $"<#{project.GlobalNotificationChannelId.Value}>" : "`(not configured)`")}",
            ephemeral: true);
    }

    private async Task FollowupAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = false)
    {
        var message = await base.FollowupAsync(
            text: text,
            embed: embed,
            components: components,
            ephemeral: ephemeral);

        if (ephemeral)
        {
            _ = DeleteFollowupAfterDelayAsync(message, TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
        }
    }

    private async Task DeleteFollowupAfterDelayAsync(IUserMessage message, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await message.DeleteAsync();
        }
        catch
        {
        }
    }
}

[Group("test", "Testing utilities for reminders")]
[RequireContext(ContextType.Guild)]
public sealed class TestCommandModule(
    ProjectService projectService,
    NotificationService notificationService,
    IDbContextFactory<BotDbContext> dbContextFactory) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;

    private readonly ProjectService _projectService = projectService;
    private readonly NotificationService _notificationService = notificationService;
    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;

    [SlashCommand("standup-reminder", "Force send standup reminder message for testing.")]
    public async Task TestStandupReminderAsync(int? projectId = null)
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Only Studio Lead/Admin can run test reminders.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var project = await ResolveProjectAsync(projectId);
        if (project is null)
        {
            await FollowupAsync("Project context not found.", ephemeral: true);
            return;
        }

        var result = await _projectService.OpenDailyStandupAsync(project.Id);
        if (!result.MessageId.HasValue)
        {
            await FollowupAsync("Failed to post standup reminder. Check standup channel mapping.", ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"Standup reminder posted for project `{project.Name}` in <#{project.StandupChannelId}>.",
            ephemeral: true);
    }

    [SlashCommand("overdue-reminder", "Send an overdue task reminder to global task feed for testing.")]
    public async Task TestOverdueReminderAsync(int? taskId = null, int overdueHours = 30, int? projectId = null)
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Only Studio Lead/Admin can run test reminders.", ephemeral: true);
            return;
        }

        overdueHours = Math.Clamp(overdueHours, 1, 240);
        await DeferAsync(ephemeral: true);

        var project = await ResolveProjectAsync(projectId);
        if (project is null)
        {
            await FollowupAsync("Project context not found.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        TaskItem? task = null;
        if (taskId.HasValue)
        {
            task = await db.TaskItems.FirstOrDefaultAsync(x => x.Id == taskId.Value && x.ProjectId == project.Id);
        }

        if (task is null)
        {
            var activeSprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.IsActive);
            if (activeSprint is null)
            {
                activeSprint = new Sprint
                {
                    ProjectId = project.Id,
                    Name = "Test Sprint",
                    Goal = "Synthetic sprint for reminder testing",
                    IsActive = true
                };

                db.Sprints.Add(activeSprint);
                await db.SaveChangesAsync();
            }

            task = new TaskItem
            {
                ProjectId = project.Id,
                SprintId = activeSprint.Id,
                Type = TaskItemType.Task,
                Status = TaskItemStatus.InProgress,
                Title = "Synthetic overdue task (test)",
                Description = "Created by /test overdue-reminder",
                Points = 3,
                AssigneeId = Context.User.Id,
                CreatedById = Context.User.Id
            };

            db.TaskItems.Add(task);
        }

        task.Status = task.Status == TaskItemStatus.Done ? TaskItemStatus.InProgress : task.Status;
        task.SprintId ??= await db.Sprints
            .Where(x => x.ProjectId == project.Id && x.IsActive)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();
        task.CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-overdueHours);
        task.LastOverdueReminderDateLocal = null;

        await db.SaveChangesAsync();

        await _notificationService.NotifyOverdueTaskAsync(project.Id, task, TimeSpan.FromHours(overdueHours));
        var globalFeedText = project.GlobalNotificationChannelId.HasValue
            ? $"<#{project.GlobalNotificationChannelId.Value}>"
            : "`global-task-feed`";

        await FollowupAsync(
            $"Overdue reminder sent for task `#{task.Id}` to {globalFeedText}.",
            ephemeral: true);
    }

    [SlashCommand("overdue-scan", "Run overdue scan now and send reminders immediately.")]
    public async Task TestOverdueScanAsync(int? projectId = null)
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Only Studio Lead/Admin can run test reminders.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var project = await ResolveProjectAsync(projectId);
        if (project is null)
        {
            await FollowupAsync("Project context not found.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var overdueThresholdUtc = DateTimeOffset.UtcNow.AddHours(-24);

        var tasks = await db.TaskItems
            .Where(x =>
                x.ProjectId == project.Id &&
                x.Type == TaskItemType.Task &&
                x.SprintId != null &&
                x.Status != TaskItemStatus.Done &&
                x.CreatedAtUtc <= overdueThresholdUtc)
            .OrderBy(x => x.Id)
            .Take(25)
            .ToListAsync();

        foreach (var task in tasks)
        {
            var overdueBy = DateTimeOffset.UtcNow - task.CreatedAtUtc;
            await _notificationService.NotifyOverdueTaskAsync(project.Id, task, overdueBy);
        }

        await FollowupAsync($"Overdue scan completed. Sent {tasks.Count} reminder(s).", ephemeral: true);
    }

    private async Task<Project?> ResolveProjectAsync(int? projectId)
    {
        if (projectId.HasValue)
        {
            return await _projectService.GetProjectByIdAsync(projectId.Value);
        }

        var byChannel = await _projectService.GetProjectByChannelAsync(Context.Channel.Id);
        if (byChannel is not null)
        {
            return byChannel;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.Projects.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync();
    }

    private bool IsLeadOrAdmin()
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            return false;
        }

        return guildUser.GuildPermissions.Administrator ||
               guildUser.GuildPermissions.ManageGuild ||
               guildUser.Roles.Any(x => x.Name.Equals("Studio Lead", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RespondAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = false)
    {
        await base.RespondAsync(
            text: text,
            embed: embed,
            components: components,
            ephemeral: ephemeral);

        if (ephemeral)
        {
            _ = DeleteOriginalResponseAfterDelayAsync(TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
        }
    }

    private async Task FollowupAsync(
        string? text = null,
        Embed? embed = null,
        MessageComponent? components = null,
        bool ephemeral = false)
    {
        var message = await base.FollowupAsync(
            text: text,
            embed: embed,
            components: components,
            ephemeral: ephemeral);

        if (ephemeral)
        {
            _ = DeleteFollowupAfterDelayAsync(message, TimeSpan.FromSeconds(EphemeralAutoDeleteSeconds));
        }
    }

    private async Task DeleteOriginalResponseAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await Context.Interaction.DeleteOriginalResponseAsync();
        }
        catch
        {
        }
    }

    private async Task DeleteFollowupAfterDelayAsync(IUserMessage message, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await message.DeleteAsync();
        }
        catch
        {
        }
    }
}

public sealed class AddBacklogModal : IModal
{
    public string Title => "Add to Backlog";

    [InputLabel("Task Title")]
    [ModalTextInput("task_title", TextInputStyle.Short, maxLength: 200)]
    public string TaskTitle { get; set; } = string.Empty;

    [InputLabel("Story Points")]
    [ModalTextInput("points", TextInputStyle.Short, placeholder: "1-13", maxLength: 3)]
    public string Points { get; set; } = "1";

    [InputLabel("Description")]
    [ModalTextInput("description", TextInputStyle.Paragraph, maxLength: 1000)]
    public string? Description { get; set; }
}

public sealed class StartSprintModal : IModal
{
    public string Title => "Start Sprint";

    [InputLabel("Sprint Name")]
    [ModalTextInput("sprint_name", TextInputStyle.Short, maxLength: 120)]
    public string Name { get; set; } = string.Empty;

    [InputLabel("Sprint Goal")]
    [ModalTextInput("sprint_goal", TextInputStyle.Paragraph, maxLength: 500)]
    public string Goal { get; set; } = string.Empty;

    [InputLabel("Start Date (UTC+7)")]
    [ModalTextInput("sprint_start_date", TextInputStyle.Short, placeholder: "yyyy-MM-dd", maxLength: 10)]
    public string StartDate { get; set; } = string.Empty;

    [InputLabel("End Date (UTC+7)")]
    [ModalTextInput("sprint_end_date", TextInputStyle.Short, placeholder: "yyyy-MM-dd", maxLength: 10)]
    public string EndDate { get; set; } = string.Empty;
}

public sealed class ReportBugModal : IModal
{
    public string Title => "Report Bug";

    [InputLabel("Bug Title")]
    [ModalTextInput("bug_title", TextInputStyle.Short, maxLength: 200)]
    public string BugTitle { get; set; } = string.Empty;

    [InputLabel("Description")]
    [ModalTextInput("bug_desc", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Description { get; set; } = string.Empty;

    [InputLabel("Bug Points")]
    [ModalTextInput("bug_points", TextInputStyle.Short, placeholder: "3", maxLength: 3)]
    public string Points { get; set; } = "3";
}

public sealed class StandupReportModal : IModal
{
    public string Title => "Daily Standup Report";

    [InputLabel("Yesterday")]
    [ModalTextInput("standup_yesterday", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Yesterday { get; set; } = string.Empty;

    [InputLabel("Today")]
    [ModalTextInput("standup_today", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Today { get; set; } = string.Empty;

    [InputLabel("Blockers")]
    [ModalTextInput("standup_blockers", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Blockers { get; set; } = "None";
}




