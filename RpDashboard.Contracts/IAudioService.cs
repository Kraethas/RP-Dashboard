namespace RpDashboard.Contracts;

/// <summary>
/// Sound playback, pooled by file path.
///
/// Unit Alert's rule carries over: one player per distinct path, so a dispatch tone, a
/// callsign's own sound and the AFK tone can overlap without cutting each other off. Now
/// that the tools share a process the pool is shared too, which also means Street Medic and
/// Unit Alert pointing at the same file share one player rather than two.
/// </summary>
public interface IAudioService
{
    /// <summary>
    /// Plays a file from the start. Safe to call from any thread — the underlying player
    /// has to be touched on the UI thread and this marshals for you. A missing or unreadable
    /// file is reported through <see cref="PlaybackFailed"/>, not thrown: a broken sound
    /// path should never take down the poll loop that found the match.
    /// </summary>
    void Play(string filePath);

    /// <summary>
    /// Opens a file ahead of time so the first alert doesn't stall while Windows loads the
    /// codec. Cheap to call repeatedly; a path already in the pool is left alone.
    /// </summary>
    void Preload(string filePath);

    /// <summary>Stops everything this service is playing. Used by the host's global mute.</summary>
    void StopAll();

    /// <summary>0.0 to 1.0, applied to every player in the pool.</summary>
    double Volume { get; set; }

    event EventHandler<AudioFailure>? PlaybackFailed;
}

public sealed record AudioFailure(string FilePath, string Message);
