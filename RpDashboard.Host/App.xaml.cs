using System.Windows;

namespace RpDashboard.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Explicit rather than StartupUri, because the window has to be constructed before the
        // tray icon decides whether to show it: starting minimised to tray is a supported start
        // mode, and ShutdownMode has to be set before a window that can be hidden ever exists.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
