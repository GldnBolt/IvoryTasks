namespace Ivory.Desktop;
public partial class App : Application
{
    public static MainWindow? Main;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { AppLog.Write("ui.unhandled", e.Exception); e.Handled = true; Main?.Report("Something went wrong. Your saved tasks are safe. " + e.Exception.Message); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Write("app.start");
        try {
            Main = new MainWindow(); Main.Activate();
            Program.Instance.Activated += (_, activation) => Main.DispatcherQueue.TryEnqueue(() => Main.HandleActivation(activation));
            Main.HandleActivation(Program.Instance.GetActivatedEventArgs());
        } catch (Exception e) {
            AppLog.Write("app.start.failed", e);
            System.Windows.Forms.MessageBox.Show("Ivory could not open its saved data or configuration. Your database has not been reset.\n\n" + e.Message + "\n\nData folder: " + AppSettings.DataRoot, "Ivory · Startup problem", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            Exit();
        }
    }
}
