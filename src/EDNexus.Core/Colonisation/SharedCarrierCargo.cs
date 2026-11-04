using EliteDangerous.RavenColonial;

namespace EDNexus.Core.Colonisation;

/// <summary>What the shared tracker said about a carrier.</summary>
/// <param name="Known">True when the tracker holds this carrier. False with a null <paramref name="Error"/> is a definite "not tracked here".</param>
/// <param name="Cargo">What the tracker holds for the carrier, in the tracker's own spelling, empty when not known.</param>
/// <param name="Error">Set when the question could not be answered and may be asked again.</param>
public sealed record SharedCarrierLookup(bool Known, IReadOnlyDictionary<string, int> Cargo, string? Error = null);

/// <summary>The outcome of reporting a cargo change.</summary>
/// <param name="Totals">What the tracker says the carrier now holds; empty when it said nothing.</param>
/// <param name="Error">Why the change was not accepted; null when it was.</param>
/// <param name="KeyRejected">True when the tracker refused the commander's API key (401/403) as opposed to failing.</param>
public sealed record SharedCarrierWrite(IReadOnlyDictionary<string, int> Totals, string? Error = null, bool KeyRejected = false);

/// <summary>
/// Reports changes to the commander's fleet carrier cargo to the shared tracker. Backed by Raven
/// Colonial in the app; an interface so the sync service can be exercised without the network.
/// </summary>
public interface ISharedCarrierCargo
{
    /// <summary>Asks whether the tracker holds this carrier, and what it holds for it.</summary>
    Task<SharedCarrierLookup> FindAsync(long marketId, CancellationToken ct = default);

    /// <summary>Reports a signed change to the carrier's cargo (commodity, in the tracker's spelling, to tons).</summary>
    Task<SharedCarrierWrite> PatchAsync(long marketId, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default);
}

/// <summary>
/// The engine-side <see cref="ISharedCarrierCargo"/> adapter over the reusable <see cref="RavenColonialClient"/>.
/// </summary>
/// <remarks>
/// The commander's API key is read through <c>apiKey</c> on every call, so changing it in Settings takes
/// effect on the next movement, and it is never kept here. This adapter never decides <em>whether</em> to
/// send — that is the caller's opt-in — and only maps names and outcomes.
/// </remarks>
public sealed class RavenColonialCarrierCargo : ISharedCarrierCargo
{
    private static readonly IReadOnlyDictionary<string, int> NoCargo = new Dictionary<string, int>();

    private readonly RavenColonialClient _client;
    private readonly Func<string> _apiKey;

    public RavenColonialCarrierCargo(RavenColonialClient client, Func<string> apiKey)
    {
        _client = client;
        _apiKey = apiKey;
    }

    public async Task<SharedCarrierLookup> FindAsync(long marketId, CancellationToken ct = default)
    {
        var found = await _client.GetCarrierAsync(marketId, ct).ConfigureAwait(false);
        if (!found.IsOk) return new SharedCarrierLookup(false, NoCargo, found.Error ?? "lookup failed");
        return found.Value is { } carrier
            ? new SharedCarrierLookup(true, carrier.Cargo)
            : new SharedCarrierLookup(false, NoCargo);
    }

    public async Task<SharedCarrierWrite> PatchAsync(
        long marketId, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default)
    {
        var key = _apiKey()?.Trim() ?? "";
        if (key.Length == 0) return new SharedCarrierWrite(NoCargo, "no Raven Colonial API key set");

        var sent = await _client.PatchCarrierCargoAsync(marketId, key, deltas, ct).ConfigureAwait(false);
        if (sent.IsOk) return new SharedCarrierWrite(sent.Value?.Totals ?? NoCargo);

        return new SharedCarrierWrite(NoCargo, sent.Error ?? "write failed", sent.StatusCode is 401 or 403);
    }
}
