using Microsoft.Windows.AppLifecycle;
namespace Ivory.Desktop;
public static class Program
{
    [System.Runtime.InteropServices.DllImport("Microsoft.ui.xaml.dll")]
    static extern void XamlCheckProcessRequirements();
    public static AppInstance Instance = null!;
    [STAThread] public static void Main(string[] args)
    {
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Instance = AppInstance.FindOrRegisterForKey("Ivory.Main");
        if (!Instance.IsCurrent) { var activation = AppInstance.GetCurrent().GetActivatedEventArgs(); Task.Run(async () => await Instance.RedirectActivationToAsync(activation)).GetAwaiter().GetResult(); return; }
        Application.Start(initialization => {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
