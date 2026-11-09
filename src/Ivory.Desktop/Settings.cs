using Windows.ApplicationModel;
using System.Net.Http;
namespace Ivory.Desktop;
public sealed partial class MainWindow
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };
    public async Task SyncNow()
    {
        if (settings.Profile == "demo") { Report("Demo data stays local. Open Settings to connect Microsoft To Do."); return; }
        if (auth == null) { Report("Configure your Microsoft client ID and connect in Settings."); return; }
        if (!await operation.WaitAsync(0)) return;
        syncing = true; status.Text = "↻ Synchronizing with Microsoft To Do…";
        try
        {
            sync = new SyncEngine(store, new MicrosoftTodoService(auth, http), settings.DatabasePath + ".sync.lock");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await sync.Run(timeout.Token); lastSync = DateTimeOffset.UtcNow;
            RebuildSidebar(); Refresh(); windows.CheckReminders(store);
        }
        catch (HttpRequestException e) { AppLog.Write("sync.offline", e); Report("Unable to reach Microsoft To Do. Your local tasks are safe and pending changes will retry automatically."); status.Text = "Offline · Local changes are saved"; }
        catch (OperationCanceledException e) { AppLog.Write("sync.timeout", e); Report("Synchronization timed out. Your local changes are safe."); }
        finally { syncing = false; operation.Release(); Refresh(); }
    }
    public async Task ShowSettings()
    {
        ShowMain();
        var panel = new StackPanel { Spacing = 16, Width = 440 };
        panel.Children.Add(Text("Samsung / Microsoft Sync", 23));
        panel.Children.Add(Text(settings.Profile == "demo" ? "○ Not connected · Local demo" : "● Account linked · Sign-in is checked during sync"));
        panel.Children.Add(Text("Samsung Reminder → Microsoft To Do → Ivory\n\nSamsung Reminder tasks appear here through Microsoft To Do synchronization. Enable ‘Sync with Microsoft To Do’ in Samsung Reminder and use the same Microsoft account here. Graph cannot reliably identify which tasks originated on Samsung.", 13));
        var clientId = new TextBox { Header = "Microsoft Entra Application (client) ID", Text = settings.ClientId, PlaceholderText = "Public client GUID — see README" };
        var tenant = new TextBox { Header = "Tenant", Text = settings.TenantId };
        panel.Children.Add(clientId); panel.Children.Add(tenant);
        var accountStatus = Text("Authentication uses your browser. No client secret is needed.", 12); panel.Children.Add(accountStatus);
        var connect = AsyncButton("Connect Microsoft To Do", async () => {
            if (!await operation.WaitAsync(0)) throw new InvalidOperationException("Wait for the current synchronization to finish.");
            try {
                var candidate = new AuthService(clientId.Text.Trim(), tenant.Text.Trim());
                var profile = await candidate.Connect();
                settings.ClientId = clientId.Text.Trim(); settings.TenantId = tenant.Text.Trim(); settings.Profile = profile; settings.Save();
                auth = candidate; store = new TaskStore(settings.DatabasePath); lastSync = default; CloseDetail(); listFilter = null;
                accountStatus.Text = "Connected. Close Settings and select Sync now to load your lists."; RebuildSidebar(); Refresh();
            } finally { operation.Release(); }
        }); panel.Children.Add(connect);
        panel.Children.Add(AsyncButton("Disconnect · return to local demo", async () => {
            if (!await operation.WaitAsync(0)) throw new InvalidOperationException("Wait for synchronization to finish.");
            try {
                if (auth != null) await auth.Disconnect();
                windows.CancelReminders(); settings.Profile = "demo"; settings.Save(); store = new TaskStore(settings.DatabasePath); store.SeedDemo(); lastSync = default; listFilter = null; CloseDetail();
                accountStatus.Text = "Disconnected. Microsoft data remains cached separately on this PC."; RebuildSidebar(); Refresh();
            } finally { operation.Release(); }
        }));
        panel.Children.Add(Text("Appearance", 20));
        var theme = new ComboBox { Header = "Theme", ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = settings.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.SelectionChanged += (_, _) => { settings.Theme = theme.SelectedItem?.ToString() ?? "System"; settings.Save(); ApplyTheme(); Refresh(); }; panel.Children.Add(theme);
        var interval = new NumberBox { Header = "Auto-sync interval (minutes)", Value = settings.SyncMinutes, Minimum = 1, Maximum = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        interval.ValueChanged += (_, _) => { settings.SyncMinutes = double.IsNaN(interval.Value) ? 5 : Math.Clamp((int)interval.Value, 1, 120); settings.Save(); }; panel.Children.Add(interval);
        panel.Children.Add(Text("Windows integration", 20));
        var notification = new ToggleSwitch { Header = "Reminder notifications", IsOn = settings.NotificationsEnabled };
        notification.Toggled += (_, _) => { settings.NotificationsEnabled = notification.IsOn; settings.Save(); windows.CheckReminders(store); }; panel.Children.Add(notification);
        panel.Children.Add(Text("Future reminders are scheduled with Windows. Keep Ivory running to receive newly synchronized reminders. Windows notification and Do Not Disturb settings apply.", 12));
        var tray = new ToggleSwitch { Header = "Keep running in the system tray", IsOn = settings.TrayEnabled };
        tray.Toggled += (_, _) => { settings.TrayEnabled = tray.IsOn; settings.Save(); windows.UpdateTray(); }; panel.Children.Add(tray);
        var startup = new ToggleSwitch { Header = "Start with Windows", IsOn = await WindowsServices.StartupEnabled() };
        startup.Toggled += async (_, _) => await Safe(async () => { var enabled = await WindowsServices.SetStartup(startup.IsOn); if (enabled != startup.IsOn) { startup.IsOn = enabled; Report("Windows has disabled startup. Change it in Settings → Apps → Startup."); } }); panel.Children.Add(startup);
        var widget = new ToggleSwitch { Header = "Show task data in the Windows widget", IsOn = settings.WidgetEnabled };
        widget.Toggled += (_, _) => { settings.WidgetEnabled = widget.IsOn; settings.Save(); }; panel.Children.Add(widget);
        var important = new ToggleSwitch { Header = "Widget: important tasks only", IsOn = settings.WidgetImportantOnly };
        important.Toggled += (_, _) => { settings.WidgetImportantOnly = important.IsOn; settings.Save(); }; panel.Children.Add(important);
        panel.Children.Add(Text("Pin ‘Ivory · My tasks’ from Windows Widgets (Win + W → Add widgets). Install the MSIX first. The widget reads this account’s local database; its refresh action opens Ivory to synchronize.", 12));
        var conflicts = store.Conflicts();
        if (conflicts.Count > 0)
        {
            panel.Children.Add(Text("Resolve synchronization conflicts", 20));
            foreach (var conflict in conflicts)
            {
                var group = new StackPanel { Spacing = 8 };
                group.Children.Add(Text(conflict.Reason, 12));
                group.Children.Add(Text($"Local: {conflict.Local.Title}\n{conflict.Local.Notes}\n\nMicrosoft: {conflict.Remote?.Title ?? "Deleted / unknown create outcome"}\n{conflict.Remote?.Notes}", 13));
                group.Children.Add(Button("Keep local version", () => { store.Resolve(conflict, true); group.IsHitTestVisible = false; group.Opacity = .5; Refresh(); }));
                group.Children.Add(Button("Use Microsoft version / discard local", () => { store.Resolve(conflict, false); group.IsHitTestVisible = false; group.Opacity = .5; Refresh(); }));
                panel.Children.Add(group);
            }
        }
        foreach (var list in store.Lists().Where(x => store.Get("list-create:" + x.Id) == "uncertain"))
        {
            panel.Children.Add(Text($"List ‘{list.Name}’ has an uncertain creation result. Choose the matching Microsoft list after checking To Do; this avoids creating duplicate lists.", 12));
            var matches = store.Lists().Where(x => x.RemoteId != null).ToList();
            var choice = new ComboBox { ItemsSource = matches, DisplayMemberPath = "Name", PlaceholderText = "Select existing Microsoft list", HorizontalAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(choice);
            panel.Children.Add(Button("Recover tasks into selected list", () => {
                if (choice.SelectedItem is not TaskList target) throw new InvalidOperationException("Select the Microsoft list first.");
                foreach (var task in store.Tasks().Where(x => x.ListId == list.Id)) store.Save(task with { ListId = target.Id, RemoteListId = target.RemoteId }, task.Revision);
                store.SaveList(list with { RemoteMissing = true }); store.Set("list-create:" + list.Id, "resolved"); RebuildSidebar(); Refresh();
            }));
            panel.Children.Add(Button("Confirmed absent: retry list creation", () => store.Set("list-create:" + list.Id, "")));
        }
        var dialog = new ContentDialog { Title = "Make Ivory yours", Content = new ScrollViewer { Content = panel, MaxHeight = 580 }, CloseButtonText = "Close", XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme };
        await dialog.ShowAsync();
    }
}
