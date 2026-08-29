using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RpDashboard.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crash on a background thread leaves nothing behind but an entry in Event Viewer,
        // which is no use to anyone. This app runs in the tray for hours with timers and a poll
        // loop ticking away in the background, so an unhandled exception out there is the most
        // likely way it ever dies — and the least likely to be witnessed.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record("background thread", args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
            Record("UI thread", args.Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record("task", args.Exception);
            args.SetObserved();
        };

        // Explicit rather than StartupUri, because the window has to be constructed before the
        // tray icon decides whether to show it: starting minimised to tray is a supported start
        // mode, and ShutdownMode has to be set before a window that can be hidden ever exists.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Appends to %LOCALAPPDATA%\RpDashboard\crash.log. Appends rather than overwrites, because
    /// the interesting case is a crash that keeps happening.
    /// </summary>
    private static void Record(string where, Exception? exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RpDashboard");

            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  unhandled on the {where}{Environment.NewLine}" +
                (exception?.ToString() ?? "(no exception object)") +
                Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // Logging a crash must not cause one.
        }
    }
}
