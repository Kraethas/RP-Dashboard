using System.Text.RegularExpressions;

namespace UnitAlert;

public enum HitKind
{
    Callsign,
    Afk
}

/// <summary>A callsign pattern paired with the sound it should play. Empty means "use the default sound".</summary>
public sealed record CallsignRule(string Pattern, string SoundFile);

public sealed class MatcherOptions
{
    public IReadOnlyList<CallsignRule> Callsigns { get; init; } = Array.Empty<CallsignRule>();
    public string DefaultSoundFile { get; init; } = string.Empty;
    public bool WholeWord { get; init; }
    public bool RegexMode { get; init; }
    public bool AfkEnabled { get; init; }
    public string AfkSoundFile { get; init; } = string.Empty;
}

/// <summary>A single alert firing: which line triggered it, what matched, and which sound to play.</summary>
public sealed record AlertHit(DateTime TimestampUtc, HitKind Kind, string MatchedText, string Line, string SoundFile);

/// <summary>
/// Decides whether a chat line is worth a sound.
///
/// This is the old <c>AlertWatcher</c> with everything that wasn't matching taken out of it:
/// the DevTools reader, the 500 ms poll loop, the snapshot-overlap new-line detection, the
/// baseline-on-connect rule, and the FiveM process check are all gone. The host does those
/// once for every tool now, and hands this class one line at a time through
/// <see cref="Evaluate"/>.
///
/// What's left is the part that was ever actually Unit Alert's: the matching modes, the sound
/// resolution, and the two cooldowns. Those are unchanged, deliberately — they are the
/// behaviour people have tuned their callsign lists against.
/// </summary>
public sealed class AlertMatcher
{
    /// <summary>
    /// Fixed, not user-editable: the AFK toggle just turns detection of this literal on or off.
    /// Matches GTA World's "[AFK CHECK] You're considered AFK, type /notafk to confirm that
    /// you're playing." If the server ever changes that wording, this is the one constant to update.
    /// </summary>
    private const string AfkKeyword = "[AFK CHECK]";

    /// <summary>How long the same line is ignored after it fires, so one shout can't loop the sound.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, DateTime> _recentlyFired = new(StringComparer.Ordinal);
    private readonly Dictionary<HitKind, DateTime> _lastFiredByKind = new();
    private readonly Dictionary<string, Regex?> _compiledPatterns = new(StringComparer.Ordinal);

    private CallsignRule[] _callsignRules = Array.Empty<CallsignRule>();
    private string _defaultSoundFile = string.Empty;
    private bool _wholeWord;
    private bool _regexMode;
    private bool _afkEnabled;
    private string _afkSoundFile = string.Empty;

    /// <summary>Minimum gap between two alerts of the same kind. Callsign and AFK track separately.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised once per bad regex pattern, so it can be surfaced instead of silently ignored.</summary>
    public event Action<string>? PatternWarning;

    /// <summary>Raised for every alert that fires, with the sound already resolved.</summary>
    public event Action<AlertHit>? Matched;

    public void Configure(MatcherOptions options)
    {
        _callsignRules = options.Callsigns.ToArray();
        _defaultSoundFile = options.DefaultSoundFile;
        _wholeWord = options.WholeWord;
        _regexMode = options.RegexMode;
        _afkEnabled = options.AfkEnabled;
        _afkSoundFile = options.AfkSoundFile;

        if (_callsignRules.Length == 0 && !_afkEnabled)
            throw new ArgumentException("Nothing to listen for.", nameof(options));

        CompilePatterns();

        _recentlyFired.Clear();
        _lastFiredByKind.Clear();
    }

    private void CompilePatterns()
    {
        _compiledPatterns.Clear();
        if (!_regexMode) return;

        foreach (CallsignRule rule in _callsignRules)
        {
            if (_compiledPatterns.ContainsKey(rule.Pattern)) continue;

            try
            {
                _compiledPatterns[rule.Pattern] = new Regex(
                    rule.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                // A bad pattern is skipped and reported, not thrown: one broken entry in a list
                // of six shouldn't stop the other five from working.
                _compiledPatterns[rule.Pattern] = null;
                PatternWarning?.Invoke($"Invalid regex, skipped: {rule.Pattern}");
            }
        }
    }

    /// <summary>One line from the shared feed. Called on the UI thread.</summary>
    public void Evaluate(string line)
    {
        DateTime now = DateTime.UtcNow;
        PruneRepeatWindow(now);

        if (_afkEnabled && line.Contains(AfkKeyword, StringComparison.OrdinalIgnoreCase))
            TryFire(HitKind.Afk, line, "AFK", _afkSoundFile, now);

        foreach (CallsignRule rule in _callsignRules)
        {
            if (!Matches(line, rule.Pattern)) continue;

            string sound = string.IsNullOrWhiteSpace(rule.SoundFile) ? _defaultSoundFile : rule.SoundFile;
            TryFire(HitKind.Callsign, line, rule.Pattern, sound, now);

            // First matching callsign wins; a line shouldn't double-fire two of your own callsigns.
            break;
        }
    }

    private bool Matches(string line, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        if (_regexMode)
        {
            return _compiledPatterns.TryGetValue(pattern, out Regex? regex) &&
                   regex is not null &&
                   SafeIsMatch(regex, line);
        }

        return _wholeWord
            ? ContainsWholeWord(line, pattern)
            : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeIsMatch(Regex regex, string line)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Plain substring match, but the character immediately before and after the hit (if any)
    /// must not be a letter, digit or underscore. Stops a pattern like "FD-1" from matching
    /// inside "FD-12".
    /// </summary>
    private static bool ContainsWholeWord(string line, string pattern)
    {
        int start = 0;
        while (true)
        {
            int index = line.IndexOf(pattern, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;

            bool leftBoundary = index == 0 || !IsWordChar(line[index - 1]);
            int endIndex = index + pattern.Length;
            bool rightBoundary = endIndex >= line.Length || !IsWordChar(line[endIndex]);

            if (leftBoundary && rightBoundary) return true;
            start = index + 1;
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void TryFire(HitKind kind, string line, string matchedText, string soundFile, DateTime now)
    {
        if (_lastFiredByKind.TryGetValue(kind, out DateTime lastForKind) && now - lastForKind < Cooldown)
            return;

        // Keyed by kind so an AFK ping and a callsign hail on the same line don't suppress each other.
        string repeatKey = kind + "|" + line;
        if (_recentlyFired.TryGetValue(repeatKey, out DateTime firedAt) && now - firedAt < RepeatWindow)
            return;

        _recentlyFired[repeatKey] = now;
        _lastFiredByKind[kind] = now;
        Matched?.Invoke(new AlertHit(now, kind, matchedText, line, soundFile));
    }

    private void PruneRepeatWindow(DateTime now)
    {
        if (_recentlyFired.Count < 200) return;

        foreach (string key in _recentlyFired
                     .Where(pair => now - pair.Value >= RepeatWindow)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _recentlyFired.Remove(key);
        }
    }
}
