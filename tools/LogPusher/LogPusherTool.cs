using System.Windows;
using RpDashboard.Contracts;

[assembly: RpToolAssembly(HostApi.Version)]

namespace LogPusherPanel;

/// <summary>
/// LogPusher's entry point into the dashboard.
///
/// The only tool here that never subscribes to the chat feed — LogPusher reads Afterline's log
/// files off disk and has nothing to do with the game's HUD. Opening this tab costs the game
/// nothing at all.
/// </summary>
public sealed class LogPusherTool : IRpTool
{
    private IToolHost? _host;
    private LogPusherView? _view;

    public string Id => "logpusher";

    public string Title => "LogPusher";

    public string Description =>
        "Start, stop and watch the session archiver that pushes your logs to Notion.";

    /// <summary>
    /// The panel's own folder, not LogPusher's. All this remembers is where the exe is; the
    /// tool's real configuration stays in its own .env beside it, where LogPusher expects it.
    /// </summary>
    public string SettingsFolderName => "RpDashboard.LogPusher";

    public void Initialise(IToolHost host) => _host = host;

    public FrameworkElement CreateView()
    {
        if (_host is null) throw new InvalidOperationException("Initialise must run before CreateView.");

        _view = new LogPusherView(_host);
        return _view;
    }

    public void Shutdown() => _view?.Shutdown();
}
