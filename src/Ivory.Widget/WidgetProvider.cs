using System.Text.Json;
using Ivory.Core;
using Ivory.Sync;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using Windows.System;

namespace Ivory.Widget;
public sealed class WidgetProvider : IWidgetProvider
{
    public static readonly ManualResetEvent Exit = new(false);
    readonly object gate = new();
    readonly Dictionary<string, (WidgetSize Size, bool Active)> widgets = new();
    readonly Timer timer;
    DateTimeOffset lastActive = DateTimeOffset.UtcNow;
    public WidgetProvider()
    {
        foreach (var info in WidgetManager.GetDefault().GetWidgetInfos()) widgets[info.WidgetContext.Id] = (info.WidgetContext.Size, false);
        timer = new Timer(_ => {
            lock (gate) {
                foreach (var (id, value) in widgets.Where(x => x.Value.Active).ToArray()) Update(id, value.Size);
                // The host can restart the COM server when it needs it; do not stay resident indefinitely.
                if (!widgets.Values.Any(x => x.Active) && DateTimeOffset.UtcNow - lastActive > TimeSpan.FromMinutes(10)) Exit.Set();
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
    void Update(string id, WidgetSize size, string? error = null)
    {
        try {
            var settings = AppSettings.Load(); var db = new TaskStore(settings.DatabasePath);
            var limit = size == WidgetSize.Small ? 1 : size == WidgetSize.Medium ? 3 : 6;
            var card = WidgetCard.Create(db.Tasks(), DateOnly.FromDateTime(DateTime.Now), limit, Path.GetFileNameWithoutExtension(settings.DatabasePath), settings.WidgetEnabled, settings.WidgetImportantOnly, settings.Profile == "demo", error);
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id) { Template = card, Data = "{}", IsPlaceholderContent = false });
        } catch (Exception e) {
            AppLog.Write("widget.update.failed", e);
            try { WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id) { Template = WidgetCard.Create([], DateOnly.FromDateTime(DateTime.Now), 0, "", false, false, false, "Tasks could not be loaded. Open Ivory to review the error."), Data = "{}", IsPlaceholderContent = false }); }
            catch (Exception fallbackError) { AppLog.Write("widget.fallback.failed", fallbackError); }
        }
    }
    public void CreateWidget(WidgetContext context) { lock (gate) { widgets[context.Id] = (context.Size, true); lastActive = DateTimeOffset.UtcNow; Update(context.Id, context.Size); } }
    public void DeleteWidget(string id, string state) { lock (gate) { widgets.Remove(id); if (widgets.Count == 0) Exit.Set(); } }
    public void Activate(WidgetContext context) => CreateWidget(context);
    public void Deactivate(string id) { lock (gate) { if (widgets.TryGetValue(id, out var value)) widgets[id] = (value.Size, false); lastActive = DateTimeOffset.UtcNow; } }
    public void OnWidgetContextChanged(WidgetContextChangedArgs args) => CreateWidget(args.WidgetContext);
    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        // Copy callback arguments while valid; no WinRT callback objects escape into asynchronous work.
        var id = args.WidgetContext.Id; var size = args.WidgetContext.Size; var verb = args.Verb; var data = args.Data;
        lock (gate)
        {
            try {
                if (verb == "complete") {
                    var settings = AppSettings.Load(); if (!settings.WidgetEnabled) return;
                    using var json = JsonDocument.Parse(data); var root = json.RootElement;
                    if (root.GetProperty("profile").GetString() != Path.GetFileNameWithoutExtension(settings.DatabasePath)) throw new InvalidOperationException("Account changed. Refresh the widget first.");
                    var store = new TaskStore(settings.DatabasePath);
                    var task = store.Find(root.GetProperty("id").GetGuid());
                    if (task != null && !task.Deleted && !task.Completed) {
                        if (task.Revision != root.GetProperty("revision").GetInt64()) throw new InvalidOperationException("Task changed. Refresh before completing it.");
                        store.Complete(task, true);
                    }
                } else if (verb == "refresh") { _ = LaunchSync(); }
                Update(id, size);
            } catch (Exception e) { AppLog.Write("widget.action.failed", e); Update(id, size, "Task changed or could not be saved. Open Ivory to review it."); }
        }
    }
    static async Task LaunchSync()
    {
        try { await Launcher.LaunchUriAsync(new Uri("ivorytasks://sync")); } catch (Exception e) { AppLog.Write("widget.launch.failed", e); }
    }
}
