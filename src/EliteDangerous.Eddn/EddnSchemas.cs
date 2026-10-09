namespace EliteDangerous.Eddn;

/// <summary>
/// EDDN schema references and the set of journal events the network accepts on the journal schema.
/// Sending an event outside <see cref="JournalWhitelist"/> to it gets the whole message rejected, so the
/// transformer routes those to their own schema (or drops them) before upload.
/// </summary>
public static class EddnSchemas
{
    private const string Base = "https://eddn.edcd.io/schemas";

    public const string Journal = Base + "/journal/1";
    public const string Commodity = Base + "/commodity/3";
    public const string Outfitting = Base + "/outfitting/2";
    public const string Shipyard = Base + "/shipyard/2";
    public const string FcMaterialsJournal = Base + "/fcmaterials_journal/1";
    public const string NavRoute = Base + "/navroute/1";
    public const string CodexEntry = Base + "/codexentry/1";
    public const string ApproachSettlement = Base + "/approachsettlement/1";
    public const string FssDiscoveryScan = Base + "/fssdiscoveryscan/1";
    public const string FssAllBodiesFound = Base + "/fssallbodiesfound/1";
    public const string FssBodySignals = Base + "/fssbodysignals/1";
    public const string FssSignalDiscovered = Base + "/fsssignaldiscovered/1";
    public const string NavBeaconScan = Base + "/navbeaconscan/1";
    public const string ScanBaryCentre = Base + "/scanbarycentre/1";

    /// <summary>
    /// Journal events permitted on the <see cref="Journal"/> schema (its <c>event</c> enum). Every other
    /// uploadable event has its own schema — sending it here gets the message rejected with a 400.
    /// </summary>
    public static readonly IReadOnlySet<string> JournalWhitelist = new HashSet<string>(StringComparer.Ordinal)
    {
        "Docked", "FSDJump", "CarrierJump", "Location", "Scan", "SAASignalsFound",
    };
}
