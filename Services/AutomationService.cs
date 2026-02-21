using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;

namespace ProjectManagerBot.Services;

public sealed class AutomationService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    ProjectService projectService,
    NotificationService notificationService,
    StudioTimeService studioTime,
    ILogger<AutomationService> logger) : BackgroundService
{
    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly ProjectService _projectService = projectService;
    private readonly NotificationService _notificationService = notificationService;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly ILogger<AutomationService> _logger = logger;
    private DateTimeOffset _lastOverdueSweepLocal = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryRunStandupPromptAsync(stoppingToken);
                await TryRunOverdueTaskReminderAsync(stoppingToken);
                await TryRunSprintAutoCloseAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vòng lặp tự động hóa gặp lỗi");
            }
        }
    }

    private async Task TryRunStandupPromptAsync(CancellationToken cancellationToken)
    {
        var localNow = _studioTime.LocalNow;
        if (localNow.Hour != 9 || localNow.Minute != 0)
        {
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var projects = await db.Projects.ToListAsync(cancellationToken);

        foreach (var project in projects)
        {
            if (project.LastStandupDateLocal?.Date == _studioTime.LocalDate.Date)
            {
                continue;
            }

            var result = await _projectService.OpenDailyStandupAsync(project.Id, cancellationToken);
            if (result.MessageId.HasValue)
            {
                _logger.LogInformation(
                    "Đã gửi nhắc báo cáo ngày cho dự án {ProjectId} vào {LocalDate}",
                    project.Id,
                    result.LocalDate);
            }
        }
    }

    private async Task TryRunOverdueTaskReminderAsync(CancellationToken cancellationToken)
    {
        var localNow = _studioTime.LocalNow;
        if ((localNow - _lastOverdueSweepLocal).TotalMinutes < 30)
        {
            return;
        }

        _lastOverdueSweepLocal = localNow;

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var localDate = _studioTime.LocalDate;
        var overdueThresholdUtc = DateTimeOffset.UtcNow.AddHours(-24);

        var candidates = await db.TaskItems
            .Where(x => x.SprintId != null)
            .OrderBy(x => x.Id)
            .Take(1000)
            .ToListAsync(cancellationToken);

        var overdueTasks = candidates
            .Where(x =>
                x.Type == TaskItemType.Task &&
                x.Status != TaskItemStatus.Done &&
                x.CreatedAtUtc <= overdueThresholdUtc &&
                (!x.LastOverdueReminderDateLocal.HasValue || x.LastOverdueReminderDateLocal.Value < localDate))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(100)
            .ToList();

        if (overdueTasks.Count == 0)
        {
            return;
        }

        foreach (var task in overdueTasks)
        {
            var overdueBy = DateTimeOffset.UtcNow - task.CreatedAtUtc;
            await _notificationService.NotifyOverdueTaskAsync(task.ProjectId, task, overdueBy, cancellationToken);
            task.LastOverdueReminderDateLocal = localDate;
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Đã gửi {Count} thông báo nhắc quá hạn", overdueTasks.Count);
    }

    private async Task TryRunSprintAutoCloseAsync(CancellationToken cancellationToken)
    {
        var localNow = _studioTime.LocalNow;

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var activeSprints = await db.Sprints
            .Where(x => x.IsActive && x.EndDateLocal.HasValue)
            .OrderBy(x => x.ProjectId)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (activeSprints.Count == 0)
        {
            return;
        }

        foreach (var sprint in activeSprints)
        {
            var endLocal = sprint.EndDateLocal!.Value;
            // Backward-compatible: nếu mốc thời gian chỉ là ngày (00:00), coi hạn chót là cuối ngày.
            var effectiveEnd = endLocal.TimeOfDay == TimeSpan.Zero
                ? endLocal.Date.AddDays(1).AddTicks(-1)
                : endLocal;

            if (localNow.DateTime < effectiveEnd)
            {
                continue;
            }

            var sprintTasks = await db.TaskItems
                .Where(x => x.ProjectId == sprint.ProjectId && x.SprintId == sprint.Id)
                .ToListAsync(cancellationToken);

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

            await db.SaveChangesAsync(cancellationToken);

            await _projectService.RefreshDashboardMessageAsync(sprint.ProjectId, cancellationToken);
            await _notificationService.NotifySprintEndedAsync(
                sprint.ProjectId,
                actorDiscordId: 0,
                sprint,
                velocity,
                doneTasks.Count,
                unfinishedTasks.Count,
                cancellationToken);

            _logger.LogInformation(
                "Tự động đóng chu kỳ {SprintId} của dự án {ProjectId} tại {LocalNow}",
                sprint.Id,
                sprint.ProjectId,
                localNow);
        }
    }
}
