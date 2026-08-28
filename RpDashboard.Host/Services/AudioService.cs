using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using RpDashboard.Contracts;

namespace RpDashboard.Host.Services;

/// <summary>
/// Sound playback, one <see cref="MediaPlayer"/> per distinct file path.
///
/// The pooling rule comes from Unit Alert and is the reason this isn't just a single player:
/// a dispatch tone, a callsign's own sound and the AFK tone have to be able to overlap
/// instead of cutting each other off. Sharing the pool across tools extends that — two tools
/// pointed at the same file now share one player rather than holding two.
///
/// MediaPlayer is a DispatcherObject and must be created and driven on the UI thread, but the
/// callers are poll loops on background threads, so every entry point marshals.
/// </summary>
internal sealed class AudioService : IAudioService
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, Sound> _players = new(StringComparer.OrdinalIgnoreCase);
    private double _volume = 1.0;

    public AudioService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public event EventHandler<AudioFailure>? PlaybackFailed;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            _dispatcher.BeginInvoke(() =>
            {
                foreach (Sound sound in _players.Values)
                    sound.Player.Volume = _volume;
            });
        }
    }

    public void Play(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        if (_dispatcher.CheckAccess()) PlayCore(filePath);
        else _dispatcher.BeginInvoke(() => PlayCore(filePath));
    }

    public void Preload(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        if (_dispatcher.CheckAccess()) { GetOrCreate(filePath); return; }
        _dispatcher.BeginInvoke(() => GetOrCreate(filePath));
    }

    private void PlayCore(string filePath)
    {
        try
        {
            // Checked before touching the player: MediaPlayer reports a missing file
            // asynchronously through MediaFailed, long after the call that caused it, which
            // makes it useless for telling the user which sound is broken.
            if (!File.Exists(filePath))
            {
                PlaybackFailed?.Invoke(this, new AudioFailure(filePath, "The sound file is missing."));
                return;
            }

            Sound sound = GetOrCreate(filePath);

            // Restart rather than overlap: back-to-back hails on the same sound should
            // re-trigger, not stack. Unit Alert's rule, and the reason the pool is keyed by
            // path — two *different* sounds still overlap, which is the case that matters.
            sound.Player.Stop();

            // Seeking before the media has opened either throws or is quietly ignored
            // depending on the codec, so the very first play just starts from the top.
            if (sound.IsOpen) sound.Player.Position = TimeSpan.Zero;

            sound.Player.Play();
        }
        catch (Exception ex)
        {
            // A broken sound path must never take down the poll loop that found the match.
            PlaybackFailed?.Invoke(this, new AudioFailure(filePath, ex.Message));
        }
    }

    private Sound GetOrCreate(string filePath)
    {
        if (_players.TryGetValue(filePath, out Sound? existing)) return existing;

        var player = new MediaPlayer { Volume = _volume };
        var sound = new Sound { Player = player };

        player.MediaOpened += (_, _) => sound.IsOpen = true;
        player.MediaFailed += (_, e) =>
        {
            sound.IsOpen = false;
            PlaybackFailed?.Invoke(this, new AudioFailure(
                filePath,
                e.ErrorException?.Message ?? "Windows couldn't play that file."));
        };

        player.Open(new Uri(filePath, UriKind.Absolute));
        _players[filePath] = sound;
        return sound;
    }

    public void StopAll()
    {
        if (_dispatcher.CheckAccess()) StopAllCore();
        else _dispatcher.BeginInvoke(StopAllCore);
    }

    private void StopAllCore()
    {
        foreach (Sound sound in _players.Values)
        {
            try { sound.Player.Stop(); } catch { /* nothing playing */ }
        }
    }

    public void Dispose()
    {
        StopAllCore();

        foreach (Sound sound in _players.Values)
        {
            try { sound.Player.Close(); } catch { /* already closed */ }
        }

        _players.Clear();
    }

    private sealed class Sound
    {
        public required MediaPlayer Player { get; init; }

        /// <summary>Set once MediaOpened fires; until then the player can't be seeked.</summary>
        public bool IsOpen { get; set; }
    }
}
