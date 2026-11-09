using System.Security;
using System.Security.Cryptography;
using System.Text;
using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Forms = System.Windows.Forms;

namespace Ivory.Desktop;
public sealed class WindowsServices : IDisposable
{
    readonly MainWindow window;
    readonly AppSettings settings;
    Forms.NotifyIcon? tray;
    string? lastSchedule;
    bool notificationFailureReported;
    public WindowsServices(MainWindow window, AppSettings settings) { this.window = window; this.settings = settings; UpdateTray(); CheckReminders(new TaskStore(settings.DatabasePath)); }
    public void UpdateTray()
    {
        tray?.Dispose(); tray = null;
        if (!settings.TrayEnabled) return;
        var menu = new Forms.ContextMenuStrip();
        void Item(string title, Action action) => menu.Items.Add(title, null, (_, _) => window.DispatcherQueue.TryEnqueue(() => _ = window.Safe(() => { action(); return Task.CompletedTask; })));
        Item("Open", window.ShowMain); Item("Quick Add", window.QuickAdd); Item("Today's tasks", window.ShowToday);
        Item("Sync now", () => _ = window.Safe(window.SyncNow)); Item("Settings", () => _ = window.Safe(window.ShowSettings)); menu.Items.Add(new Forms.ToolStripSeparator()); Item("Quit", window.Quit);
        tray = new Forms.NotifyIcon { Text = "Ivory Tasks", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => window.DispatcherQueue.TryEnqueue(window.ShowMain);
    }
    static bool Packaged { get { try { _ = Package.Current.Id; return true; } catch { return false; } } }
    public static async Task<bool> StartupEnabled()
    {
        if (!Packaged) return false;
        return (await StartupTask.GetAsync("IvoryStartup")).State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }
    public static async Task<bool> SetStartup(bool enabled)
    {
        if (!Packaged) throw new InvalidOperationException("Install the MSIX package to enable Start with Windows.");
        var task = await StartupTask.GetAsync("IvoryStartup"); if (!enabled) { task.Disable(); return false; }
        return await task.RequestEnableAsync() is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }
    public void CancelReminders()
    {
        if (!Packaged) return;
        var notifier = ToastNotificationManager.CreateToastNotifier(); foreach (var item in notifier.GetScheduledToastNotifications()) notifier.RemoveFromSchedule(item);
        lastSchedule = null;
    }
    public void CheckReminders(TaskStore store)
    {
        if (!Packaged) return;
        try
        {
            var tasks = store.Tasks().Where(x => !x.Deleted && !x.Completed && x.ReminderDateTime > DateTimeOffset.Now).OrderBy(x => x.ReminderDateTime).Take(64).ToList();
            var fingerprint = settings.Profile + settings.NotificationsEnabled + string.Join("|", tasks.Select(x => $"{x.Id}:{x.Revision}:{x.ReminderDateTime:O}"));
            if (fingerprint == lastSchedule) return;
            var notifier = ToastNotificationManager.CreateToastNotifier();
            var existing = notifier.GetScheduledToastNotifications().ToList();
            var desired = settings.NotificationsEnabled ? tasks : new List<TaskItem>();
            string Tag(TaskItem t) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Profile}:{t.Id}:{t.Revision}:{t.ReminderDateTime:O}")))[..16];
            foreach (var old in existing.Where(x => !desired.Any(t => Tag(t) == x.Tag))) notifier.RemoveFromSchedule(old);
            foreach (var task in desired.Where(t => t.ReminderDateTime > DateTimeOffset.Now.AddSeconds(1) && !existing.Any(x => x.Tag == Tag(t))))
            {
                var xml = new XmlDocument(); xml.LoadXml($"<toast activationType='protocol' launch='ivorytasks://task/{task.Id}'><visual><binding template='ToastGeneric'><text>Ivory · Reminder</text><text>{SecurityElement.Escape(task.Title)}</text></binding></visual></toast>");
                notifier.AddToSchedule(new ScheduledToastNotification(xml, task.ReminderDateTime!.Value) { Tag = Tag(task), Group = "reminders" });
            }
            lastSchedule = fingerprint;
        }
        catch (Exception e) { AppLog.Write("notifications.failed", e); if (!notificationFailureReported) { window.Report("Windows could not schedule reminder notifications. Check Windows notification settings. Your tasks are saved."); notificationFailureReported = true; } }
    }
    public void Dispose() { tray?.Dispose(); }
}
