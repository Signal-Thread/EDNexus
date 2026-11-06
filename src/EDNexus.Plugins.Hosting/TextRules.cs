using System.Globalization;

namespace EDNexus.Plugins.Hosting;

/// <summary>Shared checks for untrusted text (manifest display fields and package entry names).</summary>
internal static class TextRules
{
    /// <summary>
    /// Returns a reason (phrased to follow the field name, e.g. "contains control characters") if
    /// <paramref name="text"/> contains control characters, invisible <see cref="UnicodeCategory.Format"/>
    /// characters (bidi overrides, zero-width spaces/joiners, BOM), or unpaired surrogates;
    /// otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="text">The text to check.</param>
    /// <param name="allowLineBreaks">Whether <c>\n</c>, <c>\r</c> and <c>\t</c> are permitted.</param>
    public static string? FindInvisibleOrInvalid(string text, bool allowLineBreaks)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(text, i) == UnicodeCategory.Format)
                    return $"contains an invisible formatting character (U+{char.ConvertToUtf32(ch, text[i + 1]):X4})";
                i++;
                continue;
            }
            if (char.IsSurrogate(ch))
                return "contains invalid Unicode (an unpaired surrogate)";
            if (char.IsControl(ch))
            {
                if (allowLineBreaks && ch is '\n' or '\r' or '\t')
                    continue;
                return "contains control characters";
            }
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                return $"contains an invisible formatting character (U+{(int)ch:X4})";
        }
        return null;
    }

    /// <summary>
    /// Makes untrusted text (a folder name, or an exception message raised by plugin code) safe to
    /// echo in a log line or the UI: control characters (which include <c>\n</c>, <c>\r</c> and the
    /// C1 next-line U+0085), invisible formatting characters, the Unicode line and paragraph
    /// separators (U+2028, U+2029, which are neither control nor format characters but still break a
    /// line in many viewers) and unpaired surrogates become <c>?</c>, and the result is cut to
    /// <paramref name="maxLength"/>.
    /// </summary>
    public static string ForDisplay(string? text, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maxLength + 1));
        var i = 0;
        for (; i < text.Length && builder.Length < maxLength; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(text, i) == UnicodeCategory.Format)
                    builder.Append('?');
                else
                    builder.Append(text, i, 2);
                i++;
                continue;
            }
            builder.Append(char.IsSurrogate(ch) || char.IsControl(ch) || IsInvisibleOrLineBreaking(CharUnicodeInfo.GetUnicodeCategory(ch))
                ? '?'
                : ch);
        }
        if (i < text.Length)
            builder.Append('…');
        return builder.ToString();
    }

    private static bool IsInvisibleOrLineBreaking(UnicodeCategory category)
        => category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}

/// <summary>
/// Makes untrusted text safe to write to a one-line-per-entry log. For callers outside this
/// assembly (the app's plugin log lines); see <see cref="TextRules.ForDisplay"/> for what is replaced.
/// </summary>
public static class PluginLogText
{
    /// <summary>
    /// <paramref name="text"/> with control, invisible-format, line- and paragraph-separator
    /// characters and unpaired surrogates replaced by <c>?</c>, cut to <paramref name="maxLength"/>
    /// characters. <see langword="null"/> becomes the empty string. Never throws.
    /// </summary>
    public static string Sanitise(string? text, int maxLength = 500) => TextRules.ForDisplay(text, maxLength);
}
