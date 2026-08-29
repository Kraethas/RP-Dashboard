using System.IO;
using System.Windows.Threading;
using RpDashboard.Contracts;

namespace RpDashboard.Host.Chat;

/// <summary>
/// One poll loop over <see cref="NuiConnection"/>, fanned out to every subscribed tool.
///
/// This is where the consolidation actually pays. As three processes the tools ran three
/// sockets and three <c>Runtime.evaluate</c> calls against the HUD every 500 ms, none of them
/// aware of the others. Here there is one socket, one evaluate, one pass of new-line
/// detection, and the result is handed to however many tools are listening — so a fourth tool
/// costs the game nothing.
///
/// Two properties matter beyond that:
///
/// **It sleeps when nobody is listening.** The loop only runs while at least one subscription
/// is active and unpaused. Switch every tool off and the app stops touching the game entirely.
///
/// **Late subscribers do not see backlog.** Each subscription carries its own cursor into the
/// sequence, set to the current head when it starts. This replaces the per-app
/// "baseline on connect" rule the three tools each had privately, which does not survive the
/// merge: with a shared connection, a tool switched on mid-session would otherwise inherit a
/// connection that baselined ten minutes ago and immediately fire on everything since.
/// </summary>
internal sealed class ChatFeed : IChatFeed, IAsyncDisposable
{
    /// <summary>How long to wait between polls when the game isn't there. Backs off to <see cref="MaxRetry"/>.</summary>
    private static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(15);

    private readonly NuiConnection _connection = new();
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();

    /// <summary>The previous poll's messages, for working out which ones are new.</summary>
    private IReadOnlyList<string> _previousHtml = Array.Empty<string>();

    /// <summary>
    /// Recent lines, newest last, capped. Only exists so a subscription that starts between
    /// polls has a defined cursor to start from — nothing replays it.
    /// </summary>
    private readonly Queue<ChatLine> _recent = new();
    private const int RecentCapacity = 200;

    private long _head;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private ChatFeedStatus _status = new(ChatFeedState.Idle, "not listening");

    public ChatFeed(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool IsConnected => _connection.IsConnected;

    public ChatFeedStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<ChatFeedStatus>? StatusChanged;

    /// <summary>
    /// Raised when a poll found no overlap with the previous one, so chat may have gone by
    /// unseen. Rare: it takes more than a full HUD buffer between two polls, and the observed
    /// peak in real play is 24 lines a second against a 240-a-second read. On the UI thread.
    /// </summary>
    public event EventHandler? GapSuspected;

    private void RaiseGapSuspected()
        => _dispatcher.BeginInvoke(() => GapSuspected?.Invoke(this, EventArgs.Empty));

    public IChatSubscription Subscribe(ChatSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var subscription = new Subscription(this, options);

        lock (_gate)
        {
            // Start from the head as it stands now. Everything already in the buffer predates
            // this tool caring, so it is not this tool's to react to.
            subscription.Cursor = _head;
            _subscriptions.Add(subscription);
        }

        EnsureLoopState();
        return subscription;
    }

    public async Task<IReadOnlyList<ChatLine>> SnapshotAsync(
        int maxLines,
        CancellationToken cancellationToken = default)
    {
        // Reuses the live connection when the loop already has one open, rather than opening a
        // second socket for a diagnostic button. When the loop is idle this connects, reads
        // once, and leaves the connection open for the loop to find — cheaper than a reconnect
        // if the user is about to switch a tool on, and closed by Dispose either way.
        IReadOnlyList<string> html = await _connection.ReadVisibleHtmlAsync(cancellationToken);

        int skip = Math.Max(0, html.Count - Math.Max(1, maxLines));

        return html.Skip(skip)
            .Select((raw, i) => new ChatLine(skip + i, HtmlText.ToPlainText(raw), raw, DateTime.Now))
            .ToArray();
    }

    /// <summary>
    /// Starts the loop if anything wants lines, stops it if nothing does. Called whenever a
    /// subscription is added, paused, resumed or disposed.
    /// </summary>
    private void EnsureLoopState()
    {
        CancellationTokenSource? toCancel = null;

        lock (_gate)
        {
            bool wanted = _subscriptions.Any(s => s.IsActive);

            if (wanted && _loop is null)
            {
                _loopCts = new CancellationTokenSource();
                CancellationToken token = _loopCts.Token;
                _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
            else if (!wanted && _loop is not null)
            {
                toCancel = _loopCts;
                _loopCts = null;
                _loop = null;
            }
        }

        if (toCancel is null) return;

        toCancel.Cancel();
        toCancel.Dispose();

        // Drop the socket as well as the loop. Leaving it open would keep a DevTools client
        // attached to the game's CEF for no reason, which is the cost this whole class exists
        // to avoid; the next Subscribe reconnects in well under a second.
        _ = Task.Run(async () =>
        {
            try { await _connection.ResetAsync(); } catch { /* the game closing is the normal way this ends */ }
        });

        _previousHtml = Array.Empty<string>();
        SetStatus(new ChatFeedStatus(ChatFeedState.Idle, "not listening"));
    }

    private async Task RunAsync(CancellationToken token)
    {
        TimeSpan retry = MinRetry;

        while (!token.IsCancellationRequested)
        {
            TimeSpan wait;

            try
            {
                bool wasConnected = _connection.IsConnected;
                if (!wasConnected)
                    SetStatus(new ChatFeedStatus(ChatFeedState.Waiting, "looking for FiveM"));

                IReadOnlyList<string> html = await _connection.ReadVisibleHtmlAsync(token);

                if (!wasConnected)
                {
                    // A reconnect means the HUD list we are diffing against is stale — the
                    // buffer may have rolled over completely while the game was away. Treat the
                    // first read after reconnecting as the new baseline rather than announcing
                    // all of it as new.
                    _previousHtml = html;
                    SetStatus(new ChatFeedStatus(ChatFeedState.Reading, "reading chat"));
                    retry = MinRetry;
                    wait = CurrentInterval();
                }
                else
                {
                    Publish(NewLinesSince(_previousHtml, html, out bool gap));
                    if (gap) RaiseGapSuspected();
                    _previousHtml = html;
                    retry = MinRetry;
                    wait = CurrentInterval();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Everything else, including a plain OperationCanceledException, is transient.
                //
                // That second case is the bug Unit Alert shipped a fix for and it survives the
                // merge: NuiConnection gives each of its own HTTP and WebSocket calls a private
                // 2 s timeout, and when that fires it throws an OperationCanceledException that
                // is indistinguishable by type from the loop being told to stop. The `when`
                // clause above is what tells them apart — it checks *this* loop's token, not the
                // connection's internal one. Without it a single slow response from FiveM's
                // DevTools (a hitch, a HUD reload, a loading screen) ends polling for good and
                // the UI just quietly says it isn't listening any more.
                //
                // Any new catch added to this loop needs the same guard.
                await SafeResetAsync();
                SetStatus(new ChatFeedStatus(ChatFeedState.Waiting, DescribeFailure(ex)));

                wait = retry;
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            }

            try
            {
                await Task.Delay(wait, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SafeResetAsync()
    {
        try { await _connection.ResetAsync(); }
        catch { /* already gone */ }
    }

    private static string DescribeFailure(Exception ex)
        => ex is IOException ? ex.Message : "waiting for FiveM";

    /// <summary>
    /// The shortest interval any active subscription asked for. Polling faster than the most
    /// impatient tool needs would be work the game does for nobody.
    /// </summary>
    private TimeSpan CurrentInterval()
    {
        lock (_gate)
        {
            TimeSpan? shortest = _subscriptions
                .Where(s => s.IsActive)
                .Select(s => s.Options.PreferredInterval)
                .DefaultIfEmpty(TimeSpan.FromMilliseconds(500))
                .Min();

            // A tool asking for something absurd shouldn't be able to hammer the game.
            TimeSpan value = shortest ?? TimeSpan.FromMilliseconds(500);
            return value < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : value;
        }
    }

    /// <summary>
    /// Which of <paramref name="current"/> weren't in <paramref name="previous"/>.
    ///
    /// The HUD is a scrolling window: old messages fall off the top as new ones are appended,
    /// so the two snapshots overlap in the middle. Find the longest suffix of the previous
    /// snapshot that is also a prefix of this one, and everything past it is new. This is the
    /// same overlap rule all three tools used privately, done once here.
    /// </summary>
    internal static IReadOnlyList<string> NewLinesSince(
        IReadOnlyList<string> previous,
        IReadOnlyList<string> current)
        => NewLinesSince(previous, current, out _);

    /// <param name="gapSuspected">
    /// True when no overlap could be found at all between two non-empty reads. That means more
    /// than a full buffer's worth arrived between polls, so some of it was never seen — or the
    /// HUD was cleared, which is indistinguishable from here.
    ///
    /// Worth surfacing rather than swallowing. The tools only ever needed *new* lines and could
    /// treat a gap as nothing much; the log writer is keeping the only copy of this chat that
    /// will ever exist, and quietly missing some of it is the one failure it must not have.
    /// </param>
    internal static IReadOnlyList<string> NewLinesSince(
        IReadOnlyList<string> previous,
        IReadOnlyList<string> current,
        out bool gapSuspected)
    {
        gapSuspected = false;

        if (current.Count == 0) return Array.Empty<string>();

        // Nothing to compare against: the caller is responsible for treating a first read as a
        // baseline, so reaching here with an empty previous means a genuinely empty chat.
        if (previous.Count == 0) return current;

        int max = Math.Min(previous.Count, current.Count);

        for (int k = max; k > 0; k--)
        {
            bool matches = true;
            int offset = previous.Count - k;

            for (int i = 0; i < k; i++)
            {
                if (!string.Equals(previous[offset + i], current[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return current.Skip(k).ToArray();
        }

        // No overlap at all — the buffer rolled over entirely between polls, or the HUD was
        // cleared. Everything visible is new, and something may not have been seen.
        gapSuspected = true;
        return current;
    }

    private void Publish(IReadOnlyList<string> newHtml)
    {
        if (newHtml.Count == 0) return;

        DateTime seenAt = DateTime.Now;
        var lines = new List<ChatLine>(newHtml.Count);

        lock (_gate)
        {
            foreach (string raw in newHtml)
            {
                var line = new ChatLine(_head++, HtmlText.ToPlainText(raw), raw, seenAt);
                lines.Add(line);

                _recent.Enqueue(line);
                while (_recent.Count > RecentCapacity) _recent.Dequeue();
            }
        }

        Subscription[] targets;
        lock (_gate) targets = _subscriptions.Where(s => s.IsActive).ToArray();

        if (targets.Length == 0) return;

        // Marshal once for the whole batch rather than per subscription per line: tools expect
        // their handlers on the UI thread, and this is the hot path.
        _dispatcher.BeginInvoke(() =>
        {
            foreach (Subscription subscription in targets)
                subscription.Deliver(lines);
        });
    }

    private void SetStatus(ChatFeedStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }

        _dispatcher.BeginInvoke(() => StatusChanged?.Invoke(this, status));
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate) _subscriptions.Remove(subscription);
        EnsureLoopState();
    }

    /// <summary>Current head of the sequence, for a subscription re-baselining after a pause.</summary>
    private long Head
    {
        get { lock (_gate) return _head; }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;

        lock (_gate)
        {
            _subscriptions.Clear();
            cts = _loopCts;
            _loopCts = null;
        }

        cts?.Cancel();

        if (_loop is not null)
        {
            try { await _loop; } catch { /* shutting down */ }
            _loop = null;
        }

        cts?.Dispose();
        await _connection.DisposeAsync();
    }

    private sealed class Subscription : IChatSubscription
    {
        private readonly ChatFeed _feed;
        private bool _paused;
        private bool _disposed;

        public Subscription(ChatFeed feed, ChatSubscriptionOptions options)
        {
            _feed = feed;
            Options = options;
        }

        public ChatSubscriptionOptions Options { get; }

        /// <summary>Sequence of the next line this subscription has not yet seen.</summary>
        public long Cursor { get; set; }

        public bool IsActive => !_paused && !_disposed;

        public event EventHandler<ChatLine>? LineReceived;

        public bool IsPaused
        {
            get => _paused;
            set
            {
                if (_paused == value || _disposed) return;
                _paused = value;

                // Resuming re-baselines rather than catching up. A tool that was switched off
                // for an hour does not want an hour of alerts the moment it comes back, and
                // for the ones that time things — RP Pace's idle clock — replaying stale lines
                // would be actively wrong.
                if (!value) Cursor = _feed.Head;

                _feed.EnsureLoopState();
            }
        }

        public void Deliver(IReadOnlyList<ChatLine> batch)
        {
            if (!IsActive) return;

            EventHandler<ChatLine>? handler = LineReceived;
            if (handler is null)
            {
                Cursor = batch[^1].Sequence + 1;
                return;
            }

            foreach (ChatLine line in batch)
            {
                if (line.Sequence < Cursor) continue;
                Cursor = line.Sequence + 1;

                // One tool throwing must not cost the others their line. The alternative is a
                // bad regex or a missing sound file in one tab silently stopping every other
                // tab from seeing chat.
                try { handler(this, line); }
                catch { /* the tool's problem, reported through its own status */ }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            LineReceived = null;
            _feed.Remove(this);
        }
    }
}
