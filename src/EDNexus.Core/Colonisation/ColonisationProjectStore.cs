using System.Text;
using System.Text.Json;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// The on-disk form of one construction project. Deliberately tolerant: every field may be missing
/// or hand-edited, and <see cref="ToSite"/> repairs what it can instead of throwing.
/// </summary>
public sealed class ColonisationProjectRecord
{
    public long MarketId { get; set; }
    public string? StationName { get; set; }
    public string? StarSystem { get; set; }
    public double Progress { get; set; }
    public bool Complete { get; set; }
    public bool Failed { get; set; }
    public DateTimeOffset Updated { get; set; }
    public List<ColonisationResource?>? Resources { get; set; }

    public static ColonisationProjectRecord From(ColonisationSite site) => new()
    {
        MarketId = site.MarketId,
        StationName = site.StationName,
        StarSystem = site.StarSystem,
        Progress = site.Progress,
        Complete = site.Complete,
        Failed = site.Failed,
        Updated = site.Updated,
        Resources = site.Resources.Cast<ColonisationResource?>().ToList(),
    };

    /// <summary>The live site for this record, or null when it has no usable market id.</summary>
    public ColonisationSite? ToSite()
    {
        if (MarketId <= 0) return null;

        var resources = new List<ColonisationResource>();
        foreach (var r in Resources ?? [])
        {
            if (r is null) continue;
            var symbol = CommodityName.Canonicalize(string.IsNullOrWhiteSpace(r.Symbol) ? r.Name : r.Symbol);
            if (symbol.Length == 0) continue;
            resources.Add(new ColonisationResource(
                string.IsNullOrWhiteSpace(r.Name) ? symbol : r.Name,
                symbol,
                Math.Max(0, r.Required),
                Math.Max(0, r.Provided),
                Math.Max(0, r.Payment)));
        }

        return new ColonisationSite
        {
            MarketId = MarketId,
            StationName = StationName,
            StarSystem = StarSystem,
            Progress = double.IsFinite(Progress) ? Math.Clamp(Progress, 0, 1) : 0,
            Complete = Complete,
            Failed = Failed,
            Updated = Updated,
            Resources = resources,
        };
    }
}

/// <summary>Everything remembered about one commander's colonisation projects.</summary>
public sealed class ColonisationProjectData
{
    public int Version { get; set; } = 1;

    /// <summary>The project the card was last showing on its own, if any.</summary>
    public long? SelectedMarketId { get; set; }

    /// <summary>True when the card was last showing the combined all-projects list.</summary>
    public bool ShowAll { get; set; }

    public List<ColonisationProjectRecord?>? Projects { get; set; }

    /// <summary>The usable projects; later duplicates of a market id win, bad records are dropped.</summary>
    public IReadOnlyList<ColonisationSite> ToSites()
    {
        var byId = new Dictionary<long, ColonisationSite>();
        foreach (var p in Projects ?? [])
            if (p?.ToSite() is { } site) byId[site.MarketId] = site;
        return byId.Values.ToList();
    }
}

/// <summary>
/// Loads and saves <see cref="ColonisationProjectData"/> as one small JSON file per commander in a
/// folder (the app uses the same one as <c>settings.json</c>). Never throws: a missing or unreadable
/// file reads as empty, and a file that cannot be parsed is moved aside rather than overwritten.
/// </summary>
public sealed class ColonisationProjectStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly object _gate = new();

    public ColonisationProjectStore(string directory) => _directory = directory;

    /// <summary>Where <paramref name="commander"/>'s projects live (the file may not exist yet).</summary>
    public string PathFor(string commander)
    {
        var sb = new StringBuilder();
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        foreach (var ch in commander.Trim().ToLowerInvariant())
            sb.Append(Array.IndexOf(invalid, ch) >= 0 || char.IsWhiteSpace(ch) ? '_' : ch);
        return System.IO.Path.Combine(_directory, $"colonisation.{sb}.json");
    }

    /// <summary>The saved projects for <paramref name="commander"/>; empty when none or unreadable.</summary>
    public ColonisationProjectData Load(string commander)
    {
        if (string.IsNullOrWhiteSpace(commander)) return new();
        var path = PathFor(commander);
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return new();
                var parsed = JsonSerializer.Deserialize<ColonisationProjectData>(File.ReadAllText(path));
                if (parsed is not null) return parsed;
                Quarantine(path);
            }
            catch (JsonException)
            {
                Quarantine(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked right now; leave it alone and start empty for this session.
            }
            return new();
        }
    }

    /// <summary>Writes the data atomically (temp file, then swap). Returns false instead of throwing.</summary>
    public bool TrySave(string commander, ColonisationProjectData data)
    {
        if (string.IsNullOrWhiteSpace(commander)) return false;
        var path = PathFor(commander);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void Quarantine(string path)
    {
        try { File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfff}"); }
        catch { }
    }
}
