using System.Text.RegularExpressions;

namespace EDNexus.Core.Telemetry;

/// <summary>
/// Redacts personally-identifying strings before anything leaves the machine. No I/O of its own and
/// deterministic for a given set of sensitive values, so it can be unit-tested in isolation. Used by
/// the crash reporter's Sentry hooks to scrub messages, exception text, file paths, tags, extras and
/// breadcrumbs.
/// </summary>
public sealed partial class PiiScrubber
{
    public const string RedactedToken = "[redacted]";
    public const string UserToken = "[user]";

    /// <summary>
    /// Sensitive values shorter than this many characters (after trimming) are ignored: redacting a
    /// one- or two-letter value would mangle unrelated words, stack traces and file paths wherever
    /// those letters happen to appear.
    /// </summary>
    public const int MinLiteralLength = 3;

    private readonly string[] _literals;
    private readonly Func<IEnumerable<string?>>? _currentSensitive;

    /// <param name="sensitive">
    /// Fixed sensitive literals to redact wholesale — e.g. the OS user name and the journal directory
    /// path. Blank entries and ones shorter than <see cref="MinLiteralLength"/> are ignored to avoid
    /// over-redacting.
    /// </param>
    /// <param name="currentSensitive">
    /// Sensitive values that can change or only become known later — e.g. the Inara API key and the
    /// CMDR name. Read afresh on every <see cref="Scrub"/> call, on whichever thread is scrubbing, so it
    /// must be cheap and thread-safe. Filtered the same way as <paramref name="sensitive"/>.
    /// </param>
    public PiiScrubber(IEnumerable<string>? sensitive = null, Func<IEnumerable<string?>>? currentSensitive = null)
    {
        _literals = Normalize(sensitive ?? Enumerable.Empty<string>());
        _currentSensitive = currentSensitive;
    }

    /// <summary>Matches a home-directory path and captures the prefix so the user segment can be
    /// replaced: <c>C:\Users\name</c>, <c>/home/name</c>, <c>/Users/name</c>.</summary>
    [GeneratedRegex(@"([A-Za-z]:\\Users\\|/home/|/Users/)[^\\/\r\n""]+", RegexOptions.IgnoreCase)]
    private static partial Regex HomePathRegex();

    public string? Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        string[] literals;
        try { literals = CurrentLiterals(); }
        // Fail closed: Sentry still sends an event whose BeforeSend hook throws, so if the current
        // values can't be read, withhold this whole string rather than risk leaking one of them.
        catch { return RedactedToken; }

        var result = input;
        foreach (var literal in literals)
            result = result.Replace(literal, RedactedToken, StringComparison.OrdinalIgnoreCase);

        result = HomePathRegex().Replace(result, m => m.Groups[1].Value + UserToken);
        return result;
    }

    /// <summary>The fixed literals plus the current values, sorted together so longest-first holds across both.</summary>
    private string[] CurrentLiterals()
        => _currentSensitive is null ? _literals : Normalize(_literals.Concat(_currentSensitive()));

    private static string[] Normalize(IEnumerable<string?> values) => values
        .Where(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= MinLiteralLength)
        .Select(s => s!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        // Longest first so "Ada Lovelace" is redacted before a stray "Ada".
        .OrderByDescending(s => s.Length)
        .ToArray();
}
