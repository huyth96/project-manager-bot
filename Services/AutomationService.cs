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
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automation loop failed");
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
                    "Posted standup prompt for project {ProjectId} at {LocalDate}",
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

        _logger.LogInformation("Sent {Count} overdue task reminder(s)", overdueTasks.Count);
    }
}
