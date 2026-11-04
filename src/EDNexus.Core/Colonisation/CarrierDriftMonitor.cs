namespace EDNexus.Core.Colonisation;

/// <summary>What a <see cref="CarrierDriftMonitor"/> made of one reply from the tracker.</summary>
/// <param name="IsDrift">True when the carrier moved in a way EDNexus alone cannot explain.</param>
/// <param name="Detail">Which commodities, for the log; null when there is no drift.</param>
internal readonly record struct DriftVerdict(bool IsDrift, string? Detail = null);

/// <summary>
/// Notices a second tool reporting the same carrier cargo to the shared tracker.
/// </summary>
/// <remarks>
/// <para>
/// The tracker adds up whatever it is told, and the journal tells every tool the same thing, so two
/// reporters double every movement. The tracker's reply to a cargo change is what it now holds; this
/// keeps the last totals it saw and, for each new reply, checks <c>last + what we sent == what it holds</c>.
/// </para>
/// <para>
/// A duplicate has a recognisable shape: the unexplained difference equals a change we sent, either in
/// this very send (the other tool got in first) or in an earlier one (it got in afterwards, and shows up
/// on whatever we next send). That is flagged at once. Anything else unexplained — a squadmate editing
/// the manifest on the website, say — is tolerated once and only counts as drift when it happens on two
/// sends in a row.
/// </para>
/// <para>
/// A reply that says nothing (an empty body) proves nothing; a removal bigger than the stock is not
/// judged, since the tracker may clamp it; and a commodity that reaches zero may simply be missing from
/// the reply. Single-threaded: used only by the sync's worker.
/// </para>
/// </remarks>
internal sealed class CarrierDriftMonitor
{
    private Dictionary<string, int>? _last;
    private Dictionary<string, int> _lastSent = new(StringComparer.Ordinal);
    private int _unexplainedInARow;

    /// <summary>True once the monitor has totals to compare the next reply against.</summary>
    public bool HasBaseline => _last is not null;

    /// <summary>Adopts the tracker's own figures as the starting point. Ignored once there is one: a later lookup must not mask drift.</summary>
    public void SetBaseline(IReadOnlyDictionary<string, int> totals)
        => _last ??= new Dictionary<string, int>(totals, StringComparer.Ordinal);

    public void Reset()
    {
        _last = null;
        _lastSent = new Dictionary<string, int>(StringComparer.Ordinal);
        _unexplainedInARow = 0;
    }

    /// <summary>Judges a reply. <paramref name="sent"/> is what was just sent; <paramref name="totals"/> is what the tracker says it holds now.</summary>
    public DriftVerdict Observe(IReadOnlyDictionary<string, int> sent, IReadOnlyDictionary<string, int> totals)
    {
        var previouslySent = _lastSent;
        _lastSent = Merge(previouslySent, sent);

        // No baseline yet, or a reply that said nothing: all that can be done is keep the books.
        if (_last is null)
        {
            _last = new Dictionary<string, int>(totals, StringComparer.Ordinal);
            return default;
        }
        if (totals.Count == 0)
        {
            foreach (var (name, units) in sent)
                _last[name] = Math.Max(0, _last.GetValueOrDefault(name) + units);
            return default;
        }

        var unexplained = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in totals.Keys.Concat(sent.Keys).Distinct(StringComparer.Ordinal))
        {
            var expected = _last.GetValueOrDefault(name) + sent.GetValueOrDefault(name);
            if (expected < 0) continue;                       // the tracker may clamp at zero; cannot judge
            var difference = totals.GetValueOrDefault(name) - expected;
            if (difference != 0) unexplained[name] = difference;
        }

        foreach (var (name, units) in totals) _last[name] = units;
        foreach (var name in sent.Keys.Where(n => !totals.ContainsKey(n))) _last[name] = 0;

        if (unexplained.Count == 0)
        {
            _unexplainedInARow = 0;
            return default;
        }

        _unexplainedInARow++;
        var duplicate = unexplained.Any(u =>
            (sent.TryGetValue(u.Key, out var now) && u.Value == now)
            || (previouslySent.TryGetValue(u.Key, out var before) && u.Value == before));
        if (!duplicate && _unexplainedInARow < 2) return default;

        return new DriftVerdict(true, string.Join(", ", unexplained.Select(u => $"{u.Key} {u.Value:+#;-#}")));
    }

    private static Dictionary<string, int> Merge(Dictionary<string, int> older, IReadOnlyDictionary<string, int> newer)
    {
        var merged = new Dictionary<string, int>(older, StringComparer.Ordinal);
        foreach (var (name, units) in newer) merged[name] = units;
        return merged;
    }
}
