using System.IO;
using System.Windows.Threading;
using RpDashboard.Contracts;
using RpDashboard.Host.Chat;
using RpDashboard.Host.Hosting;
using RpDashboard.Host.Services;

namespace RpDashboard.SelfTest;

/// <summary>
/// Exercises the two pieces of real logic in the host without opening a window or a socket.
///
/// Both of them used to live inside each tool, privately, three times over. Now one copy feeds
/// all three tabs, so a mistake here is a mistake in every tool at once — which is the whole
/// reason this file exists. `dotnet run --project tests/RpDashboard.SelfTest`.
/// </summary>
public static class Program
{
    private static int _failures;
    private static int _checks;

    public static int Main(string[] args)
    {
        if (args.Contains("--live")) return Live.Run().GetAwaiter().GetResult();

        if (args.Contains("--sample"))
        {
            // Writes a log through the real ChatLogWriter and leaves it, so LogPusher's Python
            // parser can be pointed at genuine output rather than at a hand-made imitation of it.
            int at = Array.IndexOf(args, "--sample");
            return WriteSample(at + 1 < args.Length ? args[at + 1] : ".");
        }

        if (args.Contains("--compare"))
        {
            int at = Array.IndexOf(args, "--compare");
            return Compare.Run(at + 1 < args.Length ? args[at + 1] : null);
        }

        Section("new-line detection (the scrolling-window overlap rule)");
        NewLineTests();

        Section("recovering the gap on reconnect");
        ReconnectTests();

        Section("outerHTML to plain text");
        HtmlTests();

        Section("chat log writing");
        ChatLogTests();

        Section("start with Windows");
        StartupTests();

        Section("one instance at a time");
        SingleInstanceTests();

        Section("plugin loading");
        PluginTests();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"all {_checks} checks passed"
            : $"{_failures} of {_checks} checks FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void NewLineTests()
    {
        // The ordinary case: the buffer has room, one message is appended.
        Check("appends are new",
            New(["a", "b"], ["a", "b", "c"]), ["c"]);

        // The buffer is full, so the oldest falls off the top as the newest arrives. Getting
        // this wrong is what would make a full chat log re-announce every line, every poll.
        Check("a scrolled buffer reports only the new tail",
            New(["a", "b", "c"], ["b", "c", "d"]), ["d"]);

        Check("several at once, scrolled",
            New(["a", "b", "c"], ["c", "d", "e"]), ["d", "e"]);

        Check("nothing changed means nothing new",
            New(["a", "b", "c"], ["a", "b", "c"]), []);

        // No overlap at all: the game was away long enough for the buffer to roll completely,
        // or the HUD was cleared. Everything visible is genuinely new.
        Check("a total rollover reports everything",
            New(["a", "b"], ["x", "y"]), ["x", "y"]);

        Check("an empty read reports nothing",
            New(["a", "b"], []), []);

        // Repeated identical lines are common in chat — a callsign paged twice, an emote
        // repeated. The longest-overlap rule has to prefer the longer match, or the second
        // copy is silently swallowed as if it were the first.
        Check("a repeated line still counts as new",
            New(["a", "b"], ["a", "b", "b"]), ["b"]);

        Check("consecutive duplicates survive a scroll",
            New(["x", "a", "a"], ["a", "a", "a"]), ["a"]);
    }

    private static void ReconnectTests()
    {
        // A brief drop, and the buffer still holds the window we last saw. The two lines that
        // arrived while we were away are recovered rather than lost — this is the real case from
        // 5 September, where a two-minute stall cost two lines that baselining had thrown away.
        var (lines, gap) = Recover(["a", "b"], ["a", "b", "c", "d"], within: true);
        Check("a short drop recovers the missed tail", lines, ["c", "d"]);
        Check("and does not flag a gap", !gap);

        // Cold start: no earlier window to trust, so the buffer is pre-existing backlog. Nothing
        // is published — this is the invariant that stops a tool switched on mid-session firing
        // on everything already on screen.
        (lines, gap) = Recover([], ["a", "b", "c"], within: true);
        Check("a cold start baselines, publishing nothing", lines, []);
        Check("a cold start is not a gap", !gap);

        // The buffer rolled clean over during the outage: no overlap, so the window cannot be
        // lined up. Baseline, and say so.
        (lines, gap) = Recover(["a", "b"], ["x", "y", "z"], within: true);
        Check("a rolled-over buffer recovers nothing", lines, []);
        Check("and flags the gap", gap);

        // A long outage — the game was closed and reopened later. Even if the stale window happens
        // to share a line with the new session, the time guard refuses to trust it.
        (lines, gap) = Recover(["a", "b"], ["a", "b", "c"], within: false);
        Check("a long outage baselines regardless of overlap", lines, []);
        Check("a long outage is a clean baseline, not a gap", !gap);
    }

    private static (IReadOnlyList<string> lines, bool gap) Recover(
        string[] previous, string[] current, bool within)
    {
        IReadOnlyList<string> lines = ChatFeed.RecoverOnReconnect(previous, current, within, out bool gap);
        return (lines, gap);
    }

    private static void HtmlTests()
    {
        // Colour is how GTAW distinguishes /me, /do, OOC and speech, so the raw form keeps the
        // span; the plain form is what the matching tools see.
        CheckText("spans are stripped",
            "<li><span style=\"color:#C2A2DA\">* Ilya Morozov checks a pulse.</span></li>",
            "* Ilya Morozov checks a pulse.");

        CheckText("entities are decoded",
            "<li><span>Nikos says: that&#39;s him &amp; his brother</span></li>",
            "Nikos says: that's him & his brother");

        // Adjacent spans render with nothing between them, so nothing goes between them here.
        // The opposite rule — a space at every tag boundary — was tried, and comparing a real
        // session against Afterline showed exactly what it cost: "[ CHAT ]", "un- realized",
        // "selfie , old man". Every difference between the two logs, and not one of them real.
        CheckText("adjacent spans are not separated",
            "<li><span>FD</span><span>-1</span></li>",
            "FD-1");

        CheckText("a line break is whitespace",
            "<li><span>one</span><br><span>two</span></li>",
            "one two");

        CheckText("a self-closed break too",
            "<li>one<br />two</li>", "one two");

        CheckText("a tag merely starting br is not a break",
            "<li><brand>one</brand>two</li>", "onetwo");

        CheckText("whitespace collapses",
            "<li>  dispatch\n\n   to   all   units  </li>",
            "dispatch to all units");

        // GTAW pads with non-breaking spaces. They read as spaces, so they have to match as
        // spaces — otherwise a padded callsign quietly stops firing.
        CheckText("a non-breaking space is a space",
            "<li>FD-1&nbsp;&nbsp;responding</li>",
            "FD-1 responding");

        CheckText("an empty element is empty", "<li></li>", "");
    }

    /// <summary>
    /// Loads the real Plugins folder the way the dashboard does at startup.
    ///
    /// This is the check that the plugin split actually holds: it proves a DLL built from a
    /// separate project, with no compile-time link to the host, is found, version-matched and
    /// instantiated through the contract alone. If the contract ever drifts, this fails here
    /// rather than as an empty window.
    /// </summary>
    private static int WriteSample(string directory)
    {
        var feed = new FakeFeed();
        using var writer = new ChatLogWriter(feed, Dispatcher.CurrentDispatcher, directory);
        writer.Start();

        var start = new DateTime(2026, 8, 29, 9, 30, 0);
        (string text, string colour)[] script =
        {
            ("Stats for Ilya Morozov(#65887) (Kraethas#123456)", "#1e90ff"),
            ("Ilya Morozov says: evening.", "#ffffff"),
            ("* Ilya Morozov wipes his hands on his apron.", "#c2a2da"),
            ("(( Marta Alvarez: brb ))", "#28b4bf"),
            ("* The register jams. ((Ilya Morozov))", "#c2a2da"),
            ("Ilya Morozov says: evening.", "#ffffff"),
        };

        for (int i = 0; i < script.Length; i++)
        {
            feed.Emit(new ChatLine(
                i + 1, script[i].text,
                $"<li style=\"color: rgb(255,255,255);\"><span style=\"color:{script[i].colour}\">x</span></li>",
                start.AddSeconds(i * 30)));
        }

        writer.Stop();
        Console.WriteLine(writer.CurrentFile);
        return 0;
    }

    /// <summary>
    /// The log the dashboard writes has to be readable by LogPusher without changing LogPusher,
    /// which means matching Afterline's filename shape and line format exactly. That is the
    /// whole reason the format was not designed freely.
    /// </summary>
    private static void ChatLogTests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rp-dashboard-log-" + Guid.NewGuid().ToString("N"));

        try
        {
            var feed = new FakeFeed();
            using (var writer = new ChatLogWriter(feed, Dispatcher.CurrentDispatcher, directory))
            {
                writer.Start();

                var seen = new DateTime(2026, 8, 29, 14, 3, 11);
                feed.Emit(new ChatLine(1, "Ilya Morozov says: evening.",
                    "<li><span style=\"color:#ffffff\">x</span></li>", seen));
                feed.Emit(new ChatLine(2, "(( Marta: brb ))",
                    "<li><span style=\"color:#28B4BF\">x</span></li>", seen.AddSeconds(4)));

                // GTAW's HUD stamps most lines itself, so this is what the feed usually hands over.
                feed.Emit(new ChatLine(3, "[21:13:01] Welcome to GTA World .",
                    "<li><span style=\"color:#1e90ff\">x</span></li>", seen.AddSeconds(9)));

                writer.Stop();
            }

            string[] files = Directory.GetFiles(directory);
            string? text = files.FirstOrDefault(f => f.EndsWith(".txt", StringComparison.Ordinal));
            string? meta = files.FirstOrDefault(f => f.EndsWith(".lines.jsonl", StringComparison.Ordinal));

            _checks++;
            bool named = text is not null &&
                         Path.GetFileName(text) == "Chatlog [GTAWorld Roleplay English] [29-August-2026].txt";
            Console.WriteLine(named
                ? "  ok   the filename is the one LogPusher recognises"
                : $"  FAIL filename was {(text is null ? "(none)" : Path.GetFileName(text))}");
            if (!named) _failures++;

            // Afterline zero-pads the day. Three days of testing all landed on two-digit dates,
            // so an unpadded "d" looked right until 1 September produced "[1-September-2026]"
            // and nothing could pair it with Afterline's "[01-September-2026]".
            Check("single-digit days are zero-padded, as Afterline pads them",
                ChatLogWriter.FileNameFor(new DateTime(2026, 9, 1))
                    == "Chatlog [GTAWorld Roleplay English] [01-September-2026].txt");

            string[] lines = text is null ? Array.Empty<string>() : File.ReadAllLines(text);

            Check("the server header is written first",
                lines.FirstOrDefault() == "[SERVER: GTAWorld Roleplay English]");

            Check("lines carry a [HH:MM:SS] stamp and the text",
                lines.Any(l => l == "[14:03:11] Ilya Morozov says: evening."));

            Check("every line is written exactly once",
                lines.Count(l => l.EndsWith("evening.", StringComparison.Ordinal)) == 1);

            // "[00:19:45] [21:13:01] Welcome..." is what the first version wrote: two stamps,
            // the outer one merely recording when we read it, and a stray one left in the body
            // once LogPusher strips the first.
            Check("a line the game already stamped is not stamped again",
                lines.Any(l => l == "[21:13:01] Welcome to GTA World ."));

            Check("no line carries two timestamps",
                !lines.Any(l => System.Text.RegularExpressions.Regex.IsMatch(
                    l, @"^\[\d{2}:\d{2}:\d{2}\]\s+\[\d{2}:\d{2}:\d{2}\]")));

            // _currentDay starts unset, so the original rollover test fired on the first write
            // of every run — marking a date change each time the dashboard was merely reopened.
            Check("reopening does not fake a date rollover",
                !lines.Any(l => l.Contains("DATE ROLLOVER", StringComparison.Ordinal)));

            // The sidecar is what Afterline throws away: colour, which is the raw material for
            // the constants Street Medic is still missing.
            string metaText = meta is null ? "" : File.ReadAllText(meta);
            Check("the sidecar records the colour", metaText.Contains("#28b4bf"));
            Check("the sidecar records the sequence", metaText.Contains("\"Seq\":2"));

            // Afterline's sidecar keeps colour as per-character runs. Keeping the markup keeps
            // that and everything else it was derived from, so switching Afterline off loses no
            // information rather than losing whatever nobody had thought to extract yet.
            Check("the sidecar archives the original markup",
                metaText.Contains("\"Html\":") && metaText.Contains("<span"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* temp */ }
        }

        TimerThreadTest();
        GameClockTests();
    }

    /// <summary>
    /// The log is kept on the game's clock, not this machine's.
    ///
    /// GTA World stamps with the server's time — three hours off this machine when measured —
    /// and every other player's log is on that clock. A log that cannot be lined up against
    /// someone else's is worth much less if it is ever needed.
    ///
    /// The first version named files by the local date while writing the game's timestamps
    /// inside them, so one session was filed under a single day here while Afterline split it
    /// across two. LogPusher takes a session's date from the filename and its times from the
    /// body, so those two disagreeing is exactly the kind of quiet mismatch that has already
    /// cost this project twice.
    /// </summary>
    private static void GameClockTests()
    {
        var clock = new GameClock();

        // The measured case: read at 00:50 local, stamped 21:50 by the game. That is the
        // previous game day, not this one.
        DateTime read = new(2026, 8, 30, 0, 50, 5);
        DateTime game = clock.TimeOf("[21:50:05] (( PM to Luna : ooooh ))", read);

        Check("a stamp three hours behind resolves to the previous game day",
            game == new DateTime(2026, 8, 29, 21, 50, 5));

        Check("and the offset is learned", clock.OffsetKnown && clock.Offset == TimeSpan.FromHours(-3));

        // Later the same run, past the game's midnight.
        DateTime later = clock.TimeOf("[01:10:44] Vehicle parked.", new DateTime(2026, 8, 30, 4, 10, 44));
        Check("after the game's midnight it moves to the next game day",
            later == new DateTime(2026, 8, 30, 1, 10, 44));

        // Unstamped lines carry the offset forward rather than falling back to local time.
        DateTime unstamped = clock.TimeOf("no stamp here", new DateTime(2026, 8, 30, 4, 20, 0));
        Check("an unstamped line is dated on the game's clock too",
            unstamped == new DateTime(2026, 8, 30, 1, 20, 0));

        // The offset runs the other way for a server ahead of local time.
        var ahead = new GameClock();
        DateTime aheadGame = ahead.TimeOf("[00:30:00] x", new DateTime(2026, 8, 29, 23, 30, 0));
        Check("a server ahead of local time resolves to the next game day",
            aheadGame == new DateTime(2026, 8, 30, 0, 30, 0));
    }

    /// <summary>
    /// The flush timer runs on a threadpool thread, and this is the regression test for what
    /// that cost the first time: the writer raised Progress straight from that thread, a handler
    /// set a TextBlock, WPF threw, and nothing catches an exception on a threadpool thread — so
    /// the app died a few seconds after launch with nothing in any log.
    ///
    /// The test above never caught it because it calls Stop(), which flushes on the calling
    /// thread. Only the timer path has the bug, so only the timer path proves the fix.
    /// </summary>
    private static void TimerThreadTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rp-dashboard-timer-" + Guid.NewGuid().ToString("N"));
        int uiThread = Environment.CurrentManagedThreadId;
        int handlerThread = 0;
        var exploded = false;

        try
        {
            var feed = new FakeFeed();
            using (var writer = new ChatLogWriter(feed, Dispatcher.CurrentDispatcher, directory))
            {
                writer.Start();

                // A handler of exactly the kind that crashed it: one that would touch the UI.
                writer.Progress += (_, _) =>
                {
                    handlerThread = Environment.CurrentManagedThreadId;
                    exploded = true;
                };

                feed.Emit(new ChatLine(1, "timer path", "<li><span>x</span></li>", DateTime.Now));

                // Long enough for the 2s flush timer to fire on its own.
                Thread.Sleep(2600);

                Check("the timer wrote without being told to",
                    Directory.Exists(directory) && Directory.GetFiles(directory, "*.txt").Length == 1);

                // Nothing pumps a dispatcher in a console app, so a properly marshalled Progress
                // is queued and never runs here. If it did run, it ran on the timer's thread.
                Check("Progress is not raised on the timer's thread",
                    !exploded || handlerThread == uiThread);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>A feed that emits exactly what a test tells it to.</summary>
    private sealed class FakeFeed : IChatFeed
    {
        private readonly List<Subscription> _subscriptions = new();

        public bool IsConnected => true;
        public ChatFeedStatus Status => new(ChatFeedState.Reading, "fake");
        public event EventHandler<ChatFeedStatus>? StatusChanged;

        public IChatSubscription Subscribe(ChatSubscriptionOptions options)
        {
            var subscription = new Subscription(this);
            _subscriptions.Add(subscription);
            _ = StatusChanged;
            return subscription;
        }

        public Task<IReadOnlyList<ChatLine>> SnapshotAsync(int maxLines, CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ChatLine>>(Array.Empty<ChatLine>());

        public void Emit(ChatLine line)
        {
            foreach (Subscription subscription in _subscriptions.ToArray())
                subscription.Deliver(line);
        }

        private sealed class Subscription : IChatSubscription
        {
            private readonly FakeFeed _feed;
            public Subscription(FakeFeed feed) => _feed = feed;

            public event EventHandler<ChatLine>? LineReceived;
            public bool IsPaused { get; set; }

            public void Deliver(ChatLine line)
            {
                if (!IsPaused) LineReceived?.Invoke(this, line);
            }

            public void Dispose() => _feed._subscriptions.Remove(this);
        }
    }

    private static void Check(string name, bool ok)
    {
        _checks++;
        Console.WriteLine(ok ? $"  ok   {name}" : $"  FAIL {name}");
        if (!ok) _failures++;
    }

    /// <summary>
    /// Exercises the real per-user Run key, under a name of its own.
    ///
    /// Against the real registry, because the two things that go wrong here are quoting a path
    /// that contains spaces and leaving a stale entry behind on disable, and neither shows up
    /// against a stub. But never against the real value: this test used to save and restore
    /// "RP Dashboard" around itself, which meant every run briefly switched off a setting whose
    /// entire purpose is to be on when nobody is watching — and left it off for good if the
    /// restore ever failed to happen.
    /// </summary>
    private static void StartupTests()
    {
        const string testName = "RP Dashboard (self-test)";
        string? realBefore = StartupRegistration.RegisteredCommand;

        StartupRegistration.Name = testName;

        try
        {
            StartupRegistration.Disable();
            Check("starts from a known-clean state", !StartupRegistration.IsEnabled);

            Check("enabling reports success", StartupRegistration.Enable());
            Check("and the entry is there", StartupRegistration.IsEnabled);

            string command = StartupRegistration.RegisteredCommand ?? "";

            // The dashboard lives under "000 - Claude Code stuff", so an unquoted path would be
            // read by Windows as a command plus arguments and silently fail to launch.
            Check("the executable path is quoted", command.StartsWith("\"", StringComparison.Ordinal));

            Check("it starts to the tray",
                command.EndsWith(StartupRegistration.TrayArgument, StringComparison.Ordinal));

            Check("it points at this executable",
                Environment.ProcessPath is null || command.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase));

            Check("enabling twice is not an error", StartupRegistration.Enable());

            Check("disabling reports success", StartupRegistration.Disable());
            Check("and the entry is gone", !StartupRegistration.IsEnabled);
            Check("disabling twice is not an error", StartupRegistration.Disable());
        }
        finally
        {
            StartupRegistration.Disable();          // clear the test's own entry
            StartupRegistration.Name = StartupRegistration.ValueName;
        }

        Check("the real setting was never touched",
            StartupRegistration.RegisteredCommand == realBefore);
    }

    private static void SingleInstanceTests()
    {
        // A name of its own, because the real dashboard is very likely running while this test
        // does, and signalling it would pop its window up in the middle of a test run.
        const string name = "RpDashboard.SelfTest";

        using var first = new SingleInstance(name);
        using var requested = new ManualResetEventSlim();
        first.ShowRequested += requested.Set;

        Check("the first launch claims the instance", first.TryClaim());

        using (var second = new SingleInstance(name))
        {
            Check("a second launch is refused", !second.TryClaim());
            Check("and the first is asked to show itself", requested.Wait(TimeSpan.FromSeconds(5)));
        }

        requested.Reset();
        Check("the first is still running after that", !new SingleInstance(name).TryClaim());
        Check("(which asked it to show itself again)", requested.Wait(TimeSpan.FromSeconds(5)));

        first.Dispose();

        using var next = new SingleInstance(name);
        Check("once the first exits, the next launch is first", next.TryClaim());
    }

    private static void PluginTests()
    {
        string? plugins = FindPluginsFolder();

        if (plugins is null)
        {
            // Saying so is better than a green run that tested nothing. Build a plugin project
            // first: `dotnet build plugins/UnitAlert/UnitAlert.Plugin.csproj`.
            Console.WriteLine("  --   no Plugins folder found, skipped");
            return;
        }

        Console.WriteLine($"       {plugins}");

        PluginScanResult scan = PluginLoader.Load(plugins);

        _checks++;
        if (scan.Problems.Count == 0)
        {
            Console.WriteLine("  ok   every DLL in the folder loaded");
        }
        else
        {
            _failures++;
            Console.WriteLine("  FAIL some plugins reported problems");
            foreach (PluginProblem problem in scan.Problems)
                Console.WriteLine($"         {problem.FileName}: {problem.Message}");
        }

        _checks++;
        if (scan.Tools.Count > 0)
        {
            Console.WriteLine($"  ok   {scan.Tools.Count} tool(s) found");
            foreach (LoadedTool loaded in scan.Tools)
                Console.WriteLine($"         {loaded.Tool.Title} ({loaded.Tool.Id}) " +
                                  $"v{loaded.AssemblyVersion} settings={loaded.Tool.SettingsFolderName}");
        }
        else
        {
            _failures++;
            Console.WriteLine("  FAIL no tools found");
        }

        // Type identity is the failure mode worth naming: a plugin that shipped its own copy of
        // the contracts assembly implements a *different* IRpTool, and the cast the host does
        // would throw between two types that print with the same name.
        foreach (LoadedTool loaded in scan.Tools)
        {
            _checks++;
            bool sameContract = ReferenceEquals(
                loaded.Tool.GetType().GetInterface(nameof(IRpTool))?.Assembly,
                typeof(IRpTool).Assembly);

            Console.WriteLine(sameContract
                ? $"  ok   {loaded.Tool.Title} binds to the host's contract assembly"
                : $"  FAIL {loaded.Tool.Title} loaded its own copy of RpDashboard.Contracts");

            if (!sameContract) _failures++;
        }
    }

    /// <summary>
    /// The dashboard reads plugins from beside its own exe; this test runs from a different
    /// output folder, so it walks up to the repository root and looks in the host's build
    /// output — the same DLLs the app would load.
    /// </summary>
    private static string? FindPluginsFolder()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Plugins");
        if (Directory.Exists(local)) return local;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RpDashboard.sln")))
            {
                foreach (string configuration in new[] { "Debug", "Release" })
                {
                    string candidate = Path.Combine(
                        directory.FullName, "RpDashboard.Host", "bin", configuration, "net8.0-windows", "Plugins");

                    if (Directory.Exists(candidate)) return candidate;
                }

                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IReadOnlyList<string> New(string[] previous, string[] current)
        => ChatFeed.NewLinesSince(previous, current);

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine(name);
    }

    private static void Check(string name, IReadOnlyList<string> actual, string[] expected)
    {
        _checks++;
        bool ok = actual.Count == expected.Length && actual.Zip(expected).All(p => p.First == p.Second);

        Console.WriteLine(ok
            ? $"  ok   {name}"
            : $"  FAIL {name}\n         expected [{string.Join(", ", expected)}]" +
              $"\n         got      [{string.Join(", ", actual)}]");

        if (!ok) _failures++;
    }

    private static void CheckText(string name, string html, string expected)
    {
        _checks++;
        string actual = HtmlText.ToPlainText(html);
        bool ok = actual == expected;

        Console.WriteLine(ok
            ? $"  ok   {name}"
            : $"  FAIL {name}\n         expected \"{expected}\"\n         got      \"{actual}\"");

        if (!ok) _failures++;
    }
}
