using System.Text.Json;
namespace Ivory.Core;
public static class WidgetCard
{
    public static string Create(IEnumerable<TaskItem> all, DateOnly today, int limit, string profile, bool enabled, bool importantOnly, bool demo, string? error = null)
    {
        var tasks = all.Where(x => !x.Completed && !x.Deleted).ToList();
        var show = tasks.Where(x => !importantOnly || x.Priority == Priority.High).OrderBy(x => x.DueDate < today ? 0 : x.DueDate == today ? 1 : x.Priority == Priority.High ? 2 : 3).ThenBy(x => x.DueDate ?? DateOnly.MaxValue).ThenByDescending(x => x.Priority).Take(limit).ToList();
        var body = new List<object> {
            new { type = "TextBlock", text = "ivory. · My tasks", weight = "Bolder", size = "Medium", wrap = true },
            new { type = "TextBlock", text = error ?? (!enabled ? "Task display is disabled in Ivory Settings." : $"Today · {tasks.Count(x => x.DueDate == today)}   |   Overdue · {tasks.Count(x => x.DueDate < today)}   |   Important · {tasks.Count(x => x.Priority == Priority.High)}"), wrap = true, isSubtle = true, spacing = "Small" }
        };
        if (enabled && error == null)
        {
            if (demo) body.Add(new { type = "TextBlock", text = "LOCAL DEMO · Separate from Microsoft", size = "Small", wrap = true, isSubtle = true });
            foreach (var task in show)
            {
                body.Add(new { type = "ColumnSet", spacing = "Small", columns = new object[] {
                    new { type = "Column", width = "stretch", items = new object[] {
                        new { type = "TextBlock", text = (task.Priority == Priority.High ? "★ " : "") + task.Title, wrap = true, maxLines = 2 },
                        new { type = "TextBlock", text = (task.DueDate < today ? "Overdue · " : "") + task.DueLabel, isSubtle = true, size = "Small", spacing = "None" }
                    }, selectAction = new { type = "Action.OpenUrl", url = $"ivorytasks://task/{task.Id}" } },
                    new { type = "Column", width = "auto", items = new object[] {
                        new { type = "ActionSet", actions = new object[] { new { type = "Action.Execute", title = "✓", tooltip = "Complete " + task.Title, verb = "complete", data = new { id = task.Id, revision = task.Revision, profile }, fallback = new { type = "Action.OpenUrl", url = $"ivorytasks://task/{task.Id}" } } } }
                    } }
                } });
            }
            if (show.Count == 0) body.Add(new { type = "TextBlock", text = "A little breathing room. No tasks to show.", wrap = true });
        }
        return JsonSerializer.Serialize(new {
            type = "AdaptiveCard", version = "1.5", body,
            actions = new object[] { new { type = "Action.OpenUrl", title = "Open Ivory", url = "ivorytasks://open" }, new { type = "Action.Execute", title = "Refresh", verb = "refresh", fallback = new { type = "Action.OpenUrl", url = "ivorytasks://sync" } } }
        });
    }
}
