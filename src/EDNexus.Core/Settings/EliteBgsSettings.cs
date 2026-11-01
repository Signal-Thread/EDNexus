namespace EDNexus.Core.Settings;

/// <summary>Options for the EliteBGS faction-influence data source.</summary>
public sealed class EliteBgsSettings
{
    /// <summary>
    /// Opt-in, default off. When true, EDNexus may look up a faction or system by name on elitebgs.app
    /// (a public read-only API) to show influence and background-simulation states. Only the faction or
    /// system name being looked up is sent. Nothing is requested while this is off.
    /// </summary>
    public bool Enabled { get; set; }
}
