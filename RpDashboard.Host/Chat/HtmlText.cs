using System.Net;
using System.Text;

namespace RpDashboard.Host.Chat;

/// <summary>
/// Turns one chat message's outerHTML into the plain text the matching tools want.
///
/// This is the work that used to happen inside the game, once per tool, as
/// <c>innerText.replace(/\s+/g,' ')</c> in the evaluated expression. Doing it here means the
/// HUD serialises one array per poll instead of two, and three tools share one result.
///
/// It is deliberately a tag stripper and not an HTML parser. The input is a single <c>&lt;li&gt;</c>
/// of GTAW's own markup — spans with inline colour, the occasional <c>&lt;br&gt;</c> — not
/// arbitrary web content, and the colour that matters to Street Medic is read off the raw
/// form anyway, not this one.
/// </summary>
internal static class HtmlText
{
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var text = new StringBuilder(html.Length);
        bool inTag = false;

        foreach (char c in html)
        {
            if (c == '<')
            {
                inTag = true;
                // A tag boundary is a word boundary: "<span>FD</span><span>-1</span>" must not
                // become "FD-1" by accident in one place and "FD -1" in another. A space here,
                // collapsed below, keeps that consistent.
                text.Append(' ');
            }
            else if (c == '>')
            {
                inTag = false;
            }
            else if (!inTag)
            {
                text.Append(c);
            }
        }

        // GTAW's markup carries entities — &amp; in OOC, &#39; in contractions, nbsp for
        // alignment. Matching on the decoded form is what the tools have always seen.
        string decoded = WebUtility.HtmlDecode(text.ToString());

        return CollapseWhitespace(decoded);
    }

    private static string CollapseWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        bool lastWasSpace = true; // leading whitespace is dropped

        foreach (char c in value)
        {
            // char.IsWhiteSpace covers the non-breaking space GTAW pads with, which matters:
            // it reads as a space so it has to match as one, or a callsign padded with one
            // silently stops matching.
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) result.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                result.Append(c);
                lastWasSpace = false;
            }
        }

        return result.ToString().TrimEnd();
    }
}
