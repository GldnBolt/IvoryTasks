using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ivory.Core;

namespace Ivory.Sync;
public static class GraphMapping
{
    static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static TaskItem FromGraph(JsonElement e, TaskList list)
    {
        DateTimeOffset? Stamp(string key) => DateTimeOffset.TryParse(S(e, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
        string? body = null, bodyType = null;
        if (e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object) { body = S(b, "content"); bodyType = S(b, "contentType"); }
        DateOnly? due = null;
        if (e.TryGetProperty("dueDateTime", out var d) && d.ValueKind == JsonValueKind.Object && DateTime.TryParse(S(d, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) due = DateOnly.FromDateTime(date);
        DateTimeOffset? reminder = null;
        if (e.TryGetProperty("isReminderOn", out var on) && on.ValueKind == JsonValueKind.True && e.TryGetProperty("reminderDateTime", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            if (DateTime.TryParse(S(r, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(S(r, "timeZone") ?? "UTC");
                reminder = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            }
        }
        DateTimeOffset? completedAt = null;
        if (e.TryGetProperty("completedDateTime", out var c) && c.ValueKind == JsonValueKind.Object && DateTimeOffset.TryParse(S(c, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var completeTime)) completedAt = completeTime;
        return new TaskItem {
            Title = S(e, "title") ?? "Untitled task", Notes = body ?? "", NotesContentType = bodyType ?? "text", ListId = list.Id,
            RemoteId = S(e, "id") ?? throw new InvalidDataException("Graph task has no ID."), RemoteListId = list.RemoteId,
            Completed = S(e, "status") == "completed", CompletedAt = completedAt,
            Priority = S(e, "importance") switch { "high" => Priority.High, "low" => Priority.Low, _ => Priority.Normal },
            DueDate = due, ReminderDateTime = reminder, CreatedAt = Stamp("createdDateTime") ?? DateTimeOffset.UtcNow, UpdatedAt = Stamp("lastModifiedDateTime") ?? DateTimeOffset.UtcNow,
            RemoteModifiedAt = Stamp("lastModifiedDateTime"), ETag = S(e, "@odata.etag"),
            Recurrence = e.TryGetProperty("recurrence", out var recurrence) && recurrence.ValueKind == JsonValueKind.Object ? recurrence.Clone() : null,
            Source = TaskSource.MicrosoftToDo, SyncStatus = SyncStatus.Synced, LastSyncedAt = DateTimeOffset.UtcNow
        };
    }
    static object DateTimeZone(DateTimeOffset date) => new { dateTime = date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture), timeZone = "UTC" };
    public static object ToGraph(TaskItem task) => new {
        title = task.Title, body = new { content = task.Notes, contentType = task.NotesContentType }, importance = task.Priority.ToString().ToLowerInvariant(), status = task.Completed ? "completed" : "notStarted",
        dueDateTime = task.DueDate == null ? null : new { dateTime = task.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00", timeZone = "UTC" },
        isReminderOn = task.ReminderDateTime != null,
        reminderDateTime = task.ReminderDateTime is {} reminder ? DateTimeZone(reminder) : null,
        completedDateTime = task.Completed && task.CompletedAt is {} complete ? DateTimeZone(complete) : null,
        recurrence = task.Recurrence
    };
    public static JsonElement? CreateRecurrence(int kind, DateTime date)
    {
        if (kind == 0) return null;
        object pattern = kind switch {
            1 => new { type = "daily", interval = 1 },
            2 => new { type = "weekly", interval = 1, daysOfWeek = new[] { date.DayOfWeek.ToString().ToLowerInvariant() }, firstDayOfWeek = "monday" },
            _ => (object)new { type = "absoluteMonthly", interval = 1, dayOfMonth = date.Day }
        };
        return JsonSerializer.SerializeToElement(new { pattern, range = new { type = "noEnd", startDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } });
    }
}
