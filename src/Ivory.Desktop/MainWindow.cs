using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Globalization;

namespace Ivory.Desktop;
public sealed partial class MainWindow : Window
{
    TaskStore store;
    readonly AppSettings settings;
    AuthService? auth;
    SyncEngine? sync;
    TaskFilter filter = TaskFilter.Today;
    Guid? listFilter;
    TaskItem? selected;
    readonly Grid root = new();
    readonly StackPanel sidebar = new() { Spacing = 6, Padding = new Thickness(20, 28, 20, 20) };
    readonly StackPanel rows = new() { Spacing = 8 };
    readonly StackPanel detail = new() { Spacing = 16, Padding = new Thickness(24) };
    ScrollViewer? detailScroll;
    readonly TextBlock heading = new() { FontSize = 36, FontFamily = new FontFamily("Georgia"), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock subtitle = new() { FontSize = 14, Opacity = .7 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly TextBox quick = new() { PlaceholderText = "What needs to be done?", MinHeight = 46, MaxLength = 255 };
    readonly TextBox search = new() { PlaceholderText = "Search tasks, notes, lists", Width = 280, MaxLength = 500 };
    readonly ComboBox sort = new() { Width = 150, ItemsSource = new[] { "Due date", "Priority", "Recently changed", "Title" }, SelectedIndex = 0 };
    readonly InfoBar message = new() { IsClosable = true, Severity = InfoBarSeverity.Informational };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    readonly WindowsServices windows;
    DateTimeOffset lastSync;
    bool syncing;
    bool quitting;
    string? renderedState;
    readonly SemaphoreSlim operation = new(1, 1);
    public MainWindow()
    {
        Title = "Ivory · A little more clarity";
        settings = AppSettings.Load();
        store = new TaskStore(settings.DatabasePath); if (settings.Profile == "demo") store.SeedDemo();
        if (!string.IsNullOrWhiteSpace(settings.ClientId)) auth = new AuthService(settings.ClientId, settings.TenantId);
        Content = root;
        AppWindow.Resize(new SizeInt32(1240, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 760;
        BuildLayout(); ApplyTheme(); RebuildSidebar(); Refresh();
        windows = new WindowsServices(this, settings);
        AppWindow.Closing += (_, e) => { if (!quitting && settings.TrayEnabled) { e.Cancel = true; AppWindow.Hide(); } };
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(30);
        Closed += (_, _) => { windows.Dispose(); timer.Stop(); };
        timer.Tick += async (_, _) => { await Safe(async () => { windows.CheckReminders(store); Refresh(); if (settings.Profile != "demo" && DateTimeOffset.UtcNow - lastSync > TimeSpan.FromMinutes(settings.SyncMinutes)) await SyncNow(); }); };
        timer.Start();
        root.Loaded += async (_, _) => { await Safe(async () => { if (auth != null && settings.Profile != "demo") await SyncNow(); }); };
    }
    static SolidColorBrush Brush(string hex)
    {
        return new SolidColorBrush(Color.FromArgb(255, byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber), byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber), byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber)));
    }
    bool Dark => root.ActualTheme == ElementTheme.Dark;
    void ApplyTheme()
    {
        root.RequestedTheme = settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        root.Background = Brush(Dark ? "#20201F" : "#FAF8F3");
        sidebar.Background = Brush(Dark ? "#292825" : "#F0ECE3");
        detail.Background = Brush(Dark ? "#292825" : "#F0ECE3");
    }
    void BuildLayout()
    {
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        root.Children.Add(new ScrollViewer { Content = sidebar, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var main = new Grid { Padding = new Thickness(38, 28, 38, 24), RowSpacing = 22 };
        for (var i = 0; i < 5; i++) main.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Grid.SetColumn(main, 1); root.Children.Add(main);
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new TextBlock { Text = "YOUR SPACE, SIMPLIFIED", FontSize = 11, CharacterSpacing = 180, VerticalAlignment = VerticalAlignment.Center, Opacity = .65 };
        top.Children.Add(mark); Grid.SetColumn(search, 1); top.Children.Add(search); main.Children.Add(top);
        search.TextChanged += (_, _) => Refresh();
        var intro = new StackPanel { Spacing = 10 }; intro.Children.Add(heading); intro.Children.Add(subtitle); Grid.SetRow(intro, 1); main.Children.Add(intro);
        var tools = new Grid { ColumnSpacing = 12 }; tools.ColumnDefinitions.Add(new ColumnDefinition()); tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.Children.Add(quick); Grid.SetColumn(sort, 1); tools.Children.Add(sort); Grid.SetRow(tools, 2); main.Children.Add(tools);
        quick.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; await Safe(AddQuick); } };
        sort.SelectionChanged += (_, _) => Refresh();
        var scroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 3); main.Children.Add(scroll);
        var footer = new StackPanel { Spacing = 8 }; footer.Children.Add(message); footer.Children.Add(status); Grid.SetRow(footer, 4); main.Children.Add(footer);
        detailScroll = new ScrollViewer { Content = detail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed }; Grid.SetColumn(detailScroll, 2); root.Children.Add(detailScroll);
        root.ActualThemeChanged += (_, _) => ApplyTheme();
        Shortcut(VirtualKey.N, VirtualKeyModifiers.Control, () => quick.Focus(FocusState.Keyboard));
        Shortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => search.Focus(FocusState.Keyboard));
        Shortcut(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = Safe(SyncNow));
        Shortcut(VirtualKey.Escape, VirtualKeyModifiers.None, CloseDetail);
        // Space on the focused task button opens the details; Space on its native checkbox toggles completion.
        root.SizeChanged += (_, _) => { if (selected != null) LayoutDetail(); search.Width = root.ActualWidth < 1000 ? 200 : 280; };
    }
    void Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers }; shortcut.Invoked += (_, e) => { action(); e.Handled = true; }; root.KeyboardAccelerators.Add(shortcut);
    }
    static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    Button Button(string text, Action action)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 38 };
        b.Click += async (_, _) => await Safe(() => { action(); return Task.CompletedTask; }); return b;
    }
    Button AsyncButton(string text, Func<Task> action)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 38 };
        b.Click += async (_, _) => { b.IsEnabled = false; try { await Safe(action); } finally { b.IsEnabled = true; } }; return b;
    }
    void RebuildSidebar()
    {
        sidebar.Children.Clear();
        sidebar.Children.Add(new TextBlock { Text = "ivory.", FontFamily = new FontFamily("Georgia"), FontSize = 38, Margin = new Thickness(8, 0, 0, 2) });
        sidebar.Children.Add(new TextBlock { Text = settings.Profile == "demo" ? "LOCAL DEMO · Separate data" : "MICROSOFT TO DO", FontSize = 10, CharacterSpacing = 100, Margin = new Thickness(8, 0, 0, 30) });
        foreach (var (name, value) in new[] { ("Today", TaskFilter.Today), ("Upcoming", TaskFilter.Upcoming), ("All tasks", TaskFilter.All), ("Overdue", TaskFilter.Overdue), ("Important", TaskFilter.Important), ("Completed", TaskFilter.Completed) })
            sidebar.Children.Add(Button((listFilter == null && filter == value ? "•  " : "    ") + name, () => { filter = value; listFilter = null; CloseDetail(); RebuildSidebar(); Refresh(); }));
        sidebar.Children.Add(new TextBlock { Text = "YOUR LISTS", FontSize = 10, CharacterSpacing = 140, Margin = new Thickness(8, 30, 0, 8), Opacity = .65 });
        foreach (var list in store.Lists()) sidebar.Children.Add(Button((listFilter == list.Id ? "•  " : "    ") + list.Name + (list.RemoteMissing ? " · unavailable" : ""), () => { listFilter = list.Id; filter = TaskFilter.All; CloseDetail(); RebuildSidebar(); Refresh(); }));
        sidebar.Children.Add(AsyncButton("+ New list", AddList));
        sidebar.Children.Add(AsyncButton("↻  Sync now", SyncNow));
        sidebar.Children.Add(AsyncButton("Settings", ShowSettings));
    }
    public void Refresh()
    {
        if (root.ColumnDefinitions.Count == 0) return;
        var tasks = store.Tasks(); var lists = store.Lists(); var today = DateOnly.FromDateTime(DateTime.Now);
        var renderState = $"{settings.Profile}|{settings.Theme}|{Dark}|{filter}|{listFilter}|{today}|{search.Text}|{sort.SelectedIndex}|{lastSync:O}|{syncing}|" + string.Join(';', tasks.Select(x => $"{x.Id}:{x.Revision}")) + "|" + string.Join(';', lists.Select(x => $"{x.Id}:{x.Name}:{x.RemoteMissing}"));
        if (renderedState == renderState) return;
        renderedState = renderState;
        var items = TaskQuery.Apply(tasks, filter, today, search.Text, listFilter, lists);
        items = sort.SelectedIndex switch { 1 => items.OrderByDescending(x => x.Priority).ThenBy(x => x.DueDate), 2 => items.OrderByDescending(x => x.UpdatedAt), 3 => items.OrderBy(x => x.Title), _ => items.OrderBy(x => x.DueDate ?? DateOnly.MaxValue).ThenByDescending(x => x.Priority) };
        var result = items.ToList();
        heading.Text = listFilter != null ? lists.FirstOrDefault(x => x.Id == listFilter)?.Name ?? "Your tasks" : filter switch { TaskFilter.Today => "Make room for today.", TaskFilter.Upcoming => "A little planning ahead.", TaskFilter.Completed => "Look how far you’ve come.", TaskFilter.Overdue => "One step at a time.", TaskFilter.Important => "What matters most.", _ => "Everything, in its place." };
        subtitle.Text = $"{DateTime.Now:dddd, MMMM d}  ·  {result.Count} {(result.Count == 1 ? "task" : "tasks")}";
        rows.Children.Clear();
        if (result.Count == 0) rows.Children.Add(new StackPanel { Spacing = 12, Margin = new Thickness(0, 42, 0, 0), Children = { Text("A little breathing room.", 24), Text("Add a task above, or choose another view.") } });
        foreach (var item in result)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(12), Background = Brush(Dark ? "#2C2B28" : "#FFFFFF") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var check = new CheckBox { IsChecked = item.Completed, MinWidth = 0, Width = 24, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "Complete " + item.Title);
            check.Click += async (_, _) => { await Safe(() => { store.Complete(item, check.IsChecked == true); return Task.CompletedTask; }); renderedState = null; Refresh(); }; row.Children.Add(check);
            var label = new StackPanel { Spacing = 5 }; var title = Text(item.Title, 16); title.Opacity = item.Completed ? .55 : 1; label.Children.Add(title);
            var meta = Text($"{lists.FirstOrDefault(x => x.Id == item.ListId)?.Name}   ·   {item.DueLabel}" + (item.Priority == Priority.High ? "   ·   Important" : "") + (item.SyncStatus is SyncStatus.Conflict or SyncStatus.Pending or SyncStatus.Uploading ? $"   ·   {item.SyncStatus}" : ""), 11); meta.Opacity = .65; label.Children.Add(meta);
            var open = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, "Edit " + item.Title);
            open.Click += async (_, _) => await Safe(() => { OpenDetail(store.Find(item.Id)!); return Task.CompletedTask; });
            open.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Space) { e.Handled = true; await Safe(() => { store.Complete(store.Find(item.Id)!, !item.Completed); Refresh(); return Task.CompletedTask; }); } };
            Grid.SetColumn(open, 1); row.Children.Add(open); rows.Children.Add(row);
        }
        var pending = tasks.Count(x => x.SyncStatus is SyncStatus.Pending or SyncStatus.Uploading); var conflicts = store.Conflicts().Count;
        if (!syncing) status.Text = settings.Profile == "demo" ? "Local demo · Changes stay on this PC. Connect Microsoft To Do in Settings." : $"{(lastSync == default ? "Cached locally" : "✓ Last sync " + lastSync.ToLocalTime().ToString("t"))}  ·  {pending} changes waiting  ·  {conflicts} conflicts";
    }
    async Task AddQuick()
    {
        var title = quick.Text.Trim(); if (title.Length == 0) return;
        var lists = store.Lists().Where(x => !x.RemoteMissing).ToList(); if (lists.Count == 0) { Report("Create a list first."); return; }
        var list = lists.FirstOrDefault(x => x.Id == listFilter) ?? lists[0];
        store.Save(new TaskItem { Title = title, ListId = list.Id, RemoteListId = list.RemoteId, DueDate = filter == TaskFilter.Today ? DateOnly.FromDateTime(DateTime.Now) : null, SyncStatus = settings.Profile == "demo" ? SyncStatus.Local : SyncStatus.Pending });
        quick.Text = ""; Refresh(); await Task.CompletedTask;
    }
    async Task AddList()
    {
        var name = new TextBox { Header = "List name", MaxLength = 100 };
        var dialog = Dialog("A place for your tasks", name, "Create list");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(name.Text))
        {
            store.SaveList(new TaskList(Guid.NewGuid(), name.Text.Trim())); RebuildSidebar(); Refresh();
        }
    }
    ContentDialog Dialog(string title, object content, string primary = "Save") => new() { Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme };
    void CloseDetail() { selected = null; root.ColumnDefinitions[2].Width = new GridLength(0); if (detailScroll != null) detailScroll.Visibility = Visibility.Collapsed; detail.Children.Clear(); }
    void LayoutDetail()
    {
        if (detailScroll == null) return;
        detailScroll.Visibility = Visibility.Visible;
        if (root.ActualWidth < 1100) {
            root.ColumnDefinitions[2].Width = new GridLength(0);
            Grid.SetColumn(detailScroll, 1); Grid.SetColumnSpan(detailScroll, 2);
            detailScroll.HorizontalAlignment = HorizontalAlignment.Right; detailScroll.Width = Math.Min(340, Math.Max(280, root.ActualWidth - 220));
        } else {
            Grid.SetColumn(detailScroll, 2); Grid.SetColumnSpan(detailScroll, 1);
            detailScroll.HorizontalAlignment = HorizontalAlignment.Stretch; detailScroll.Width = double.NaN;
            root.ColumnDefinitions[2].Width = new GridLength(340);
        }
    }
    void OpenDetail(TaskItem item)
    {
        selected = item; detail.Children.Clear(); LayoutDetail();
        detail.Children.Add(Button("Close  ×", CloseDetail));
        var title = new TextBox { Header = "Task", Text = item.Title, MaxLength = 255, TextWrapping = TextWrapping.Wrap };
        var notes = new TextBox { Header = "Notes", Text = item.Notes, AcceptsReturn = true, MinHeight = 100, MaxHeight = 180, TextWrapping = TextWrapping.Wrap, MaxLength = 10000 };
        var lists = store.Lists().Where(x => !x.RemoteMissing || x.Id == item.ListId).ToList();
        var listBox = new ComboBox { Header = "List", ItemsSource = lists, DisplayMemberPath = "Name", SelectedIndex = lists.FindIndex(x => x.Id == item.ListId), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = item.RemoteId == null };
        var priority = new ComboBox { Header = "Priority", ItemsSource = Enum.GetNames<Priority>(), SelectedIndex = (int)item.Priority, HorizontalAlignment = HorizontalAlignment.Stretch };
        var due = new CalendarDatePicker { Header = "Due date", Date = item.DueDate == null ? null : new DateTimeOffset(item.DueDate.Value.ToDateTime(TimeOnly.MinValue)), PlaceholderText = "No due date", HorizontalAlignment = HorizontalAlignment.Stretch };
        var dueActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dueActions.Children.Add(Button("Today", () => due.Date = DateTimeOffset.Now)); dueActions.Children.Add(Button("Tomorrow", () => due.Date = DateTimeOffset.Now.AddDays(1))); dueActions.Children.Add(Button("Clear", () => due.Date = null));
        var reminderOn = new ToggleSwitch { Header = "Reminder", IsOn = item.ReminderDateTime != null };
        var reminderDate = new CalendarDatePicker { Date = item.ReminderDateTime?.ToLocalTime() ?? DateTimeOffset.Now, HorizontalAlignment = HorizontalAlignment.Stretch };
        var reminderTime = new TimePicker { Time = item.ReminderDateTime?.ToLocalTime().TimeOfDay ?? new TimeSpan(DateTime.Now.Hour, 0, 0), ClockIdentifier = "24HourClock" };
        var repeat = new ComboBox { Header = "Repeat", ItemsSource = new[] { "Never", "Daily", "Weekly", "Monthly", "Keep existing pattern" }, SelectedIndex = item.Recurrence == null ? 0 : 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var control in new UIElement[] { title, notes, listBox, priority, due, dueActions, reminderOn, reminderDate, reminderTime, repeat }) detail.Children.Add(control);
        detail.Children.Add(Text($"Created {item.CreatedAt.ToLocalTime():g}\nModified {item.UpdatedAt.ToLocalTime():g}\nSource: {item.Source}\nSync: {item.SyncStatus}", 11));
        var save = AsyncButton("Save changes", async () => {
            if (listBox.SelectedItem is not TaskList list) throw new InvalidOperationException("Select a list.");
            DateTimeOffset? reminder = null;
            if (reminderOn.IsOn)
            {
                if (reminderDate.Date == null) throw new InvalidOperationException("Choose a reminder date.");
                var dt = reminderDate.Date.Value.Date + reminderTime.Time;
                if (TimeZoneInfo.Local.IsInvalidTime(dt)) throw new InvalidOperationException("This time falls in a daylight-saving gap. Choose another time.");
                reminder = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
                if (reminder <= DateTimeOffset.Now && reminder != item.ReminderDateTime) throw new InvalidOperationException("Choose a future reminder time.");
            }
            var recurrence = repeat.SelectedIndex == 4 ? item.Recurrence : GraphMapping.CreateRecurrence(repeat.SelectedIndex, due.Date?.DateTime ?? DateTime.Today);
            store.Edit(item with { Title = title.Text.Trim(), Notes = notes.Text, ListId = list.Id, RemoteListId = list.RemoteId, Priority = (Priority)priority.SelectedIndex, DueDate = due.Date == null ? null : DateOnly.FromDateTime(due.Date.Value.DateTime), ReminderDateTime = reminder, Recurrence = recurrence });
            Refresh(); CloseDetail(); await Task.CompletedTask;
        }); save.IsEnabled = item.SyncStatus is not (SyncStatus.Conflict or SyncStatus.Uploading); detail.Children.Add(save);
        detail.Children.Add(AsyncButton(item.Completed ? "Mark incomplete" : "Complete task", () => { store.Complete(store.Find(item.Id)!, !item.Completed); Refresh(); CloseDetail(); return Task.CompletedTask; }));
        detail.Children.Add(AsyncButton("Delete task", async () => { if (await Dialog("Delete this task?", Text("This deletion will also be sent to Microsoft To Do for a synchronized task."), "Delete").ShowAsync() == ContentDialogResult.Primary) { store.Delete(store.Find(item.Id)!); CloseDetail(); Refresh(); } }));
    }
    public void Report(string text) { message.Message = text; message.IsOpen = true; }
    public async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { AppLog.Write("operation.failed", e); Report(e is GraphException ge ? $"Microsoft To Do returned {ge.StatusCode}. Your local tasks are safe. Try syncing again." : e.Message); }
    }
    public void ShowMain() { AppWindow.Show(); Activate(); }
    public void QuickAdd() { ShowMain(); quick.Focus(FocusState.Keyboard); }
    public void ShowToday() { ShowMain(); filter = TaskFilter.Today; listFilter = null; RebuildSidebar(); Refresh(); }
    public void Quit() { quitting = true; Close(); }
    public void HandleActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments activation)
    {
        ShowMain();
        var args = Environment.GetCommandLineArgs();
        var uriArg = args.FirstOrDefault(x => x.StartsWith("ivorytasks:", StringComparison.OrdinalIgnoreCase));
        if (activation.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocol) uriArg = protocol.Uri.AbsoluteUri;
        if (uriArg != null && Uri.TryCreate(uriArg, UriKind.Absolute, out var uri) && Guid.TryParse(uri.AbsolutePath.Trim('/'), out var id))
        { var task = store.Find(id); if (task != null) OpenDetail(task); }
        if (uriArg != null && Uri.TryCreate(uriArg, UriKind.Absolute, out var action) && action.Host == "sync") _ = Safe(SyncNow);
    }
}
