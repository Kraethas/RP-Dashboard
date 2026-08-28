using System.Windows;

namespace RpDashboard.Contracts;

/// <summary>
/// One tab in the dashboard.
///
/// A tool assembly exports one of these per tab, with a public parameterless constructor.
/// The host finds them by reflecting over assemblies marked
/// <see cref="RpToolAssemblyAttribute"/> in the plugins folder.
/// </summary>
public interface IRpTool
{
    /// <summary>
    /// Stable, lowercase, no spaces — "unit-alert". This keys the tool's settings and its
    /// saved tab order, so changing it orphans the user's configuration. Pick it once.
    /// </summary>
    string Id { get; }

    /// <summary>Tab header.</summary>
    string Title { get; }

    /// <summary>One line, shown in the host's About/plugins list.</summary>
    string Description { get; }

    /// <summary>
    /// The folder under <c>%LOCALAPPDATA%</c> this tool's settings live in.
    ///
    /// Defaults to <see cref="Id"/>, which is right for a tool that never existed as its own
    /// exe. A converted tool should override it with the folder its standalone version used —
    /// "UnitAlert", not "unit-alert" — so the plugin build picks up the settings already on
    /// disk instead of starting the user from scratch.
    /// </summary>
    string SettingsFolderName => Id;

    /// <summary>
    /// Called once, before the view is built, and only if the host is going to load this tool.
    /// Throwing here is how a tool declines to load (a missing content file, say); the host
    /// reports it and carries on with the other tabs rather than failing to start.
    /// </summary>
    void Initialise(IToolHost host);

    /// <summary>
    /// Built once, on the UI thread, and kept for the life of the process — tab switches
    /// don't rebuild it, so a tool keeps its state and its timers while another tab is up.
    /// </summary>
    FrameworkElement CreateView();

    /// <summary>
    /// The window is closing. Persist settings, cancel background work, dispose the chat
    /// subscription. The host gives every tool this call before it exits, and swallows
    /// anything thrown so one tool can't block the others from shutting down cleanly.
    /// </summary>
    void Shutdown();
}
