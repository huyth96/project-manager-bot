using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;
using ProjectManagerBot.Services;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace ProjectManagerBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class InteractionModule(
    InitialSetupService initialSetupService,
    ProjectService projectService,
    NotificationService notificationService,
    StudioTimeService studioTime,
    IDbContextFactory<BotDbContext> dbContextFactory,
    ILogger<InteractionModule> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;
    private static readonly ConcurrentDictionary<string, SprintDraftState> SprintDrafts = new();
    private static readonly int[] SprintPickerHours = [9, 12, 15, 18, 21];
    private static readonly TimeSpan SprintDraftTtl = TimeSpan.FromMinutes(20);

    private readonly InitialSetupService _initialSetupService = initialSetupService;
    private readonly ProjectService _projectService = projectService;
    private readonly NotificationService _notificationService = notificationService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<InteractionModule> _logger = logger;

    [SlashCommand("studio-init", "Khởi tạo hạ tầng studio: role, category, kênh và phân quyền.")]
    public async Task StudioInitAsync()
    {
        if (Context.Guild.OwnerId != Context.User.Id)
        {
            await RespondAsync("Chỉ chủ server mới có thể chạy `/studio-init`.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var setupResult = await _initialSetupService.InitializeStudioAsync(Context.Guild);

        await ResetDatabaseAsync();

        var project = await _projectService.UpsertProjectAsync(
            name: "Project A: Đồ Án Tốt Nghiệp",
            channelId: setupResult.P1DashboardChannelId,
            bugChannelId: setupResult.P1BugsChannelId,
            standupChannelId: setupResult.DailyStandupChannelId,
            githubCommitsChannelId: setupResult.GitHubCommitsChannelId,
            globalNotificationChannelId: setupResult.GlobalTaskFeedChannelId);

        await _projectService.RefreshDashboardMessageAsync(project.Id);

        await FollowupAsync(
            $"Đã khởi tạo studio.\n" +
            $"- Số kênh đã xóa: `{setupResult.DeletedChannelsCount}`\n" +
            "- Cơ sở dữ liệu: `đã reset dữ liệu (không xóa file DB)`\n" +
            $"- Kênh dashboard: <#{setupResult.P1DashboardChannelId}>\n" +
            $"- Kênh lỗi: <#{setupResult.P1BugsChannelId}>\n" +
            $"- Kênh báo cáo ngày: <#{setupResult.DailyStandupChannelId}>\n" +
            $"- Kênh github-commits: <#{setupResult.GitHubCommitsChannelId}>\n" +
            $"- Kênh thông báo toàn cục: <#{setupResult.GlobalTaskFeedChannelId}>\n" +
            $"- Kênh shop: <#{setupResult.ShopChannelId}>\n" +
            $"- Mã dự án: `{project.Id}`",
            ephemeral: true);
    }

    private async Task ResetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 10;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                await db.Database.EnsureCreatedAsync(cancellationToken);

                await using var connection = db.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken);
                }

                var foreignKeysDisabled = false;
                try
                {
                    await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys = OFF;", cancellationToken);
                    foreignKeysDisabled = true;

                    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                    try
                    {
                        var tableNames = new List<string>();
                        await using (var readTablesCommand = connection.CreateCommand())
                        {
                            readTablesCommand.Transaction = transaction;
                            readTablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

                            await using var reader = await readTablesCommand.ExecuteReaderAsync(cancellationToken);
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                if (!reader.IsDBNull(0))
                                {
                                    tableNames.Add(reader.GetString(0));
                                }
                            }
                        }

                        foreach (var tableName in tableNames)
                        {
                            var escapedTableName = tableName.Replace("\"", "\"\"");
                            await ExecuteNonQueryAsync(connection, transaction, $"DELETE FROM \"{escapedTableName}\";", cancellationToken);
                        }

                        var hasSqliteSequence = false;
                        await using (var sequenceCheckCommand = connection.CreateCommand())
                        {
                            sequenceCheckCommand.Transaction = transaction;
                            sequenceCheckCommand.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence';";

                            var scalar = await sequenceCheckCommand.ExecuteScalarAsync(cancellationToken);
                            if (scalar is not null && scalar != DBNull.Value)
                            {
                                hasSqliteSequence = Convert.ToInt64(scalar, CultureInfo.InvariantCulture) > 0;
                            }
                        }

                        if (hasSqliteSequence)
                        {
                            await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM sqlite_sequence;", cancellationToken);
                        }

                        await transaction.CommitAsync(cancellationToken);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
                finally
                {
                    if (foreignKeysDisabled)
                    {
                        try
                        {
                            await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken);
                        }
                        catch
                        {
                        }
                    }
                }

                return;
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(300 * attempt);
                _logger.LogWarning(
                    ex,
                    "SQLite dang ban trong luc reset studio-init. Thu lai lan {Attempt}/{MaxAttempts} sau {DelayMs}ms.",
                    attempt,
                    maxAttempts,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Khong the reset co so du lieu vi SQLite dang bi khoa.");
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
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
                _logger.LogWarning("Không tìm thấy kênh tồn đọng cho dự án {ProjectId}", projectId);
            }
        }

        await _projectService.RefreshDashboardMessageAsync(projectId);

        await RespondAsync("Đã thêm nhiệm vụ vào tồn đọng.", ephemeral: true);
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
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể bắt đầu chu kỳ.", ephemeral: true);
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
                      "- **Bắt Đầu Chu Kỳ**: tạo chu kỳ mới\n" +
                      "- **Kết Thúc Chu Kỳ**: đóng chu kỳ, tính vận tốc\n" +
                      "- **Giao Nhiệm Vụ**: chỉ định người xử lý\n\n" +
                      "> ⚠️ Việc chưa xong khi kết thúc chu kỳ sẽ quay lại tồn đọng."
                    : "⛔ Không đủ quyền\n\n> ⚠️ Chỉ **Trưởng nhóm studio/Quản trị** mới được thao tác tại đây.")
            .AddField(
                "🧭 Hướng Dẫn Nhanh",
                "- Mở chu kỳ từ đầu mỗi vòng làm việc\n" +
                "- Theo dõi lỗi/nhiệm vụ trong Bảng Nhiệm Vụ\n" +
                "- Chốt chu kỳ đúng hạn để bảo toàn nhịp đội",
                false)
            .Build();

        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [ComponentInteraction("admin:start_sprint:*", true)]
    public async Task StartSprintFromAdminPanelAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể bắt đầu chu kỳ.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<StartSprintModal>($"sprint:start:{projectId}");
    }

    [ModalInteraction("sprint:start:*", true)]
    public async Task HandleStartSprintModalAsync(string projectIdRaw, StartSprintModal modal)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể bắt đầu chu kỳ.", ephemeral: true);
            return;
        }

        var name = modal.Name.Trim();
        var goal = string.IsNullOrWhiteSpace(modal.Goal) ? "Chưa đặt mục tiêu" : modal.Goal.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await RespondAsync("Tên chu kỳ không được để trống.", ephemeral: true);
            return;
        }

        var hasManualStart = !string.IsNullOrWhiteSpace(modal.StartDate);
        var hasManualEnd = !string.IsNullOrWhiteSpace(modal.EndDate);
        if (hasManualStart || hasManualEnd)
        {
            DateTime? startDateLocal = null;
            DateTime? endDateLocal = null;

            if (hasManualStart)
            {
                if (!TryParseSprintDateTime(modal.StartDate, isEndDate: false, out var parsedStart))
                {
                    await RespondAsync("Ngày bắt đầu không hợp lệ. Dùng `yyyy-MM-dd` hoặc `yyyy-MM-dd HH:mm`.", ephemeral: true);
                    return;
                }

                startDateLocal = parsedStart;
            }

            if (hasManualEnd)
            {
                if (!TryParseSprintDateTime(modal.EndDate, isEndDate: true, out var parsedEnd))
                {
                    await RespondAsync("Ngày kết thúc không hợp lệ. Dùng `yyyy-MM-dd` hoặc `yyyy-MM-dd HH:mm`.", ephemeral: true);
                    return;
                }

                endDateLocal = parsedEnd;
            }

            if (startDateLocal.HasValue && endDateLocal.HasValue && endDateLocal.Value < startDateLocal.Value)
            {
                await RespondAsync("Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu.", ephemeral: true);
                return;
            }

            if (startDateLocal.HasValue && endDateLocal.HasValue)
            {
                await CreateSprintAndPromptBacklogSelectionAsync(
                    projectId: projectId,
                    name: name,
                    goal: goal,
                    startDateLocal: startDateLocal.Value,
                    endDateLocal: endDateLocal.Value,
                    actorDiscordId: Context.User.Id);
                return;
            }

            var tokenWithManual = Guid.NewGuid().ToString("N")[..12];
            SprintDrafts[tokenWithManual] = new SprintDraftState(
                ProjectId: projectId,
                UserId: Context.User.Id,
                Name: name,
                Goal: goal,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                StartDateLocal: startDateLocal,
                EndDateLocal: endDateLocal);

            var componentsWithManual = BuildSprintDateTimePickerComponents(tokenWithManual, _studioTime.LocalDate);
            await RespondTransientAsync(
                "🗓️ Đã nhận một phần thời gian bạn nhập tay.\n" +
                "- Hãy chọn nốt mốc còn thiếu bằng menu bên dưới (UTC+7).",
                components: componentsWithManual);
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        SprintDrafts[token] = new SprintDraftState(
            ProjectId: projectId,
            UserId: Context.User.Id,
            Name: name,
            Goal: goal,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            StartDateLocal: null,
            EndDateLocal: null);

        var components = BuildSprintDateTimePickerComponents(token, _studioTime.LocalDate);
        await RespondTransientAsync(
            "🗓️ Chọn thời điểm bắt đầu và kết thúc chu kỳ (UTC+7).\n" +
            "- Bạn có thể nhập tay trong modal bằng `yyyy-MM-dd HH:mm` nếu muốn.\n" +
            "- Nếu không, hãy dùng 2 menu chọn bên dưới.",
            components: components);
    }

    [ComponentInteraction("sprint:pick_start:*", true)]
    public async Task PickSprintStartDateTimeAsync(string token, string[] selectedValues)
    {
        var draft = await GetValidSprintDraftAsync(token);
        if (draft is null)
        {
            return;
        }

        var picked = selectedValues.FirstOrDefault();
        if (!TryParseSprintPickerValue(picked, out var startDateLocal))
        {
            await RespondAsync("Mốc thời gian bắt đầu không hợp lệ.", ephemeral: true);
            return;
        }

        var updated = draft with { StartDateLocal = startDateLocal };
        SprintDrafts[token] = updated;
        await TryFinalizeSprintDraftAsync(token, updated);
    }

    [ComponentInteraction("sprint:pick_end:*", true)]
    public async Task PickSprintEndDateTimeAsync(string token, string[] selectedValues)
    {
        var draft = await GetValidSprintDraftAsync(token);
        if (draft is null)
        {
            return;
        }

        var picked = selectedValues.FirstOrDefault();
        if (!TryParseSprintPickerValue(picked, out var endDateLocal))
        {
            await RespondAsync("Mốc thời gian kết thúc không hợp lệ.", ephemeral: true);
            return;
        }

        var updated = draft with { EndDateLocal = endDateLocal };
        SprintDrafts[token] = updated;
        await TryFinalizeSprintDraftAsync(token, updated);
    }

    private async Task TryFinalizeSprintDraftAsync(string token, SprintDraftState draft)
    {
        if (!draft.StartDateLocal.HasValue || !draft.EndDateLocal.HasValue)
        {
            var startText = draft.StartDateLocal.HasValue
                ? draft.StartDateLocal.Value.ToString("yyyy-MM-dd HH:mm")
                : "chưa chọn";
            var endText = draft.EndDateLocal.HasValue
                ? draft.EndDateLocal.Value.ToString("yyyy-MM-dd HH:mm")
                : "chưa chọn";

            await RespondTransientAsync(
                $"Đã cập nhật lựa chọn.\n- Bắt đầu: `{startText}`\n- Kết thúc: `{endText}`",
                ephemeral: true);
            return;
        }

        if (draft.EndDateLocal.Value < draft.StartDateLocal.Value)
        {
            await RespondAsync("Thời điểm kết thúc phải lớn hơn hoặc bằng thời điểm bắt đầu.", ephemeral: true);
            return;
        }

        if (!SprintDrafts.TryRemove(token, out var finalizedDraft))
        {
            await RespondAsync("Phiên chọn thời gian đã hết hạn hoặc đã được xử lý.", ephemeral: true);
            return;
        }

        await CreateSprintAndPromptBacklogSelectionAsync(
            projectId: finalizedDraft.ProjectId,
            name: finalizedDraft.Name,
            goal: finalizedDraft.Goal,
            startDateLocal: finalizedDraft.StartDateLocal!.Value,
            endDateLocal: finalizedDraft.EndDateLocal!.Value,
            actorDiscordId: Context.User.Id);
    }

    [ComponentInteraction("sprint:select_tasks:*", true)]
    public async Task HandleSprintTaskSelectionAsync(string sprintIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(sprintIdRaw, out var sprintId))
        {
            await RespondAsync("Ngữ cảnh chu kỳ không hợp lệ.", ephemeral: true);
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
            await RespondAsync("Không tìm thấy chu kỳ.", ephemeral: true);
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

        await RespondTransientAsync($"Đã thêm {tasks.Count} nhiệm vụ vào chu kỳ `{sprint.Name}`.");
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
            await RespondAsync("Dự án này chưa có chu kỳ nào đang chạy.", ephemeral: true);
            return;
        }

        var myTasks = await db.TaskItems
            .Where(x => x.ProjectId == project.Id && x.SprintId == activeSprint.Id && x.AssigneeId == Context.User.Id)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.Id)
            .ToListAsync();

        var description = myTasks.Count == 0
            ? "💤 Chưa có nhiệm vụ đang xử lý\n\n- Mở **Bảng Nhiệm Vụ** để nhận việc.\n\n> ⚠️ Nhận nhiệm vụ tại Bảng Nhiệm Vụ, không nhận ở Việc Của Tôi."
            : string.Join(
                "\n\n",
                myTasks.Select(x =>
                    $"📜 Nhiệm vụ #{x.Id}\n" +
                    $"- **Trạng thái:** {GetStatusBadge(x.Status)}\n" +
                    $"- **Tiêu đề:** **{x.Title}**\n" +
                    $"- **Điểm:** `{x.Points}`"));

        var embed = new EmbedBuilder()
            .WithTitle($"🧙 Sổ Nhiệm Vụ Cá Nhân • {project.Name}")
            .WithDescription(description)
            .WithColor(Color.DarkPurple)
            .AddField("👤 Người Chơi", Context.User.Mention, true)
            .AddField("🎯 Số Nhiệm Vụ", myTasks.Count.ToString(), true)
            .AddField("⚔️ Chu Kỳ", activeSprint.Name, true)
            .Build();

        var components = new ComponentBuilder()
            .WithButton("✅ Đánh Dấu Xong", $"board:done:{project.Id}", ButtonStyle.Success)
            .WithButton("🗺️ Mở Bảng Nhiệm Vụ", $"board:refresh:{project.Id}", ButtonStyle.Secondary)
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
            ? "`0/0` đã hoàn thành"
            : $"`{done}/{totalSprintTasks}` đã hoàn thành (`{(int)Math.Round((double)done / totalSprintTasks * 100)}%`)";

        var laneSummary = string.Join(
            "\n",
            [
                $"- 📜 **Tồn đọng:** `{backlog}`",
                $"- 🪓 **Cần làm:** `{todo}`",
                $"- ⚔️ **Đang làm:** `{inProgress}`",
                $"- 🏆 **Hoàn thành:** `{done}`",
                $"- 🐞 **Lỗi mở:** `{openBugs}`"
            ]);

        var queueText = claimableTasks.Count == 0
            ? "💤 Chưa có nhiệm vụ trong hàng chờ."
            : string.Join("\n\n", claimableTasks.Select(x =>
                $"📌 Nhiệm vụ #{x.Id}\n" +
                $"- **Tên:** **{Truncate(x.Title, 70)}**\n" +
                $"- **Điểm:** `{x.Points}`"));

        var myFocusText = myFocusTasks.Count == 0
            ? "🍃 Bạn chưa có nhiệm vụ đang xử lý."
            : string.Join("\n\n", myFocusTasks.Select(x =>
                $"🛡️ Nhiệm vụ #{x.Id}\n" +
                $"- **Trạng thái:** {GetStatusBadge(x.Status)}\n" +
                $"- **Tên:** **{Truncate(x.Title, 65)}**"));

        var embedColor = activeSprint is null
            ? Color.DarkGrey
            : done >= Math.Max(1, totalSprintTasks / 2) ? Color.DarkGreen : Color.DarkOrange;

        var stateText = activeSprint is null
            ? "> ⚠️ Chưa có chu kỳ nào đang chạy. Trưởng nhóm/Quản trị hãy bắt đầu chu kỳ."
            : $"- **Chu kỳ:** **{activeSprint.Name}**";

        var embed = new EmbedBuilder()
            .WithTitle($"🗺️ Bảng Nhiệm Vụ Vương Quốc • {project.Name}")
            .WithColor(embedColor)
            .WithDescription(
                "🔥 Trạng Thái Chiến Dịch\n" +
                $"**Chế độ:** `{(activeSprint is null ? "Nghỉ giữa chiến dịch" : "Đang giao tranh")}`\n" +
                $"{stateText}\n" +
                "━━━━━━━━━━━━━━━━━━━━")
            .AddField("📈 Tiến Độ", $"{progressBar}\n{completionText}", false)
            .AddField("🧩 Bản Đồ Trạng Thái", laneSummary, true)
            .AddField("🎯 Hàng Chờ Nhận Việc", queueText, true)
            .AddField("🧙 Việc Của Tôi", myFocusText, false)
            .WithFooter("Nhận nhiệm vụ tại đây để tránh trùng lặp và để theo dõi tiến độ toàn đội.")
            .WithCurrentTimestamp()
            .Build();

        var components = new ComponentBuilder()
            .WithButton(
                ButtonBuilder.CreateSuccessButton("⚔️ Nhận Nhiệm Vụ", $"board:claim:{project.Id}")
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
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        await ViewBoardAsync();
    }

    [ComponentInteraction("board:claim:*", true)]
    public async Task OpenBoardClaimPickerAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var activeSprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive);
        if (activeSprint is null)
        {
            await RespondAsync("Chưa có chu kỳ đang chạy.", ephemeral: true);
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
            await RespondTransientAsync("Không có nhiệm vụ chưa giao để nhận.");
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"board:claim_select:{projectId}")
            .WithPlaceholder("Chọn nhiệm vụ để nhận")
            .WithMinValues(1)
            .WithMaxValues(tasks.Count);

        foreach (var task in tasks)
        {
            menu.AddOption(Truncate(task.Title, 90), task.Id.ToString(), $"#{task.Id} - {task.Points}pt");
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondTransientAsync("⚔️ Chọn nhiệm vụ cần nhận", components: components);
    }

    [ComponentInteraction("board:claim_select:*", true)]
    public async Task ClaimBoardTasksAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        var taskIds = ParseSelectedTaskIds(selectedTaskIds);
        if (taskIds.Count == 0)
        {
            await RespondAsync("Chưa chọn nhiệm vụ hợp lệ để nhận.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var claimedTaskIds = new List<int>();
        foreach (var taskId in taskIds)
        {
            var affected = await db.TaskItems
                .Where(x =>
                    x.ProjectId == projectId &&
                    x.Id == taskId &&
                    x.Type == TaskItemType.Task &&
                    x.Status == TaskItemStatus.Todo &&
                    x.AssigneeId == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.AssigneeId, Context.User.Id)
                    .SetProperty(x => x.Status, TaskItemStatus.InProgress));

            if (affected > 0)
            {
                claimedTaskIds.Add(taskId);
            }
        }

        if (claimedTaskIds.Count == 0)
        {
            await RespondTransientAsync("Không nhận được nhiệm vụ nào. Có thể các nhiệm vụ đã được người khác nhận trước.");
            return;
        }

        var tasks = await db.TaskItems
            .Where(x => claimedTaskIds.Contains(x.Id))
            .ToListAsync();

        await _projectService.RefreshDashboardMessageAsync(projectId);
        await _notificationService.NotifyTaskClaimedAsync(projectId, Context.User.Id, tasks);

        var skipped = taskIds.Count - claimedTaskIds.Count;
        if (skipped > 0)
        {
            await RespondTransientAsync($"Đã nhận {claimedTaskIds.Count} nhiệm vụ. Có {skipped} nhiệm vụ đã bị người khác nhận trước.");
            return;
        }

        await RespondTransientAsync($"Đã nhận {claimedTaskIds.Count} nhiệm vụ.");
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
            "Nhận nhiệm vụ đã chuyển sang **Bảng Nhiệm Vụ**. Dùng `Bảng Nhiệm Vụ -> Nhận Nhiệm Vụ`.",
            ephemeral: true);
    }

    [ComponentInteraction("tasks:done:*", true)]
    public async Task CompleteTaskSelectionAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
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
            await RespondAsync("Bạn chưa có nhiệm vụ đang xử lý.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"tasks:done_select:{projectId}")
            .WithPlaceholder("Chọn nhiệm vụ để đánh dấu hoàn thành")
            .WithMinValues(1)
            .WithMaxValues(tasks.Count);

        foreach (var task in tasks)
        {
            menu.AddOption(Truncate(task.Title, 90), task.Id.ToString(), $"#{task.Id} - {task.Points}pt");
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondTransientAsync("✅ Chọn nhiệm vụ để đánh dấu hoàn thành", components: components);
    }

    [ComponentInteraction("tasks:start_select:*", true)]
    public async Task MarkTaskInProgressAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
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

        await RespondTransientAsync($"Đã chuyển {tasks.Count} nhiệm vụ sang trạng thái Đang Làm.");
    }

    [ComponentInteraction("tasks:done_select:*", true)]
    public async Task MarkTaskDoneAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
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

        await RespondTransientAsync($"Đã đánh dấu hoàn thành {tasks.Count} nhiệm vụ. XP +{xp} (Tổng: {totalXp}).");
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
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (project is null)
        {
            await RespondAsync("Không tìm thấy dự án.", ephemeral: true);
            return;
        }

        var bugTask = new TaskItem
        {
            ProjectId = projectId,
            SprintId = null,
            Type = TaskItemType.Bug,
            Status = TaskItemStatus.Todo,
            Title = modal.BugTitle.Trim(),
            Description = modal.Description.Trim(),
            Points = 1,
            CreatedById = Context.User.Id
        };

        db.TaskItems.Add(bugTask);
        await db.SaveChangesAsync();

        var bugChannel = Context.Guild.GetTextChannel(project.BugChannelId);
        if (bugChannel is null)
        {
            await RespondAsync("Không thể truy cập kênh lỗi.", ephemeral: true);
            return;
        }

        var bugEmbed = new EmbedBuilder()
            .WithTitle($"🐉 Quái Lỗi Xuất Hiện • #{bugTask.Id}")
            .WithDescription(
                "🚨 Cảnh Báo Lỗi\n" +
                $"📌 {bugTask.Title}\n\n" +
                $"{(bugTask.Description ?? "Không có mô tả")}\n\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "> ⚠️ Mọi người có thể bấm **Nhận Lỗi** để xử lý.")
            .WithColor(Color.Red)
            .AddField("👤 Người Báo Lỗi", Context.User.Mention, true)
            .AddField("🎯 Điểm", bugTask.Points.ToString(), true)
            .AddField("🧭 Trạng Thái", GetStatusBadge(bugTask.Status), true)
            .WithCurrentTimestamp()
            .Build();

        var components = new ComponentBuilder()
            .WithButton("🛡️ Nhận Lỗi", $"bug:claim:{bugTask.Id}", ButtonStyle.Primary)
            .WithButton("✅ Đã Sửa", $"bug:fixed:{bugTask.Id}", ButtonStyle.Success)
            .Build();

        await bugChannel.SendMessageAsync(embed: bugEmbed, components: components);
        await _projectService.RefreshDashboardMessageAsync(projectId);
        await RespondAsync($"Đã báo lỗi tại <#{project.BugChannelId}>.", ephemeral: true);
    }

    [ComponentInteraction("bug:claim:*", true)]
    public async Task ClaimBugAsync(string bugTaskIdRaw)
    {
        if (!int.TryParse(bugTaskIdRaw, out var bugTaskId))
        {
            await RespondAsync("Ngữ cảnh lỗi không hợp lệ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var affected = await db.TaskItems
            .Where(x =>
                x.Id == bugTaskId &&
                x.Type == TaskItemType.Bug &&
                x.Status != TaskItemStatus.Done &&
                (x.AssigneeId == null || x.AssigneeId == Context.User.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AssigneeId, Context.User.Id)
                .SetProperty(x => x.Status, TaskItemStatus.InProgress));

        if (affected == 0)
        {
            var current = await db.TaskItems.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == bugTaskId && x.Type == TaskItemType.Bug);
            if (current is null)
            {
                await RespondAsync("Không tìm thấy lỗi.", ephemeral: true);
                return;
            }

            if (current.Status == TaskItemStatus.Done)
            {
                await RespondAsync("Lỗi này đã được sửa xong trước đó.", ephemeral: true);
                return;
            }

            if (current.AssigneeId.HasValue && current.AssigneeId != Context.User.Id)
            {
                await RespondAsync($"Lỗi này đã được nhận bởi <@{current.AssigneeId}>.", ephemeral: true);
                return;
            }

            await RespondAsync("Không thể nhận lỗi ở trạng thái hiện tại.", ephemeral: true);
            return;
        }

        var bug = await db.TaskItems.AsNoTracking()
            .FirstAsync(x => x.Id == bugTaskId && x.Type == TaskItemType.Bug);
        var embed = BuildBugStateEmbed(bug, "Đang Xử Lý", Color.Orange, Context.User.Id);
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.Message.ModifyAsync(props => props.Embed = embed);
        }

        await _projectService.RefreshDashboardMessageAsync(bug.ProjectId);
        await RespondAsync("Đã nhận lỗi.", ephemeral: true);
    }

    [ComponentInteraction("bug:fixed:*", true)]
    public async Task FixBugAsync(string bugTaskIdRaw)
    {
        if (!int.TryParse(bugTaskIdRaw, out var bugTaskId))
        {
            await RespondAsync("Ngữ cảnh lỗi không hợp lệ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var bug = await db.TaskItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bugTaskId && x.Type == TaskItemType.Bug);
        if (bug is null)
        {
            await RespondAsync("Không tìm thấy lỗi.", ephemeral: true);
            return;
        }

        if (bug.Status == TaskItemStatus.Done)
        {
            await RespondAsync("Lỗi này đã được sửa xong trước đó.", ephemeral: true);
            return;
        }

        var isAdmin = IsLeadOrAdmin();
        if (bug.AssigneeId.HasValue && bug.AssigneeId != Context.User.Id && !isAdmin)
        {
            await RespondAsync($"Chỉ người xử lý <@{bug.AssigneeId}> hoặc Trưởng nhóm/Quản trị mới có thể đóng lỗi này.", ephemeral: true);
            return;
        }

        var eligible = db.TaskItems
            .Where(x =>
                x.Id == bugTaskId &&
                x.Type == TaskItemType.Bug &&
                x.Status != TaskItemStatus.Done);
        if (!isAdmin)
        {
            eligible = eligible.Where(x => !x.AssigneeId.HasValue || x.AssigneeId == Context.User.Id);
        }

        var affected = await eligible.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.AssigneeId, Context.User.Id)
            .SetProperty(x => x.Status, TaskItemStatus.Done));
        if (affected == 0)
        {
            await RespondAsync("Lỗi không còn ở trạng thái có thể đóng.", ephemeral: true);
            return;
        }

        bug = await db.TaskItems.AsNoTracking().FirstAsync(x => x.Id == bugTaskId && x.Type == TaskItemType.Bug);

        var xpAward = Math.Max(20, bug.Points * 5);
        var totalXp = await _projectService.AwardXpAsync(Context.User.Id, xpAward);

        var embed = BuildBugStateEmbed(bug, "Đã Sửa", Color.Green, Context.User.Id);
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.Message.ModifyAsync(props => props.Embed = embed);
        }

        await _projectService.RefreshDashboardMessageAsync(bug.ProjectId);
        await RespondAsync($"Đã đánh dấu lỗi đã sửa. XP +{xpAward} (Tổng: {totalXp}).", ephemeral: true);
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
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể kết thúc chu kỳ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.IsActive);
        if (sprint is null)
        {
            await RespondAsync("Không có chu kỳ nào đang chạy để kết thúc.", ephemeral: true);
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
            $"Đã kết thúc chu kỳ.\n" +
            $"- Vận tốc: `{velocity}` điểm\n" +
            $"- Hoàn thành: `{doneTasks.Count}`\n" +
            $"- Trả về tồn đọng: `{unfinishedTasks.Count}`",
            ephemeral: true);
    }

    [ComponentInteraction("admin:end_sprint:*", true)]
    public async Task EndSprintFromAdminPanelAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể kết thúc chu kỳ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive);
        if (sprint is null)
        {
            await RespondAsync("Không có chu kỳ nào đang chạy để kết thúc.", ephemeral: true);
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
            $"Đã kết thúc chu kỳ.\n" +
            $"- Vận tốc: `{velocity}` điểm\n" +
            $"- Hoàn thành: `{doneTasks.Count}`\n" +
            $"- Trả về tồn đọng: `{unfinishedTasks.Count}`",
            ephemeral: true);
    }

    [ComponentInteraction("admin:assign_task:*", true)]
    public async Task OpenAssignTaskPickerAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm studio/Quản trị mới được giao nhiệm vụ trong chu kỳ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var activeSprint = await db.Sprints.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive);
        if (activeSprint is null)
        {
            await RespondAsync("Chưa có chu kỳ đang chạy. Hãy bắt đầu chu kỳ trước.", ephemeral: true);
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
            await RespondAsync("Không có nhiệm vụ nào có thể giao trong chu kỳ hiện tại.", ephemeral: true);
            return;
        }

        var taskMenu = new SelectMenuBuilder()
            .WithCustomId($"admin:assign_pick_task:{projectId}")
            .WithPlaceholder("Chọn nhiệm vụ cần giao")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var task in assignableTasks)
        {
            var assigneeText = task.AssigneeId.HasValue ? $"Người nhận <@{task.AssigneeId.Value}>" : "Chưa giao";
            taskMenu.AddOption(
                label: $"#{task.Id} {Truncate(task.Title, 70)}",
                value: task.Id.ToString(),
                description: $"{GetStatusBadge(task.Status)} - {assigneeText}");
        }

        var components = new ComponentBuilder().WithSelectMenu(taskMenu).Build();
        await RespondTransientAsync("🎯 Chọn nhiệm vụ trong chu kỳ để giao", components: components);
    }

    [ComponentInteraction("admin:assign_pick_task:*", true)]
    public async Task OpenAssignUserPickerAsync(string projectIdRaw, string[] selectedTaskIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm studio/Quản trị mới được giao nhiệm vụ trong chu kỳ.", ephemeral: true);
            return;
        }

        var selected = selectedTaskIds.FirstOrDefault();
        if (!int.TryParse(selected, out var taskId))
        {
            await RespondAsync("Lựa chọn nhiệm vụ không hợp lệ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var task = await db.TaskItems.FirstOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId && x.Type == TaskItemType.Task);
        if (task is null)
        {
            await RespondAsync("Không tìm thấy nhiệm vụ.", ephemeral: true);
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
            $"👥 Giao nhiệm vụ `#{task.Id} {Truncate(task.Title, 60)}` cho:",
            components: components);
    }

    [ComponentInteraction("admin:assign_pick_user:*:*", true)]
    public async Task AssignTaskToUserAsync(string projectIdRaw, string taskIdRaw, string[] selectedUserIds)
    {
        if (!int.TryParse(projectIdRaw, out var projectId) || !int.TryParse(taskIdRaw, out var taskId))
        {
            await RespondAsync("Ngữ cảnh giao việc không hợp lệ.", ephemeral: true);
            return;
        }

        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm studio/Quản trị mới được giao nhiệm vụ trong chu kỳ.", ephemeral: true);
            return;
        }

        var selectedUser = selectedUserIds.FirstOrDefault();
        if (!ulong.TryParse(selectedUser, out var assigneeId))
        {
            await RespondAsync("Người nhận được chọn không hợp lệ.", ephemeral: true);
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var task = await db.TaskItems.FirstOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId && x.Type == TaskItemType.Task);
        if (task is null)
        {
            await RespondAsync("Không tìm thấy nhiệm vụ.", ephemeral: true);
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

        await RespondTransientAsync($"Đã giao nhiệm vụ `#{task.Id}` cho <@{assigneeId}>.");
    }

    [ComponentInteraction("standup:report:*", true)]
    public async Task OpenStandupReportModalAsync(string projectIdRaw)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<StandupReportModal>($"standup:submit:{projectId}");
    }

    [ModalInteraction("standup:submit:*", true)]
    public async Task SubmitStandupReportAsync(string projectIdRaw, StandupReportModal modal)
    {
        if (!int.TryParse(projectIdRaw, out var projectId))
        {
            await RespondAsync("Ngữ cảnh dự án không hợp lệ.", ephemeral: true);
            return;
        }

        await _projectService.SaveStandupReportAsync(
            projectId: projectId,
            discordUserId: Context.User.Id,
            yesterday: modal.Yesterday.Trim(),
            today: modal.Today.Trim(),
            blockers: modal.Blockers.Trim());

        await _projectService.RefreshStandupSummaryAsync(projectId);
        await RespondAsync("Đã gửi báo cáo hằng ngày.", ephemeral: true);
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
            _logger.LogDebug(ex, "Không thể tự xóa phản hồi tương tác");
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
            _logger.LogDebug(ex, "Không thể tự xóa phản hồi theo sau của tương tác");
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
            "Kênh này chưa được gắn với dự án nào. Hãy dùng `/project setup <ten-du-an>` trước.",
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

    private async Task CreateSprintAndPromptBacklogSelectionAsync(
        int projectId,
        string name,
        string goal,
        DateTime startDateLocal,
        DateTime endDateLocal,
        ulong actorDiscordId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var hasActiveSprint = await db.Sprints.AnyAsync(x => x.ProjectId == projectId && x.IsActive);
        if (hasActiveSprint)
        {
            await RespondAsync("Đã có chu kỳ đang hoạt động. Hãy kết thúc chu kỳ hiện tại trước khi tạo chu kỳ mới.", ephemeral: true);
            return;
        }

        var sprint = new Sprint
        {
            ProjectId = projectId,
            Name = name,
            Goal = goal,
            IsActive = true,
            StartDateLocal = DateTime.SpecifyKind(startDateLocal, DateTimeKind.Unspecified),
            EndDateLocal = DateTime.SpecifyKind(endDateLocal, DateTimeKind.Unspecified)
        };

        db.Sprints.Add(sprint);
        await db.SaveChangesAsync();
        await _notificationService.NotifySprintStartedAsync(projectId, actorDiscordId, sprint);

        var backlogTasks = await db.TaskItems
            .Where(x => x.ProjectId == projectId && x.SprintId == null && x.Type == TaskItemType.Task)
            .OrderBy(x => x.Id)
            .Take(25)
            .ToListAsync();

        if (backlogTasks.Count == 0)
        {
            await _projectService.RefreshDashboardMessageAsync(projectId);
            await RespondAsync("Đã tạo chu kỳ nhưng chưa có nhiệm vụ tồn đọng nào được đưa vào.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"sprint:select_tasks:{sprint.Id}")
            .WithPlaceholder("Chọn nhiệm vụ đưa vào chu kỳ này")
            .WithMinValues(1)
            .WithMaxValues(backlogTasks.Count);

        foreach (var task in backlogTasks)
        {
            menu.AddOption(new SelectMenuOptionBuilder(
                label: Truncate(task.Title, 90),
                value: task.Id.ToString(),
                description: $"Điểm: {task.Points}"));
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await _projectService.RefreshDashboardMessageAsync(projectId);

        await RespondTransientAsync(
            $"Chu kỳ `{sprint.Name}` đã kích hoạt. Hãy chọn nhiệm vụ cần đưa vào:",
            components: components);
    }

    private async Task<SprintDraftState?> GetValidSprintDraftAsync(string token)
    {
        if (!SprintDrafts.TryGetValue(token, out var draft))
        {
            await RespondAsync("Phiên chọn thời gian đã hết hạn hoặc không tồn tại.", ephemeral: true);
            return null;
        }

        if (draft.UserId != Context.User.Id)
        {
            await RespondAsync("Bạn không thể thao tác trên phiên chọn thời gian của người khác.", ephemeral: true);
            return null;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAtUtc > SprintDraftTtl)
        {
            SprintDrafts.TryRemove(token, out _);
            await RespondAsync("Phiên chọn thời gian đã hết hạn. Hãy bắt đầu lại thao tác tạo chu kỳ.", ephemeral: true);
            return null;
        }

        return draft;
    }

    private static bool TryParseSprintDateTime(string raw, bool isEndDate, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var input = raw.Trim();
        var formats = new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd" };
        if (!DateTime.TryParseExact(
                input,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (input.Length == 10)
        {
            parsed = isEndDate
                ? parsed.Date.AddHours(23).AddMinutes(59)
                : parsed.Date.AddHours(9);
        }

        value = parsed;
        return true;
    }

    private static bool TryParseSprintPickerValue(string? raw, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                raw.Trim(),
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        value = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return true;
    }

    private static MessageComponent BuildSprintDateTimePickerComponents(string token, DateTime localDate)
    {
        var options = BuildSprintPickerOptions(localDate);
        var startPicker = new SelectMenuBuilder()
            .WithCustomId($"sprint:pick_start:{token}")
            .WithPlaceholder("Chọn thời điểm bắt đầu (UTC+7)")
            .WithMinValues(1)
            .WithMaxValues(1);

        var endPicker = new SelectMenuBuilder()
            .WithCustomId($"sprint:pick_end:{token}")
            .WithPlaceholder("Chọn thời điểm kết thúc (UTC+7)")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var option in options)
        {
            startPicker.AddOption(new SelectMenuOptionBuilder(option.Label, option.Value, option.Description));
            endPicker.AddOption(new SelectMenuOptionBuilder(option.Label, option.Value, option.Description));
        }

        return new ComponentBuilder()
            .WithSelectMenu(startPicker)
            .WithSelectMenu(endPicker)
            .Build();
    }

    private static IReadOnlyList<SelectMenuOptionBuilder> BuildSprintPickerOptions(DateTime localDate)
    {
        var options = new List<SelectMenuOptionBuilder>(25);
        var culture = CultureInfo.GetCultureInfo("vi-VN");

        for (var dayOffset = 0; dayOffset < 5; dayOffset++)
        {
            var date = localDate.Date.AddDays(dayOffset);
            foreach (var hour in SprintPickerHours)
            {
                var point = DateTime.SpecifyKind(date.AddHours(hour), DateTimeKind.Unspecified);
                options.Add(new SelectMenuOptionBuilder(
                    label: $"{point.ToString("ddd", culture)} {point:dd/MM HH:mm}",
                    value: point.ToString("yyyy-MM-dd HH:mm"),
                    description: $"{point:yyyy-MM-dd} (UTC+7)"));
            }
        }

        return options;
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
            TaskItemStatus.Backlog => "📜 Tồn Đọng",
            TaskItemStatus.Todo => "🪓 Cần Làm",
            TaskItemStatus.InProgress => "⚔️ Đang Làm",
            TaskItemStatus.Done => "🏆 Hoàn Thành",
            _ => "❓ Không xác định"
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
                ButtonBuilder.CreateSuccessButton("🔥 Bắt Đầu Chu Kỳ", $"admin:start_sprint:{projectId}")
                    .WithDisabled(disable))
            .WithButton(
                ButtonBuilder.CreateDangerButton("🏁 Kết Thúc Chu Kỳ", $"admin:end_sprint:{projectId}")
                    .WithDisabled(disable))
            .WithButton(
                ButtonBuilder.CreatePrimaryButton("🎯 Giao Nhiệm Vụ", $"admin:assign_task:{projectId}")
                    .WithDisabled(disable))
            .Build();
    }

    private Embed BuildBacklogItemEmbed(TaskItem taskItem)
    {
        return new EmbedBuilder()
            .WithTitle($"📜 Thẻ Tồn Đọng • #{taskItem.Id}")
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
            .WithTitle($"🐲 Theo Dõi Lỗi • #{bug.Id}")
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

[Group("project", "Lệnh dự án")]
[RequireContext(ContextType.Guild)]
public sealed class ProjectCommandModule(
    ProjectService projectService,
    GitHubTrackingService gitHubTrackingService) : InteractionModuleBase<SocketInteractionContext>
{
    private const int EphemeralAutoDeleteSeconds = 20;

    private readonly ProjectService _projectService = projectService;
    private readonly GitHubTrackingService _gitHubTrackingService = gitHubTrackingService;

    [SlashCommand("setup", "Gắn kênh hiện tại vào dự án và khởi tạo dashboard.")]
    public async Task SetupProjectAsync(string name)
    {
        await DeferAsync(ephemeral: true);

        var bugChannel = Context.Guild.TextChannels
            .FirstOrDefault(x => x.Name.Contains("bugs", StringComparison.OrdinalIgnoreCase));
        var standupChannel = Context.Guild.TextChannels
            .FirstOrDefault(x => x.Name.Contains("daily-standup", StringComparison.OrdinalIgnoreCase));
        var githubCommitsChannel = Context.Guild.TextChannels
            .FirstOrDefault(x => x.Name.Contains("github-commits", StringComparison.OrdinalIgnoreCase));
        var globalTaskFeed = Context.Guild.TextChannels
            .FirstOrDefault(x => x.Name.Contains("global-task-feed", StringComparison.OrdinalIgnoreCase));

        var project = await _projectService.UpsertProjectAsync(
            name: name.Trim(),
            channelId: Context.Channel.Id,
            bugChannelId: bugChannel?.Id ?? Context.Channel.Id,
            standupChannelId: standupChannel?.Id ?? Context.Channel.Id,
            githubCommitsChannelId: githubCommitsChannel?.Id ?? Context.Channel.Id,
            globalNotificationChannelId: globalTaskFeed?.Id);

        await _projectService.RefreshDashboardMessageAsync(project.Id);

        await FollowupAsync(
            $"Đã gắn kênh với dự án thành công.\n" +
            $"- Mã dự án: `{project.Id}`\n" +
            $"- Kênh dashboard: <#{project.ChannelId}>\n" +
            $"- Kênh lỗi: <#{project.BugChannelId}>\n" +
            $"- Kênh báo cáo ngày: <#{project.StandupChannelId}>\n" +
            $"- Kênh github-commits: {(project.GitHubCommitsChannelId.HasValue ? $"<#{project.GitHubCommitsChannelId.Value}>" : "`(chưa cấu hình)`")}\n" +
            $"- Kênh thông báo toàn cục: {(project.GlobalNotificationChannelId.HasValue ? $"<#{project.GlobalNotificationChannelId.Value}>" : "`(chưa cấu hình)`")}",
            ephemeral: true);
    }

    [SlashCommand("github-bind", "Gắn repo GitHub để theo dõi push (mặc định: mọi nhánh).")]
    public async Task GitHubBindAsync(string repository, string branch = "*", ITextChannel? notifyChannel = null)
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới cấu hình theo dõi GitHub.", ephemeral: true);
            return;
        }

        if (!GitHubTrackingService.TryNormalizeRepository(repository, out var normalizedRepo))
        {
            await RespondAsync(
                "Repo không hợp lệ. Dùng dạng `owner/repo-thật` hoặc URL GitHub.\n" +
                "> `owner/repo` chỉ là ví dụ, không phải tên thật.",
                ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var project = await _projectService.GetProjectByChannelAsync(Context.Channel.Id);
        if (project is null)
        {
            await FollowupAsync("Kênh này chưa gắn với dự án nào. Hãy dùng `/project setup` trước.", ephemeral: true);
            return;
        }

        var targetChannelId = notifyChannel?.Id
            ?? project.GitHubCommitsChannelId
            ?? Context.Guild.TextChannels.FirstOrDefault(x => x.Name.Contains("github-commits", StringComparison.OrdinalIgnoreCase))?.Id
            ?? Context.Channel.Id;

        await _projectService.SetGitHubCommitsChannelAsync(project.Id, targetChannelId);

        var branchInput = string.IsNullOrWhiteSpace(branch) ? "*" : branch.Trim();
        var isTrackAllBranches = GitHubTrackingService.IsTrackAllBranches(branchInput);

        var binding = await _gitHubTrackingService.UpsertBindingAsync(
            projectId: project.Id,
            repoFullName: normalizedRepo,
            branch: branchInput);
        _ = await _gitHubTrackingService.PrimeBindingCursorAsync(binding.Id);

        var branchDisplay = isTrackAllBranches
            ? "Mọi nhánh (`*`)"
            : $"`{binding.Branch}`";

        await FollowupAsync(
            $"Đã bật theo dõi push GitHub.\n" +
            $"- Dự án: `{project.Name}` (`{project.Id}`)\n" +
            $"- Repo game: `{normalizedRepo}`\n" +
            $"- Branch: {branchDisplay}\n" +
            (isTrackAllBranches ? "- Chế độ: tự mở rộng khi có nhánh mới\n" : string.Empty) +
            $"- Kênh thông báo: <#{targetChannelId}>",
            ephemeral: true);
    }

    [SlashCommand("github-unbind", "Ngừng theo dõi repo GitHub. Mặc định gỡ toàn bộ nhánh của repo.")]
    public async Task GitHubUnbindAsync(string repository, string branch = "*")
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới cấu hình theo dõi GitHub.", ephemeral: true);
            return;
        }

        if (!GitHubTrackingService.TryNormalizeRepository(repository, out var normalizedRepo))
        {
            await RespondAsync(
                "Repo không hợp lệ. Dùng dạng `owner/repo-thật` hoặc URL GitHub.\n" +
                "> `owner/repo` chỉ là ví dụ, không phải tên thật.",
                ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var project = await _projectService.GetProjectByChannelAsync(Context.Channel.Id);
        if (project is null)
        {
            await FollowupAsync("Kênh này chưa gắn với dự án nào. Hãy dùng `/project setup` trước.", ephemeral: true);
            return;
        }

        var branchInput = string.IsNullOrWhiteSpace(branch) ? "*" : branch.Trim();
        var isTrackAllBranches = GitHubTrackingService.IsTrackAllBranches(branchInput);

        var removed = await _gitHubTrackingService.RemoveBindingAsync(
            projectId: project.Id,
            repoFullName: normalizedRepo,
            branch: branchInput);

        var branchDisplay = isTrackAllBranches ? "*" : branchInput;

        await FollowupAsync(
            removed
                ? $"Đã ngừng theo dõi `{normalizedRepo}` branch `{branchDisplay}`."
                : $"Không tìm thấy cấu hình theo dõi cho `{normalizedRepo}` branch `{branchDisplay}`.",
            ephemeral: true);
    }

    [SlashCommand("github-list", "Xem danh sách repo GitHub đang được theo dõi của dự án hiện tại.")]
    public async Task GitHubListAsync()
    {
        await DeferAsync(ephemeral: true);
        var project = await _projectService.GetProjectByChannelAsync(Context.Channel.Id);
        if (project is null)
        {
            await FollowupAsync("Kênh này chưa gắn với dự án nào. Hãy dùng `/project setup` trước.", ephemeral: true);
            return;
        }

        var bindings = await _gitHubTrackingService.ListBindingsAsync(project.Id);
        if (bindings.Count == 0)
        {
            await FollowupAsync(
                $"Dự án `{project.Name}` chưa có repo GitHub nào đang theo dõi.",
                ephemeral: true);
            return;
        }

        var wildcardRepos = bindings
            .Where(x => x.Branch == "*")
            .Select(x => x.RepoFullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>();
        foreach (var wildcard in bindings.Where(x => x.Branch == "*").OrderBy(x => x.RepoFullName))
        {
            var expandedCount = bindings.Count(x =>
                x.RepoFullName.Equals(wildcard.RepoFullName, StringComparison.OrdinalIgnoreCase) &&
                x.Branch != "*");
            if (expandedCount == 0)
            {
                lines.Add($"- `{wildcard.RepoFullName}` • mọi nhánh (`*`) • đang đồng bộ danh sách nhánh");
            }
            else
            {
                lines.Add($"- `{wildcard.RepoFullName}` • mọi nhánh (`*`) • đã đồng bộ `{expandedCount}` nhánh");
            }
        }

        foreach (var single in bindings
                     .Where(x => x.Branch != "*" && !wildcardRepos.Contains(x.RepoFullName))
                     .OrderBy(x => x.RepoFullName)
                     .ThenBy(x => x.Branch))
        {
            lines.Add($"- `{single.RepoFullName}` • branch `{single.Branch}` • trạng thái: `{(single.IsEnabled ? "ON" : "OFF")}`");
        }

        await FollowupAsync(
            $"Danh sách repo theo dõi của dự án `{project.Name}`:\n" +
            string.Join('\n', lines),
            ephemeral: true);
    }

    [SlashCommand("github-sync", "Quét commit GitHub mới ngay lập tức cho dự án hiện tại.")]
    public async Task GitHubSyncAsync()
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới chạy được lệnh đồng bộ GitHub.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var project = await _projectService.GetProjectByChannelAsync(Context.Channel.Id);
        if (project is null)
        {
            await FollowupAsync("Kênh này chưa gắn với dự án nào. Hãy dùng `/project setup` trước.", ephemeral: true);
            return;
        }

        var notifications = await _gitHubTrackingService.PollProjectAsync(project.Id);
        await FollowupAsync(
            $"Đã quét GitHub cho dự án `{project.Name}`. Số thông báo push mới gửi: `{notifications}`.",
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
}

[Group("test", "Tiện ích kiểm thử nhắc việc")]
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

    [SlashCommand("standup-reminder", "Gửi nhắc báo cáo hằng ngày ngay để kiểm thử.")]
    public async Task TestStandupReminderAsync(int? projectId = null)
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể chạy lệnh kiểm thử nhắc việc.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var project = await ResolveProjectAsync(projectId);
        if (project is null)
        {
            await FollowupAsync("Không tìm thấy ngữ cảnh dự án.", ephemeral: true);
            return;
        }

        var result = await _projectService.OpenDailyStandupAsync(project.Id);
        if (!result.MessageId.HasValue)
        {
            await FollowupAsync("Không thể gửi nhắc báo cáo hằng ngày. Hãy kiểm tra cấu hình kênh báo cáo ngày.", ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"Đã gửi nhắc báo cáo hằng ngày cho dự án `{project.Name}` tại <#{project.StandupChannelId}>.",
            ephemeral: true);
    }

    [SlashCommand("overdue-reminder", "Gửi nhắc nhiệm vụ quá hạn vào kênh thông báo toàn cục để kiểm thử.")]
    public async Task TestOverdueReminderAsync(int? taskId = null, int overdueHours = 30, int? projectId = null)
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể chạy lệnh kiểm thử nhắc việc.", ephemeral: true);
            return;
        }

        overdueHours = Math.Clamp(overdueHours, 1, 240);
        await DeferAsync(ephemeral: true);

        var project = await ResolveProjectAsync(projectId);
        if (project is null)
        {
            await FollowupAsync("Không tìm thấy ngữ cảnh dự án.", ephemeral: true);
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
                    Name = "Chu Kỳ Kiểm Thử",
                    Goal = "Chu kỳ mô phỏng để kiểm thử nhắc việc",
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
                Title = "Nhiệm vụ quá hạn mô phỏng (kiểm thử)",
                Description = "Được tạo bởi /test overdue-reminder",
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
            : "`kênh thông báo toàn cục`";

        await FollowupAsync(
            $"Đã gửi nhắc quá hạn cho nhiệm vụ `#{task.Id}` tới {globalFeedText}.",
            ephemeral: true);
    }

    [SlashCommand("overdue-scan", "Quét quá hạn ngay và gửi nhắc việc lập tức.")]
    public async Task TestOverdueScanAsync(int? projectId = null)
    {
        if (!IsLeadOrAdmin())
        {
            await RespondAsync("Chỉ Trưởng nhóm/Quản trị mới có thể chạy lệnh kiểm thử nhắc việc.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var project = await ResolveProjectAsync(projectId);
        if (project is null)
        {
            await FollowupAsync("Không tìm thấy ngữ cảnh dự án.", ephemeral: true);
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

        await FollowupAsync($"Quét quá hạn hoàn tất. Đã gửi {tasks.Count} thông báo nhắc việc.", ephemeral: true);
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

public sealed record SprintDraftState(
    int ProjectId,
    ulong UserId,
    string Name,
    string Goal,
    DateTimeOffset CreatedAtUtc,
    DateTime? StartDateLocal,
    DateTime? EndDateLocal);

public sealed class AddBacklogModal : IModal
{
    public string Title => "Thêm Vào Tồn Đọng";

    [InputLabel("Tiêu đề nhiệm vụ")]
    [ModalTextInput("task_title", TextInputStyle.Short, maxLength: 200)]
    public string TaskTitle { get; set; } = string.Empty;

    [InputLabel("Điểm công việc")]
    [ModalTextInput("points", TextInputStyle.Short, placeholder: "1-13", maxLength: 3)]
    public string Points { get; set; } = "1";

    [InputLabel("Mô tả")]
    [ModalTextInput("description", TextInputStyle.Paragraph, maxLength: 1000)]
    public string? Description { get; set; }
}

public sealed class StartSprintModal : IModal
{
    public string Title => "Bắt Đầu Chu Kỳ";

    [InputLabel("Tên chu kỳ")]
    [ModalTextInput("sprint_name", TextInputStyle.Short, maxLength: 120)]
    public string Name { get; set; } = string.Empty;

    [InputLabel("Mục tiêu chu kỳ")]
    [RequiredInput(false)]
    [ModalTextInput("sprint_goal", TextInputStyle.Paragraph, maxLength: 500)]
    public string Goal { get; set; } = string.Empty;

    [InputLabel("Bắt đầu (UTC+7, tùy chọn)")]
    [RequiredInput(false)]
    [ModalTextInput("sprint_start_date", TextInputStyle.Short, placeholder: "Bỏ trống để chọn bằng menu", maxLength: 16)]
    public string StartDate { get; set; } = string.Empty;

    [InputLabel("Kết thúc (UTC+7, tùy chọn)")]
    [RequiredInput(false)]
    [ModalTextInput("sprint_end_date", TextInputStyle.Short, placeholder: "Bỏ trống để chọn bằng menu", maxLength: 16)]
    public string EndDate { get; set; } = string.Empty;
}

public sealed class ReportBugModal : IModal
{
    public string Title => "Báo Lỗi";

    [InputLabel("Tiêu đề lỗi")]
    [ModalTextInput("bug_title", TextInputStyle.Short, maxLength: 200)]
    public string BugTitle { get; set; } = string.Empty;

    [InputLabel("Mô tả")]
    [ModalTextInput("bug_desc", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Description { get; set; } = string.Empty;
}

public sealed class StandupReportModal : IModal
{
    public string Title => "Báo Cáo Hằng Ngày";

    [InputLabel("Hôm qua")]
    [ModalTextInput("standup_yesterday", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Yesterday { get; set; } = string.Empty;

    [InputLabel("Hôm nay")]
    [ModalTextInput("standup_today", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Today { get; set; } = string.Empty;

    [InputLabel("Vướng mắc")]
    [ModalTextInput("standup_blockers", TextInputStyle.Paragraph, maxLength: 1200)]
    public string Blockers { get; set; } = "Không có";
}




