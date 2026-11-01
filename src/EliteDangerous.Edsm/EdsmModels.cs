namespace EliteDangerous.Edsm;

/// <summary>Galactic coordinates of a system, in light years from Sol.</summary>
public readonly record struct EdsmCoords(double X, double Y, double Z)
{
    /// <summary>Straight-line distance to another system, in light years.</summary>
    public double DistanceTo(EdsmCoords other)
    {
        double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// A system as EDSM reports it. <see cref="Coords"/> is null when EDSM has no confirmed position
/// (unvisited systems), and <see cref="DistanceLy"/> is only populated by nearby/sphere queries.
/// </summary>
public sealed record EdsmSystem(
    string Name,
    EdsmCoords? Coords,
    double? DistanceLy = null);

/// <summary>One body (star, planet, moon, belt) in a system, as EDSM reports it.</summary>
public sealed record EdsmBody(
    string Name,
    string Type,
    string? SubType,
    bool IsLandable,
    double? DistanceToArrivalLs);

/// <summary>The bodies EDSM knows for a system, keyed by the system name it was asked about.</summary>
public sealed record EdsmSystemBodies(
    string SystemName,
    IReadOnlyList<EdsmBody> Bodies);

/// <summary>
/// The result of an EDSM lookup. Mirrors the Spansh client's convention: transport, HTTP and parse
/// failures never throw — they surface as <see cref="IsOk"/> false with an <see cref="Error"/>, and a
/// successful-but-unknown system comes back as <see cref="IsOk"/> true with a null <see cref="Value"/>.
/// </summary>
public sealed class EdsmResult<T> where T : class
{
    /// <summary>True when the request reached EDSM and parsed cleanly (even if nothing matched).</summary>
    public bool IsOk { get; init; }

    /// <summary>The parsed payload, or null when EDSM had no data for the query.</summary>
    public T? Value { get; init; }

    /// <summary>Failure detail when <see cref="IsOk"/> is false; null on success.</summary>
    public string? Error { get; init; }

    public static EdsmResult<T> Ok(T? value) => new() { IsOk = true, Value = value };

    public static EdsmResult<T> Failure(string message) => new() { IsOk = false, Error = message };
}
