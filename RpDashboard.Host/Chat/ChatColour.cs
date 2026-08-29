using System.Text.RegularExpressions;

namespace RpDashboard.Host.Chat;

/// <summary>
/// The colour GTA World drew a chat line in.
///
/// The <c>li</c> carries its own white, so reading the first colour in the markup reports white
/// for every line regardless of type. The first inner <c>span</c> is the one that means
/// something — that is where #28b4bf (OOC) and #ffec8b (radio) live.
///
/// Street Medic derives the same value for its own classification; this copy exists because the
/// log writer needs it in the host, and the two must agree, so the rule is written down once
/// here and once there rather than being inferred from the markup twice differently.
/// </summary>
internal static class ChatColour
{
    private const string ColourValue = @"(?<value>#[0-9a-f]{3,8}|rgba?\([^)]*\))";

    private static readonly Regex SpanColour = new(
        @"<span[^>]*style\s*=\s*[""'][^""']*color\s*:\s*" + ColourValue,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RgbParts = new(
        @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Lowercase long-form hex, or null when the markup carries no span colour.</summary>
    public static string? Of(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        Match match = SpanColour.Match(html);
        return match.Success ? Canonical(match.Groups["value"].Value) : null;
    }

    /// <summary>
    /// One spelling per colour, so a value written as #ff0000 still matches a line the HUD
    /// rendered as rgb(255, 0, 0).
    /// </summary>
    private static string Canonical(string colour)
    {
        Match rgb = RgbParts.Match(colour);

        if (rgb.Success)
        {
            var channels = Enumerable.Range(1, 3)
                .Select(i => Math.Clamp(int.Parse(rgb.Groups[i].Value), 0, 255));
            return "#" + string.Concat(channels.Select(v => v.ToString("x2")));
        }

        string hex = Regex.Replace(colour, @"\s+", string.Empty).ToLowerInvariant();

        // #abc is the same colour as #aabbcc; store the long form.
        return hex.Length == 4 && hex[0] == '#'
            ? "#" + string.Concat(hex[1..].Select(c => $"{c}{c}"))
            : hex;
    }
}
