using Microsoft.UI.Xaml;

namespace LiteDM.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        try { InitializeComponent(); }
        catch (Exception ex) { StartupLog.Write("App XAML initialization failed", ex); throw; }
        UnhandledException += (_, e) => StartupLog.Write("Unhandled application exception", e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (!InstanceCoordinator.TryOwn())
            {
                await InstanceCoordinator.SendAsync(new LiteDM.Core.NativeRequest("show", ""));
                Exit();
                return;
            }

            _window = new MainWindow();
            _window.Activate();
            await _window.InitializeAsync();
        }
        catch (Exception ex)
        {
            StartupLog.Write("Desktop startup failed", ex);
            MessageBox(0, "LiteDM could not start. Details were saved under LocalAppData\\LiteDM\\startup.log.", "LiteDM", 0x10);
            Exit();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(nint hwnd, string text, string caption, uint type);
}
