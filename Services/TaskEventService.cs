using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;

namespace ProjectManagerBot.Services;

public sealed class TaskEventService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    StudioTimeService studioTime,
    ILogger<TaskEventService> logger)
{
    private const int MaxSummaryLength = 500;
    private const int MaxSourceLength = 64;
    private const int MaxDescriptionLength = 1200;

    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly StudioTimeService _studioTime = studioTime;
    private readonly ILogger<TaskEventService> _logger = logger;

    public async Task RecordCreatedAsync(
        TaskItem task,
        ulong? actorDiscordId,
        TaskEventType eventType,
        string source,
        string? summary = null,
        CancellationToken cancellationToken = default)
    {
        var after = TaskEventSnapshot.FromTask(task);
        await PersistAsync(
            [BuildEvent(null, after, actorDiscordId, eventType, source, summary)],
            cancellationToken);
    }

    public async Task RecordUpdatedAsync(
        TaskEventSnapshot before,
        TaskItem afterTask,
        ulong? actorDiscordId,
        TaskEventType eventType,
        string source,
        string? summary = null,
        CancellationToken cancellationToken = default)
    {
        await RecordUpdatedAsync(
            before,
            TaskEventSnapshot.FromTask(afterTask),
            actorDiscordId,
            eventType,
            source,
            summary,
            cancellationToken);
    }

    public async Task RecordUpdatedAsync(
        TaskEventSnapshot before,
        TaskEventSnapshot after,
        ulong? actorDiscordId,
        TaskEventType eventType,
        string source,
        string? summary = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasMeaningfulDifference(before, after) && string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        await PersistAsync(
            [BuildEvent(before, after, actorDiscordId, eventType, source, summary)],
            cancellationToken);
    }

    public async Task RecordUpdatedRangeAsync(
        IEnumerable<TaskEventChange> changes,
        ulong? actorDiscordId,
        TaskEventType eventType,
        string source,
        CancellationToken cancellationToken = default)
    {
        var events = changes
            .Where(change => HasMeaningfulDifference(change.Before, change.After))
            .Select(change => BuildEvent(change.Before, change.After, actorDiscordId, eventType, source, summary: null))
            .ToList();

        if (events.Count == 0)
        {
            return;
        }

        await PersistAsync(events, cancellationToken);
    }

    public async Task RecordDeletedAsync(
        TaskEventSnapshot task,
        ulong? actorDiscordId,
        string source,
        string? summary = null,
        CancellationToken cancellationToken = default)
    {
        await PersistAsync(
            [BuildEvent(task, null, actorDiscordId, TaskEventType.Deleted, source, summary)],
            cancellationToken);
    }

    private async Task PersistAsync(IReadOnlyCollection<TaskEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            db.TaskEvents.AddRange(events);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong the luu {Count} task event vao lich su", events.Count);
        }
    }

    private TaskEvent BuildEvent(
        TaskEventSnapshot? before,
        TaskEventSnapshot? after,
        ulong? actorDiscordId,
        TaskEventType eventType,
        string source,
        string? summary)
    {
        var snapshot = after ?? before ?? throw new InvalidOperationException("Task event snapshot is required.");
        return new TaskEvent
        {
            ProjectId = snapshot.ProjectId,
            TaskItemId = snapshot.TaskItemId,
            TaskType = snapshot.TaskType,
            EventType = eventType,
            ActorDiscordId = actorDiscordId,
            OccurredAtUtc = DateTime.UtcNow,
            LocalDate = _studioTime.LocalDate,
            TitleSnapshot = snapshot.Title,
            DescriptionSnapshot = Trim(snapshot.Description, MaxDescriptionLength),
            FromStatus = before?.Status,
            ToStatus = after?.Status,
            FromAssigneeId = before?.AssigneeId,
            ToAssigneeId = after?.AssigneeId,
            FromSprintId = before?.SprintId,
            ToSprintId = after?.SprintId,
            FromPoints = before?.Points,
            ToPoints = after?.Points,
            Summary = Trim(summary ?? BuildSummary(eventType, before, after), MaxSummaryLength),
            Source = Trim(source, MaxSourceLength)
        };
    }

    private static bool HasMeaningfulDifference(TaskEventSnapshot before, TaskEventSnapshot after)
    {
        return before.ProjectId != after.ProjectId ||
               before.TaskType != after.TaskType ||
               before.Status != after.Status ||
               before.AssigneeId != after.AssigneeId ||
               before.SprintId != after.SprintId ||
               before.Points != after.Points ||
               !string.Equals(before.Title, after.Title, StringComparison.Ordinal) ||
               !string.Equals(before.Description, after.Description, StringComparison.Ordinal);
    }

    private static string BuildSummary(
        TaskEventType eventType,
        TaskEventSnapshot? before,
        TaskEventSnapshot? after)
    {
        var title = after?.Title ?? before?.Title ?? "task";
        return eventType switch
        {
            TaskEventType.Created => $"Tao task {title}",
            TaskEventType.BacklogUpdated => $"Cap nhat backlog {title}",
            TaskEventType.AddedToSprint => $"Them {title} vao sprint",
            TaskEventType.Claimed => $"Nhan {title}",
            TaskEventType.Started => $"Bat dau {title}",
            TaskEventType.Completed => $"Hoan thanh {title}",
            TaskEventType.Assigned => $"Giao {title}",
            TaskEventType.BugReported => $"Bao bug {title}",
            TaskEventType.BugClaimed => $"Nhan bug {title}",
            TaskEventType.BugFixed => $"Dong bug {title}",
            TaskEventType.ReturnedToBacklog => $"Tra {title} ve backlog",
            TaskEventType.Deleted => $"Xoa {title}",
            TaskEventType.SeededForTest => $"Cap nhat task test {title}",
            TaskEventType.BackfilledSnapshot => $"Backfill snapshot {title}",
            _ => $"Cap nhat {title}"
        };
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = value.Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength];
    }
}

public readonly record struct TaskEventSnapshot(
    int ProjectId,
    int TaskItemId,
    TaskItemType TaskType,
    TaskItemStatus Status,
    string Title,
    string? Description,
    int Points,
    ulong? AssigneeId,
    int? SprintId)
{
    public static TaskEventSnapshot FromTask(TaskItem task)
    {
        return new TaskEventSnapshot(
            ProjectId: task.ProjectId,
            TaskItemId: task.Id,
            TaskType: task.Type,
            Status: task.Status,
            Title: task.Title,
            Description: task.Description,
            Points: task.Points,
            AssigneeId: task.AssigneeId,
            SprintId: task.SprintId);
    }
}

public readonly record struct TaskEventChange(TaskEventSnapshot Before, TaskEventSnapshot After);
