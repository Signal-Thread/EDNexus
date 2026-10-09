using System.Globalization;

namespace EDNexus.Core.Updates;

/// <summary>
/// Minimal semantic version: <c>[v]MAJOR[.MINOR[.PATCH[.REVISION]]][-prerelease][+build]</c>, ordered per
/// SemVer 2.0 (a prerelease sorts below its release; prerelease identifiers compare numerically when both
/// are numeric, otherwise ordinally, with numeric identifiers below alphanumeric ones). Build metadata is ignored.
/// </summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private readonly int[] _core;          // always 4 parts: major, minor, patch, revision
    private readonly string[] _pre;        // empty for a normal release

    private SemVer(int[] core, string[] pre) { _core = core; _pre = pre; }

    /// <summary>True when this is a prerelease such as <c>0.1.0-beta.2</c>.</summary>
    public bool IsPrerelease => _pre.Length > 0;

    /// <summary>Parses <paramref name="raw"/>; returns false for anything that is not a recognisable version.</summary>
    public static bool TryParse(string? raw, out SemVer version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        string[] pre = Array.Empty<string>();
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var preText = s[(dash + 1)..];
            s = s[..dash];
            if (preText.Length == 0) return false;
            pre = preText.Split('.');
            foreach (var id in pre)
            {
                if (id.Length == 0) return false;
                foreach (var c in id)
                    if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            }
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var core = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out core[i])) return false;
        }

        version = new SemVer(core, pre);
        return true;
    }

    /// <summary>Parses <paramref name="raw"/> or returns 0.0.0 when it cannot be parsed.</summary>
    public static SemVer ParseOrZero(string? raw)
        => TryParse(raw, out var v) ? v : new SemVer(new int[4], Array.Empty<string>());

    /// <inheritdoc />
    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 4; i++)
        {
            var c = _core[i].CompareTo(other._core[i]);
            if (c != 0) return c;
        }

        // A release outranks any prerelease of the same core version.
        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;
        if (other._pre.Length == 0) return -1;

        var n = Math.Min(_pre.Length, other._pre.Length);
        for (var i = 0; i < n; i++)
        {
            var a = _pre[i];
            var b = other._pre[i];
            var aNum = a.All(char.IsAsciiDigit);
            var bNum = b.All(char.IsAsciiDigit);
            int c;
            if (aNum && bNum)
            {
                // Compare by length first so very long numeric identifiers cannot overflow.
                var at = a.TrimStart('0');
                var bt = b.TrimStart('0');
                c = at.Length != bt.Length ? at.Length.CompareTo(bt.Length) : string.CompareOrdinal(at, bt);
            }
            else if (aNum) c = -1;
            else if (bNum) c = 1;
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return c < 0 ? -1 : 1;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    /// <inheritdoc />
    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemVer o && Equals(o);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var c in _core) h.Add(c);
        foreach (var p in _pre) h.Add(p);
        return h.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var last = 3;
        while (last > 2 && _core[last] == 0) last--;
        var core = string.Join('.', _core.Take(last + 1));
        return _pre.Length == 0 ? core : core + "-" + string.Join('.', _pre);
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
}
