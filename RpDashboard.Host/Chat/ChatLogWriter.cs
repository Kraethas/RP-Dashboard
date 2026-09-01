using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using RpDashboard.Contracts;

namespace RpDashboard.Host.Chat;

/// <summary>
/// Writes the chat to disk, deduplicated, as it arrives.
///
/// This exists to replace Afterline. Afterline is a separate program that scrapes the same HUD
/// through the same DevTools port, so running it costs a second connection to the game and 69 MB
/// — and it re-reads the buffer aggressively enough that **73% of what it writes is duplicates**
/// (22,738 of 31,148 lines across five days; one 1.5 MB file was 79% repeats). LogPusher then
/// spends a whole pass throwing them away again, and every agent call reads a file where four
/// lines in five are noise.
///
/// The dashboard already solves that. <see cref="ChatFeed"/> reports each line exactly once, by
/// the same overlap rule three tools depend on, so writing what it hands over produces a clean
/// log by construction.
///
/// **The file format is deliberately Afterline's.** LogPusher identifies that format from the
/// filename alone and reads `[HH:MM:SS] text` bodies, so pointing it at this folder needs no
/// change to LogPusher at all. That is what makes it possible to run both for a while and
/// compare, rather than switching on faith.
///
/// A sidecar `.lines.jsonl` carries what the text format cannot: the sequence number and the
/// colour GTAW drew each line in. Afterline captures colour too and nothing ever reads it; here
/// it is the raw material for the constants Street Medic is still missing.
/// </summary>
internal sealed class ChatLogWriter : IDisposable
{
    /// <summary>Matches Afterline, and is what LogPusher's filename check looks for.</summary>
    private const string FileNameFormat = "Chatlog [GTAWorld Roleplay English] [{0}].txt";

    private const string ServerHeader = "[SERVER: GTAWorld Roleplay English]";

    /// <summary>
    /// GTAW's HUD prints its own "[HH:MM:SS] " in front of most lines, so the text arriving from
    /// the feed usually carries one already. Prefixing another produced
    /// "[00:19:45] [21:13:01] Welcome to GTA World ." — two stamps, the outer one being merely
    /// when we happened to read it, and a stray timestamp left sitting in the body once
    /// LogPusher strips the first.
    ///
    /// The game's own stamp is both correct and the one Afterline records, so where there is one
    /// it is kept and nothing is added.
    /// </summary>
    private static readonly Regex AlreadyStamped = new(@"^\[\d{2}:\d{2}:\d{2}\]\s", RegexOptions.Compiled);

    /// <summary>
    /// Lines are queued and written by a timer rather than inline. The feed delivers on the UI
    /// thread, and a stalled disk must not be able to hitch the window.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// UTF-8 with no BOM. Encoding.UTF8 emits one, and LogPusher opens these files as plain
    /// "utf-8" rather than "utf-8-sig", so a BOM would arrive glued to the front of the first
    /// line — invisible, and enough to stop that line matching anything.
    /// </summary>
    private static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IChatFeed _feed;
    private readonly Dispatcher _dispatcher;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Queue<ChatLine> _pending = new();

    /// <summary>
    /// The log is kept on the game's clock, not this machine's. Every other player's log is on
    /// the game's clock, and a log that cannot be lined up against someone else's is worth much
    /// less if it is ever needed.
    /// </summary>
    private readonly GameClock _gameClock = new();

    private IChatSubscription? _subscription;
    private System.Threading.Timer? _flushTimer;
    private DateTime _currentDay = DateTime.MinValue;
    private bool _disposed;

    public ChatLogWriter(IChatFeed feed, Dispatcher dispatcher, string directory)
    {
        _feed = feed;
        _dispatcher = dispatcher;
        _directory = directory;
    }

    /// <summary>Lines written to disk since the dashboard started.</summary>
    public long LinesWritten { get; private set; }

    /// <summary>The file currently being appended to, or null before the first line.</summary>
    public string? CurrentFile { get; private set; }

    public bool IsRunning => _subscription is not null;

    /// <summary>
    /// Raised when a line is written, for the status bar.
    ///
    /// Marshalled to the UI thread, because the flush runs on a timer and every other host
    /// service does the same. Raising it from the timer's thread crashed the app: a handler set
    /// a TextBlock, WPF threw, and nothing catches an exception on a threadpool thread.
    /// </summary>
    public event EventHandler? Progress;

    /// <summary>Raised with a human-readable reason when writing fails. On the UI thread.</summary>
    public event EventHandler<string>? Failed;

    private void Raise(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void Start()
    {
        if (_subscription is not null) return;

        _subscription = _feed.Subscribe(new ChatSubscriptionOptions
        {
            Name = "Chat log",

            // Half a second, matching the fastest any tool asks for: this is the subscriber
            // that must not miss anything, and asking for less costs the game nothing extra
            // because the feed polls at the shortest interval anyone requested anyway.
            PreferredInterval = TimeSpan.FromMilliseconds(500)
        });

        _subscription.LineReceived += OnLine;

        // The catch is the important part, not the flush. This callback runs on a threadpool
        // thread for the life of the process, and anything that escapes it takes the whole app
        // down with no window, no message and nothing in the log — which is exactly what
        // happened the first time this shipped.
        _flushTimer = new System.Threading.Timer(_ => SafeFlush(), null, FlushInterval, FlushInterval);
    }

    public void Stop()
    {
        if (_subscription is null) return;

        _subscription.LineReceived -= OnLine;
        _subscription.Dispose();
        _subscription = null;

        _flushTimer?.Dispose();
        _flushTimer = null;

        // Whatever is queued is written now: the alternative is losing the last couple of
        // seconds of a session every time the dashboard closes.
        Flush();
    }

    private void SafeFlush()
    {
        try
        {
            Flush();
        }
        catch (Exception ex)
        {
            Raise(() => Failed?.Invoke(this, ex.Message));
        }
    }

    private void OnLine(object? sender, ChatLine line)
    {
        lock (_gate) _pending.Enqueue(line);
    }

    private void Flush()
    {
        ChatLine[] batch;

        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        try
        {
            Write(batch);
            LinesWritten += batch.Length;
            Raise(() => Progress?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put them back: a locked file or a full disk is usually temporary, and the next
            // flush should try again rather than silently dropping a session's chat.
            lock (_gate)
            {
                foreach (ChatLine line in batch.Reverse()) _pending.Enqueue(line);
            }

            Raise(() => Failed?.Invoke(this, ex.Message));
        }
    }

    private void Write(IReadOnlyList<ChatLine> batch)
    {
        Directory.CreateDirectory(_directory);

        // Grouped by the game's day, not this machine's, so a batch that straddles the game's
        // midnight lands in two files exactly as Afterline would have written it.
        foreach (IGrouping<DateTime, (ChatLine Line, DateTime GameTime)> day in batch
                     .Select(line => (Line: line, GameTime: _gameClock.TimeOf(line.Text, line.SeenAt)))
                     .GroupBy(entry => entry.GameTime.Date))
        {
            WriteDay(day.Key, day.ToArray());
        }
    }

    private void WriteDay(DateTime gameDay, IReadOnlyList<(ChatLine Line, DateTime GameTime)> entries)
    {
        string path = Path.Combine(_directory, FileName(gameDay));
        bool isNewFile = !File.Exists(path);

        var text = new StringBuilder();
        var meta = new StringBuilder();

        if (isNewFile)
        {
            // Afterline's opening, because LogPusher's parser and anyone reading the file by
            // eye both expect it.
            text.AppendLine(ServerHeader);
            text.AppendLine();
        }

        // A session running past the game's midnight is marked, the way Afterline marks it.
        //
        // Only within one run, though. _currentDay starts unset, so the original test also fired
        // on the first write after every restart — appending a rollover marker to an existing
        // file each time the dashboard was opened, which is not a date change at all.
        if (_currentDay != DateTime.MinValue && gameDay != _currentDay)
            text.AppendLine("==================== [DATE ROLLOVER] - 00:00:00 ====================");

        _currentDay = gameDay;

        foreach ((ChatLine line, DateTime gameTime) in entries)
        {
            // The HUD stamps most lines itself, and its stamp is the game's own. Where there
            // isn't one, the time is reconstructed on the game's clock rather than borrowing
            // this machine's — the whole file has to be on one clock to be worth anything.
            if (AlreadyStamped.IsMatch(line.Text))
                text.AppendLine(line.Text);
            else
                text.Append('[').Append(gameTime.ToString("HH:mm:ss")).Append("] ")
                    .AppendLine(line.Text);

            meta.AppendLine(JsonSerializer.Serialize(new LogRecord(
                line.Sequence,
                gameTime.ToString("HH:mm:ss"),
                gameTime.ToString("o"),
                line.SeenAt.ToString("o"),
                line.Text,
                ChatColour.Of(line.RawHtml))));
        }

        File.AppendAllText(path, text.ToString(), FileEncoding);
        File.AppendAllText(Path.ChangeExtension(path, ".lines.jsonl"), meta.ToString(), FileEncoding);

        CurrentFile = path;
    }

    /// <summary>The file a given game-day is written to. Exposed so the self-test can pin it.</summary>
    internal static string FileNameFor(DateTime day) => FileName(day);

    private static string FileName(DateTime day)
        // "01-September-2026". Zero-padded, because Afterline pads and the whole point of this
        // format is to be indistinguishable from theirs — the first three days of testing all
        // fell on two-digit dates, so an unpadded "d" looked correct right up until 1 September
        // produced a name no comparison could pair with Afterline's.
        //
        // Invariant culture on purpose too: the month name is matched by an English regex, and
        // this machine is not running an English locale.
        => string.Format(FileNameFormat, day.ToString("dd-MMMM-yyyy",
            System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <param name="Time">Game time of day, matching the text file.</param>
    /// <param name="GameTime">Full game timestamp — the clock every other player's log is on.</param>
    /// <param name="ReadAt">
    /// When the dashboard read the line, on this machine's clock. Kept only so the offset
    /// between the two is recoverable after the fact; nothing should prefer it.
    /// </param>
    private sealed record LogRecord(
        long Seq, string Time, string GameTime, string ReadAt, string Text, string? Colour);
}
