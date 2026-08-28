using System.Windows;
using RpDashboard.Contracts;

[assembly: RpToolAssembly(HostApi.Version)]

namespace UnitAlert;

/// <summary>
/// Unit Alert's entry point into the dashboard. Everything it does is in
/// <see cref="UnitAlertView"/>; this is the handful of lines that make it a tab.
/// </summary>
public sealed class UnitAlertTool : IRpTool
{
    private IToolHost? _host;
    private UnitAlertView? _view;

    public string Id => "unit-alert";

    public string Title => "Unit Alert";

    public string Description => "Plays a sound when a callsign appears in the GTA World chat.";

    /// <summary>
    /// The folder the standalone exe wrote to. Keeping it means anyone upgrading from Unit
    /// Alert 1.x finds their callsigns and sounds already there.
    /// </summary>
    public string SettingsFolderName => "UnitAlert";

    public void Initialise(IToolHost host) => _host = host;

    public FrameworkElement CreateView()
    {
        if (_host is null) throw new InvalidOperationException("Initialise must run before CreateView.");

        _view = new UnitAlertView(_host);
        return _view;
    }

    public void Shutdown() => _view?.Shutdown();
}
