namespace RpDashboard.Contracts;

/// <summary>
/// The single read-only view of the game's chat, shared by every tool in the process.
///
/// This interface exists because the three tools used to run as three processes, each
/// holding its own WebSocket to FiveM's DevTools port and each evaluating its own
/// <c>querySelectorAll</c> against the HUD every half second. The host now does that once
/// and fans the result out here, so adding a fourth tool costs the game nothing.
///
/// Read-only is a hard invariant inherited from all three tools: there is deliberately no
/// method on this interface that writes to the game, and there must never be one.
/// </summary>
public interface IChatFeed
{
    bool IsConnected { get; }

    ChatFeedStatus Status { get; }

    /// <summary>Raised on the UI thread.</summary>
    event EventHandler<ChatFeedStatus>? StatusChanged;

    /// <summary>
    /// Starts delivering lines that arrive from now on. Lines already in the HUD buffer when
    /// this is called are never delivered — each subscription baselines against the feed's
    /// current head, so a tool switched on mid-session doesn't fire on ten minutes of backlog.
    ///
    /// Polling only runs while at least one subscription is active and unpaused, so a tool
    /// that is switched off costs nothing at all.
    /// </summary>
    IChatSubscription Subscribe(ChatSubscriptionOptions options);

    /// <summary>
    /// The tail of the HUD buffer as it stands right now, for diagnostics — RP Pace's
    /// "Raw chat" button is what this is for. Reuses the live connection if there is one
    /// rather than opening a second.
    /// </summary>
    Task<IReadOnlyList<ChatLine>> SnapshotAsync(int maxLines, CancellationToken cancellationToken = default);
}

public sealed record ChatSubscriptionOptions
{
    /// <summary>Shown in the host's connection diagnostics. Use the tool's display name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// How often this tool would like to see new lines. The feed polls at the shortest
    /// interval any active subscription asks for, so asking for more than you need slows
    /// nothing down but asking for less speeds nothing up either.
    /// </summary>
    public TimeSpan PreferredInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// A tool's handle on the feed. Disposing it stops delivery and, if it was the last one,
/// closes the connection to the game.
/// </summary>
public interface IChatSubscription : IDisposable
{
    /// <summary>
    /// Raised on the UI thread, once per new line, in order.
    /// </summary>
    event EventHandler<ChatLine>? LineReceived;

    /// <summary>
    /// Stops delivery without giving up the subscription. A paused subscription does not
    /// keep the connection alive, and does not replay what it missed when resumed — it
    /// re-baselines, same as a fresh subscribe.
    /// </summary>
    bool IsPaused { get; set; }
}
