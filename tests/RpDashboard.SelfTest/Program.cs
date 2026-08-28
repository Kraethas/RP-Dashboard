using System.IO;
using RpDashboard.Contracts;
using RpDashboard.Host.Chat;
using RpDashboard.Host.Hosting;

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

        Section("new-line detection (the scrolling-window overlap rule)");
        NewLineTests();

        Section("outerHTML to plain text");
        HtmlTests();

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

        // A tag boundary has to read as a word boundary, or two spans meeting mid-callsign
        // silently join into something that no longer matches what the user typed.
        CheckText("a tag boundary separates words",
            "<li><span>FD</span><span>-1</span></li>",
            "FD -1");

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
