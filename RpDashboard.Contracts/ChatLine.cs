namespace RpDashboard.Contracts;

/// <summary>
/// One message from the GTA World HUD's chat list.
///
/// Both forms are carried because the tools need different things from the same line and
/// the connection should only fetch it once: Unit Alert and RP Pace match on
/// <see cref="Text"/>, while Street Medic classifies on the inline span colour that only
/// survives in <see cref="RawHtml"/>.
/// </summary>
/// <param name="Sequence">
/// Monotonic per-session counter assigned by the feed. It is what lets a subscription that
/// starts late resume from "everything after now" instead of replaying the buffer — see
/// <see cref="IChatFeed.Subscribe"/>.
/// </param>
public sealed record ChatLine(long Sequence, string Text, string RawHtml, DateTime SeenAt);

/// <summary>What the shared connection is currently doing, for a tool's status line.</summary>
public enum ChatFeedState
{
    /// <summary>Nothing is subscribed, so nothing is being polled.</summary>
    Idle,

    /// <summary>FiveM isn't running, or its HUD hasn't loaded yet. Retrying with backoff.</summary>
    Waiting,

    /// <summary>Connected and polling.</summary>
    Reading
}

public sealed record ChatFeedStatus(ChatFeedState State, string Detail);
