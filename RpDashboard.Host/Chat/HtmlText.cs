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
        var tag = new StringBuilder(16);
        bool inTag = false;

        foreach (char c in html)
        {
            if (c == '<')
            {
                inTag = true;
                tag.Clear();
            }
            else if (c == '>')
            {
                inTag = false;

                // A line break is the one tag that means whitespace. Everything else is
                // presentation: the HUD renders adjacent spans with nothing between them, so
                // inserting anything here puts a space where the player saw none.
                //
                // An earlier version added a space at every tag boundary, on the theory that a
                // callsign split across two spans would otherwise run together. Comparing a real
                // session against Afterline showed what it actually did: "[ CHAT ]", "un- realized",
                // "selfie , old man" — every difference between the two logs, and none of them real.
                if (IsBreak(tag)) text.Append(' ');
            }
            else if (inTag)
            {
                tag.Append(c);
            }
            else
            {
                text.Append(c);
            }
        }

        // GTAW's markup carries entities — &amp; in OOC, &#39; in contractions, nbsp for
        // alignment. Matching on the decoded form is what the tools have always seen.
        string decoded = WebUtility.HtmlDecode(text.ToString());

        return CollapseWhitespace(decoded);
    }

    /// <summary>&lt;br&gt;, &lt;br/&gt; and &lt;br /&gt;, however they are spelled.</summary>
    private static bool IsBreak(StringBuilder tag)
    {
        int i = 0;
        while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;

        if (i + 1 >= tag.Length + 1) return false;
        if (i >= tag.Length || char.ToLowerInvariant(tag[i]) != 'b') return false;
        if (i + 1 >= tag.Length || char.ToLowerInvariant(tag[i + 1]) != 'r') return false;

        // "br", "br/", "br /" — but not "brand" or anything else starting br.
        for (int j = i + 2; j < tag.Length; j++)
        {
            if (!char.IsWhiteSpace(tag[j]) && tag[j] != '/') return false;
        }

        return true;
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
