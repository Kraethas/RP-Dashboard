using RpDashboard.Contracts;
using RpDashboard.Host.Chat;

namespace RpDashboard.SelfTest;

/// <summary>
/// Reads the live game, once, and prints what came back.
///
/// Everything else in this project runs without FiveM. This does the opposite: it is the check
/// that the whole chain still holds against a real session — the DevTools handshake, the frame
/// hunt, the selector, and the two derivations the tools depend on. The selector and the markup
/// are GTAW's, not ours, and they can change without notice; when a tool goes quiet this is the
/// first thing to run.
///
/// `dotnet run --project tests/RpDashboard.SelfTest -- --live`
/// </summary>
internal static class Live
{
    public static async Task<int> Run()
    {
        Console.WriteLine("Reading the live chat...");
        Console.WriteLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var feed = new ChatFeed(System.Windows.Threading.Dispatcher.CurrentDispatcher);

        IReadOnlyList<ChatLine> lines;

        try
        {
            lines = await feed.SnapshotAsync(40, cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("  Is FiveM running, and are you actually in a session?");
            Console.WriteLine("  The HUD only exists once you have connected to a server.");
            return 1;
        }
        finally
        {
            await feed.DisposeAsync();
        }

        if (lines.Count == 0)
        {
            Console.WriteLine("Connected, but the chat list is empty.");
            Console.WriteLine();
            Console.WriteLine("  The frame was found, so '.chat__messages > li' matched nothing.");
            Console.WriteLine("  Either the chat really is empty, or that selector is what changed.");
            return 1;
        }

        Console.WriteLine($"{lines.Count} line(s) visible. The last few, as each tool sees them:");
        Console.WriteLine();

        foreach (ChatLine line in lines.TakeLast(8))
        {
            Console.WriteLine($"  text   {Trim(line.Text, 96)}");
            Console.WriteLine($"  raw    {Trim(line.RawHtml, 96)}");
            Console.WriteLine();
        }

        // The one thing the merge changed for Street Medic: it classifies on the span colour,
        // which only survives because the feed carries outerHTML rather than text. If the raw
        // form has no colour in it, that tool is running blind and the constants can't be filled in.
        int coloured = lines.Count(line => line.RawHtml.Contains("color", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"colour present on {coloured} of {lines.Count} lines"
                          + (coloured == 0 ? "  <-- Street Medic classifies on this" : ""));

        int empty = lines.Count(line => string.IsNullOrWhiteSpace(line.Text));
        Console.WriteLine($"empty after tag-stripping: {empty}"
                          + (empty > 0 ? "  <-- HtmlText lost something" : ""));

        ReportColours(lines);
        return 0;
    }

    /// <summary>
    /// Groups what is on screen by the colour GTAW drew it in.
    ///
    /// This is the capture Street Medic's ChatLineReader.KnownColours has been waiting for. That
    /// table is deliberately empty with a TODO, because a guessed constant is worse than none: it
    /// silently overrides the text patterns, which work. An observed one is a different thing.
    /// Read a line whose type you know off this list and add its colour to the table.
    /// </summary>
    private static void ReportColours(IReadOnlyList<ChatLine> lines)
    {
        Console.WriteLine();
        Console.WriteLine("colours on screen, with a sample of each:");
        Console.WriteLine();

        var groups = lines
            .Select(line => new { Colour = FirstSpanColour(line.RawHtml), line.Text })
            .Where(x => x.Colour is not null)
            .GroupBy(x => x.Colour!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
        {
            Console.WriteLine($"  {group.Key}  x{group.Count()}");
            foreach (var sample in group.Take(2))
                Console.WriteLine($"      {Trim(sample.Text, 88)}");
        }

        int uncoloured = lines.Count(line => FirstSpanColour(line.RawHtml) is null);
        if (uncoloured > 0) Console.WriteLine($"  (no span colour on {uncoloured} line(s))");
    }

    /// <summary>
    /// The colour on the first inner span. The li carries its own white, so reading the first
    /// colour in the markup would report white for every line — the span is the one that means
    /// something. Same rule as ChatLineReader.SpanColour.
    /// </summary>
    private static string? FirstSpanColour(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            @"<span[^>]*style\s*=\s*[""'][^""']*color\s*:\s*(?<value>#[0-9a-f]{3,8}|rgba?\([^)]*\))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups["value"].Value.ToLowerInvariant() : null;
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..(max - 3)] + "...";
}
