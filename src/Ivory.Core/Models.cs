using System.Text.Json;

namespace Ivory.Core;

public enum Priority { Low, Normal, High }
public enum TaskSource { Local, MicrosoftToDo, SamsungSynced }
public enum SyncStatus { Local, Pending, Synced, Conflict, Uploading }
public enum TaskFilter { All, Today, Upcoming, Overdue, Completed, Important }
public sealed record TaskItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public string Notes { get; init; } = "";
    public string NotesContentType { get; init; } = "text";
    public Guid ListId { get; init; }
    public bool Completed { get; init; }
    public Priority Priority { get; init; } = Priority.Normal;
    public DateOnly? DueDate { get; init; }
    public DateTimeOffset? ReminderDateTime { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    // Graph patternedRecurrence retained as JSON to preserve patterns the editor cannot author.
    public JsonElement? Recurrence { get; init; }
    public TaskSource Source { get; init; }
    public string? RemoteId { get; init; }
    public string? RemoteListId { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? RemoteModifiedAt { get; init; }
    public SyncStatus SyncStatus { get; init; }
    public DateTimeOffset? LastSyncedAt { get; init; }
    public bool Deleted { get; init; }
    public long Revision { get; init; }
    public string DueLabel => DueDate?.ToString("ddd, MMM d") ?? "No due date";
    public string PriorityLabel => Priority == Priority.High ? "Important" : Priority.ToString();
}
public sealed record TaskList(Guid Id, string Name, string? RemoteId = null, bool RemoteMissing = false);
public sealed record Conflict(Guid Id, Guid TaskId, string Reason, TaskItem Local, TaskItem? Remote, DateTimeOffset CreatedAt);
public static class TaskQuery
{
    public static IEnumerable<TaskItem> Apply(IEnumerable<TaskItem> items, TaskFilter filter, DateOnly today, string search = "", Guid? list = null, IEnumerable<TaskList>? lists = null)
    {
        var names = (lists ?? []).ToDictionary(x => x.Id, x => x.Name);
        return items.Where(x => !x.Deleted && (list == null || x.ListId == list))
            .Where(x => filter switch {
                TaskFilter.Completed => x.Completed,
                TaskFilter.Today => !x.Completed && x.DueDate == today,
                TaskFilter.Upcoming => !x.Completed && x.DueDate > today,
                TaskFilter.Overdue => !x.Completed && x.DueDate < today,
                TaskFilter.Important => !x.Completed && x.Priority == Priority.High,
                _ => !x.Completed })
            .Where(x => string.IsNullOrWhiteSpace(search) || $"{x.Title}\n{x.Notes}\n{names.GetValueOrDefault(x.ListId)}".Contains(search, StringComparison.CurrentCultureIgnoreCase));
    }
}
