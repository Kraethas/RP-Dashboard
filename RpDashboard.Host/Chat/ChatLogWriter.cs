using System.IO;
using System.Text;
using System.Text.Json;
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
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Queue<ChatLine> _pending = new();

    private IChatSubscription? _subscription;
    private System.Threading.Timer? _flushTimer;
    private DateTime _currentDay = DateTime.MinValue;
    private bool _disposed;

    public ChatLogWriter(IChatFeed feed, string directory)
    {
        _feed = feed;
        _directory = directory;
    }

    /// <summary>Lines written to disk since the dashboard started.</summary>
    public long LinesWritten { get; private set; }

    /// <summary>The file currently being appended to, or null before the first line.</summary>
    public string? CurrentFile { get; private set; }

    public bool IsRunning => _subscription is not null;

    /// <summary>Raised when a line is written, for the status bar. On the UI thread.</summary>
    public event EventHandler? Progress;

    /// <summary>Raised with a human-readable reason when writing fails. On the UI thread.</summary>
    public event EventHandler<string>? Failed;

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
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, FlushInterval, FlushInterval);
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
            Progress?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put them back: a locked file or a full disk is usually temporary, and the next
            // flush should try again rather than silently dropping a session's chat.
            lock (_gate)
            {
                foreach (ChatLine line in batch.Reverse()) _pending.Enqueue(line);
            }

            Failed?.Invoke(this, ex.Message);
        }
    }

    private void Write(IReadOnlyList<ChatLine> batch)
    {
        Directory.CreateDirectory(_directory);

        DateTime day = batch[0].SeenAt.Date;
        string path = Path.Combine(_directory, FileName(day));
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

        if (day != _currentDay)
        {
            // A session running past midnight lands in the new day's file, marked, rather than
            // silently continuing in yesterday's. Afterline does the same.
            if (!isNewFile || _currentDay != DateTime.MinValue)
                text.AppendLine($"==================== [DATE ROLLOVER] - 00:00:00 ====================");

            _currentDay = day;
        }

        foreach (ChatLine line in batch)
        {
            text.Append('[').Append(line.SeenAt.ToString("HH:mm:ss")).Append("] ")
                .AppendLine(line.Text);

            meta.AppendLine(JsonSerializer.Serialize(new LogRecord(
                line.Sequence,
                line.SeenAt.ToString("HH:mm:ss"),
                line.SeenAt.ToString("o"),
                line.Text,
                ChatColour.Of(line.RawHtml))));
        }

        File.AppendAllText(path, text.ToString(), FileEncoding);
        File.AppendAllText(Path.ChangeExtension(path, ".lines.jsonl"), meta.ToString(), FileEncoding);

        CurrentFile = path;
    }

    private static string FileName(DateTime day)
        // "29-August-2026", the shape LogPusher's FILENAME_DATE_RE looks for. Invariant culture
        // on purpose: the month name is matched by an English regex, and this machine is not
        // running an English locale.
        => string.Format(FileNameFormat, day.ToString("d-MMMM-yyyy",
            System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed record LogRecord(long Seq, string Time, string Timestamp, string Text, string? Colour);
}
