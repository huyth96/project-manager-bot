namespace ProjectManagerBot.Models;

public enum TaskEventType
{
    Created = 1,
    BacklogUpdated = 2,
    AddedToSprint = 3,
    Claimed = 4,
    Started = 5,
    Completed = 6,
    Assigned = 7,
    BugReported = 8,
    BugClaimed = 9,
    BugFixed = 10,
    ReturnedToBacklog = 11,
    Deleted = 12,
    SeededForTest = 13,
    BackfilledSnapshot = 14
}
