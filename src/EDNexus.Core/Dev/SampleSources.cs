using System.Text.Json.Nodes;

namespace EDNexus.Core.Dev;

/// <summary>Shared, realistic name pools so the samplers emit plausible Elite Dangerous data.</summary>
internal static class SamplePools
{
    public static readonly string[] Systems =
    {
        "Sol", "Shinrarta Dezhra", "Deciat", "Colonia", "Maia", "Diaguandri", "Ratraii",
        "HIP 22460", "Synuefe XR-H d11-102", "Sagittarius A*", "Beagle Point", "Jackson's Lighthouse",
    };

    public static readonly string[] BodySuffixes = { " A 1", " A 2 a", " B 3", " 5 c", " AB 1 a", " 2", " A" };

    public static readonly string[] Stations =
    {
        "Jameson Memorial", "Ray Gateway", "Dubois Orbital", "Ehrenfried Gateway", "Garay Terminal",
        "Q3Z-BQL", "Robigo Mines", "Farseer Inc", "Ackerman Market",
    };

    // (journal symbol, localised) — the game stores the symbol in Ship and the label in Ship_Localised.
    public static readonly (string Sym, string Loc)[] Ships =
    {
        ("anaconda", "Anaconda"), ("python", "Python"), ("federation_corvette", "Federal Corvette"),
        ("cutter", "Imperial Cutter"), ("krait_mkii", "Krait Mk II"), ("type9", "Type-9 Heavy"),
        ("asp", "Asp Explorer"), ("cobramkiii", "Cobra Mk III"), ("ferdelance", "Fer-de-Lance"),
    };

    public static readonly string[] ShipNames =
    {
        "Massive Bone Yard", "Stellar Nomad", "Void Runner", "Iron Duke", "Nightingale",
        "Sual's Fortune", "Deep Six", "Halcyon", "Wandering Star", "Last Light",
    };

    public static readonly string[] Commanders =
    {
        "Demortes", "Jameson", "Aisling", "Salome", "Zorgon", "Brace", "Nova", "Ryder", "Kaz", "Halsey",
    };

    public static readonly string[] RawMaterials =
    {
        "iron", "nickel", "carbon", "sulphur", "phosphorus", "manganese",
        "zinc", "chromium", "vanadium", "tin", "arsenic", "cadmium",
    };

    public static readonly string[] ManufacturedMaterials =
    {
        "mechanicalcomponents", "heatdispersionplate", "gridresistors", "conductivecomponents",
        "shieldemitters", "chemicalprocessors", "focuscrystals", "compoundshielding", "militarygradealloys",
    };

    public static readonly string[] EncodedMaterials =
    {
        "shielddensityreports", "scrambledemissiondata", "bulkscandata", "disruptedwakeechoes",
        "emissiondata", "wakesolutions", "legacyfirmware", "hyperspacetrajectories",
    };

    // (journal symbol, localised) for ordinary trade commodities.
    public static readonly (string Sym, string Loc)[] Commodities =
    {
        ("gold", "Gold"), ("silver", "Silver"), ("palladium", "Palladium"), ("tritium", "Tritium"),
        ("painite", "Painite"), ("lowtemperaturediamond", "Low Temperature Diamonds"),
        ("bertrandite", "Bertrandite"), ("beryllium", "Beryllium"), ("water", "Water"),
        ("agriculturalmedicines", "Agricultural Medicines"),
    };

    // (journal symbol, localised, category) for a station commodity market board — a broad spread
    // across the game's real market categories so the card and hold valuation have plenty of variety.
    public static readonly (string Sym, string Loc, string Cat)[] MarketGoods =
    {
        // Metals
        ("aluminium", "Aluminium", "Metals"), ("beryllium", "Beryllium", "Metals"),
        ("cobalt", "Cobalt", "Metals"), ("copper", "Copper", "Metals"),
        ("gallium", "Gallium", "Metals"), ("gold", "Gold", "Metals"),
        ("indium", "Indium", "Metals"), ("lithium", "Lithium", "Metals"),
        ("palladium", "Palladium", "Metals"), ("platinum", "Platinum", "Metals"),
        ("silver", "Silver", "Metals"), ("tantalum", "Tantalum", "Metals"),
        ("titanium", "Titanium", "Metals"), ("uranium", "Uranium", "Metals"),
        // Minerals
        ("bauxite", "Bauxite", "Minerals"), ("bertrandite", "Bertrandite", "Minerals"),
        ("bromellite", "Bromellite", "Minerals"), ("coltan", "Coltan", "Minerals"),
        ("gallite", "Gallite", "Minerals"), ("indite", "Indite", "Minerals"),
        ("lepidolite", "Lepidolite", "Minerals"),
        ("lowtemperaturediamond", "Low Temperature Diamonds", "Minerals"),
        ("painite", "Painite", "Minerals"), ("rutile", "Rutile", "Minerals"),
        ("uraninite", "Uraninite", "Minerals"), ("opal", "Void Opals", "Minerals"),
        // Chemicals
        ("explosives", "Explosives", "Chemicals"), ("hydrogenfuel", "Hydrogen Fuel", "Chemicals"),
        ("hydrogenperoxide", "Hydrogen Peroxide", "Chemicals"), ("liquidoxygen", "Liquid Oxygen", "Chemicals"),
        ("mineraloil", "Mineral Oil", "Chemicals"), ("tritium", "Tritium", "Chemicals"),
        ("water", "Water", "Chemicals"),
        // Foods
        ("algae", "Algae", "Foods"), ("animalmeat", "Animal Meat", "Foods"),
        ("coffee", "Coffee", "Foods"), ("fish", "Fish", "Foods"),
        ("foodcartridges", "Food Cartridges", "Foods"), ("fruitandvegetables", "Fruit and Vegetables", "Foods"),
        ("grain", "Grain", "Foods"), ("tea", "Tea", "Foods"),
        // Consumer items
        ("clothing", "Clothing", "Consumer Items"), ("consumertechnology", "Consumer Technology", "Consumer Items"),
        ("domesticappliances", "Domestic Appliances", "Consumer Items"),
        // Industrial materials
        ("ceramiccomposites", "Ceramic Composites", "Industrial Materials"),
        ("polymers", "Polymers", "Industrial Materials"), ("semiconductors", "Semiconductors", "Industrial Materials"),
        ("superconductors", "Superconductors", "Industrial Materials"),
        // Medicines
        ("agriculturalmedicines", "Agricultural Medicines", "Medicines"),
        ("basicmedicines", "Basic Medicines", "Medicines"), ("progenitorcells", "Progenitor Cells", "Medicines"),
        // Machinery
        ("powergenerators", "Power Generators", "Machinery"), ("waterpurifiers", "Water Purifiers", "Machinery"),
        ("cropharvesters", "Crop Harvesters", "Machinery"),
        // Technology
        ("computercomponents", "Computer Components", "Technology"),
        ("medicaldiagnosticequipment", "Medical Diagnostic Equipment", "Technology"),
        ("robotics", "Robotics", "Technology"),
        // Weapons
        ("nonlethalweapons", "Non-Lethal Weapons", "Weapons"), ("reactivearmour", "Reactive Armour", "Weapons"),
    };

    // (journal symbol, localised, typical galactic-average price) for the mining card — a spread from
    // common laser fodder up through hotspot-only deep-core minerals, so both ends of "worth mining"
    // get exercised.
    public static readonly (string Sym, string Loc, int MeanPrice)[] MiningMaterials =
    {
        ("painite", "Painite", 44000), ("platinum", "Platinum", 21000),
        ("osmium", "Osmium", 8600), ("palladium", "Palladium", 14200),
        ("opal", "Void Opals", 220000), ("alexandrite", "Alexandrite", 400000),
        ("bromellite", "Bromellite", 8500), ("lowtemperaturediamond", "Low Temperature Diamonds", 80000),
        ("bauxite", "Bauxite", 850), ("cobalt", "Cobalt", 1300),
        ("rutile", "Rutile", 2100), ("uraninite", "Uraninite", 2200),
    };

    // (journal symbol, localised) for the commodities colonisation depots ask for.
    public static readonly (string Sym, string Loc)[] Construction =
    {
        ("aluminium", "Aluminium"), ("steel", "Steel"), ("titanium", "Titanium"),
        ("cmmcomposite", "CMM Composite"), ("ceramiccomposites", "Ceramic Composites"),
        ("computercomponents", "Computer Components"), ("copper", "Copper"),
        ("foodcartridges", "Food Cartridges"), ("fruitandvegetables", "Fruit and Vegetables"),
        ("insulatingmembrane", "Insulating Membrane"), ("liquidoxygen", "Liquid oxygen"),
        ("medicaldiagnosticequipment", "Medical Diagnostic Equipment"),
        ("nonlethalweapons", "Non-Lethal Weapons"), ("polymers", "Polymers"),
        ("powergenerators", "Power Generators"), ("semiconductors", "Semiconductors"),
        ("superconductors", "Superconductors"), ("water", "Water"),
        ("waterpurifiers", "Water Purifiers"), ("titanium", "Titanium"),
    };

    public static readonly string[] ConstructionSites =
    {
        "Born's Pride", "New Horizon", "Kepler's Rest", "Vanguard Foothold", "Aurora Landing",
        "Meridian Anchorage", "Halifax Reach",
    };

    /// <summary>A ship ident like "DE-19L".</summary>
    public static string Ident(Random rng)
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string digits = "0123456789";
        return $"{letters[rng.Next(26)]}{letters[rng.Next(26)]}-{digits[rng.Next(10)]}{letters[rng.Next(26)]}";
    }

    /// <summary>Pick a single random element from a list.</summary>
    public static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    /// <summary>Pick <paramref name="count"/> distinct entries from a list.</summary>
    public static List<T> PickDistinct<T>(Random rng, IReadOnlyList<T> items, int count)
    {
        var pool = items.ToList();
        var chosen = new List<T>();
        count = Math.Min(count, pool.Count);
        for (var i = 0; i < count; i++)
        {
            var idx = rng.Next(pool.Count);
            chosen.Add(pool[idx]);
            pool.RemoveAt(idx);
        }
        return chosen;
    }
}

/// <summary>Random star system, body, and docked/in-flight state for the Location card.</summary>
public sealed class LocationSampleSource : JournalSampleSource
{
    public override string CardKey => "location";
    public override string DisplayName => "Location";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var system = Pick(rng, SamplePools.Systems);
        if (rng.Next(2) == 0)
        {
            var station = Pick(rng, SamplePools.Stations);
            return new[]
            {
                Event("Location", o =>
                {
                    o["StarSystem"] = system;
                    o["Body"] = system + Pick(rng, SamplePools.BodySuffixes);
                    o["Docked"] = true;
                    o["StationName"] = station;
                }),
            };
        }

        return new[]
        {
            Event("FSDJump", o =>
            {
                o["StarSystem"] = system;
                o["Body"] = system + Pick(rng, SamplePools.BodySuffixes);
            }),
        };
    }
}

/// <summary>Random ship, name, ident, fuel and balance for the Ship card (and the header).</summary>
public sealed class ShipSampleSource : JournalSampleSource
{
    public override string CardKey => "ship";
    public override string DisplayName => "Ship";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var (sym, loc) = Pick(rng, SamplePools.Ships);
        var capacity = Pick(rng, new[] { 8.0, 16, 24, 32, 48, 64 });
        var main = Math.Round(rng.NextDouble() * capacity, 1);
        var name = Pick(rng, SamplePools.ShipNames);
        var ident = SamplePools.Ident(rng);
        var commander = Pick(rng, SamplePools.Commanders);
        var credits = rng.NextInt64(250_000, 5_000_000_000);

        return new[]
        {
            Event("LoadGame", o =>
            {
                o["Commander"] = commander;
                o["Ship"] = sym;
                o["Ship_Localised"] = loc;
                o["ShipName"] = name;
                o["ShipIdent"] = ident;
                o["Credits"] = credits;
            }),
            Event("Loadout", o =>
            {
                o["Ship"] = sym;
                o["Ship_Localised"] = loc;
                o["ShipName"] = name;
                o["ShipIdent"] = ident;
                o["FuelCapacity"] = new JsonObject { ["Main"] = capacity };
            }),
            Event("Status", o =>
            {
                o["Fuel"] = new JsonObject { ["FuelMain"] = main };
                o["Balance"] = credits;
            }),
        };
    }
}

/// <summary>Random engineering materials across all three categories for the Materials card.</summary>
public sealed class MaterialsSampleSource : JournalSampleSource
{
    public override string CardKey => "materials";
    public override string DisplayName => "Materials";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        return new[]
        {
            Event("Materials", o =>
            {
                o["Raw"] = Category(rng, "Raw");
                o["Manufactured"] = Category(rng, "Manufactured");
                o["Encoded"] = Category(rng, "Encoded");
            }),
        };
    }

    /// <summary>
    /// Draw from the real catalog and respect each material's own cap, so the inventory view's
    /// grades, fill bars and at-cap warnings are exercised rather than approximated. Roughly one
    /// material in six is filled right to its cap, which is what the trader hints key off.
    /// </summary>
    private static JsonArray Category(Random rng, string category)
    {
        var pool = Engineering.EngineeringCatalog.Default.Materials
            .Where(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var arr = new JsonArray();
        foreach (var material in SamplePools.PickDistinct(rng, pool, rng.Next(pool.Count / 2, pool.Count + 1)))
        {
            var count = rng.Next(6) == 0 ? material.Cap : rng.Next(1, material.Cap);
            arr.Add(new JsonObject { ["Name"] = material.Symbol, ["Count"] = count });
        }
        return arr;
    }
}

/// <summary>
/// A random body with biological signals, a part-finished sample run and a Vista Genomics sale,
/// so the Exobiology card can be driven without flying to a landable world.
/// </summary>
public sealed class ExobiologySampleSource : JournalSampleSource
{
    public override string CardKey => "exobio";
    public override string DisplayName => "Exobiology";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var catalog = Exobio.ExobiologyCatalog.Default;
        var system = Pick(rng, SamplePools.Systems);
        var systemAddress = (long)rng.Next(1_000_000, int.MaxValue) * 1000;
        var bodyId = rng.Next(1, 40);
        var bodyName = system + Pick(rng, SamplePools.BodySuffixes);

        // A landable world whose physics the prediction engine can work with, and genera the DSS
        // reveals drawn from what those physics can grow — so the predicted and mapped views agree.
        var world = Pick(rng, Worlds);
        var environment = new Exobio.BodyEnvironment(
            world.PlanetClass, world.Atmosphere, world.AtmosphereType,
            world.GravityMs2 / 9.80665, world.TemperatureK, Landable: true, WasDiscovered: true, world.Volcanism);
        var growable = catalog.Predict(environment).Select(p => p.Genus).DistinctBy(g => g.Symbol).ToList();
        var pool = growable.Count > 0 ? growable : catalog.Genera.ToList();
        var genera = SamplePools.PickDistinct(rng, pool, rng.Next(1, Math.Min(3, pool.Count) + 1));
        var species = Pick(rng, genera[0].Species);

        var lines = new List<string>
        {
            Event("Scan", o =>
            {
                o["ScanType"] = "Detailed";
                o["BodyName"] = bodyName;
                o["SystemAddress"] = systemAddress;
                o["BodyID"] = bodyId;
                o["PlanetClass"] = world.PlanetClass;
                o["Atmosphere"] = world.Atmosphere;
                o["AtmosphereType"] = world.AtmosphereType;
                o["Volcanism"] = world.Volcanism;
                o["SurfaceGravity"] = world.GravityMs2;
                o["SurfaceTemperature"] = world.TemperatureK;
                o["Landable"] = true;
                o["WasDiscovered"] = rng.Next(3) != 0;
            }),
            Event("SAASignalsFound", o =>
            {
                o["BodyName"] = bodyName;
                o["SystemAddress"] = systemAddress;
                o["BodyID"] = bodyId;
                o["Signals"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Type"] = "$SAA_SignalType_Biological;",
                        ["Type_Localised"] = "Biological",
                        ["Count"] = genera.Count,
                    },
                };
                var list = new JsonArray();
                foreach (var g in genera)
                    list.Add(new JsonObject { ["Genus"] = g.Symbol, ["Genus_Localised"] = g.Name });
                o["Genuses"] = list;
            }),
            Event("ApproachBody", o =>
            {
                o["StarSystem"] = system;
                o["SystemAddress"] = systemAddress;
                o["Body"] = bodyName;
                o["BodyID"] = bodyId;
            }),
        };

        // Walk a sample run part-way, or all the way, so both the in-progress and pending-sale
        // states show up across reshuffles.
        var stages = new[] { "Log", "Sample", "Analyse" };
        foreach (var stage in stages.Take(rng.Next(1, 4)))
            lines.Add(Event("ScanOrganic", o =>
            {
                o["ScanType"] = stage;
                o["Genus"] = species.GenusSymbol;
                o["Genus_Localised"] = species.Genus;
                o["Species"] = species.Symbol;
                o["Species_Localised"] = species.Name;
                o["SystemAddress"] = systemAddress;
                o["Body"] = bodyId;
            }));

        if (rng.Next(2) == 0)
        {
            var sold = Pick(rng, catalog.Species);
            var firstLogged = rng.Next(4) == 0;
            lines.Add(Event("SellOrganicData", o =>
            {
                o["MarketID"] = rng.Next(3_000_000, 3_900_000);
                o["BioData"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Genus"] = sold.GenusSymbol,
                        ["Species"] = sold.Symbol,
                        ["Species_Localised"] = sold.Name,
                        ["Value"] = sold.Value,
                        ["Bonus"] = firstLogged ? sold.Value * 4 : 0,
                    },
                };
            }));
        }

        return lines;
    }

    private sealed record World(string PlanetClass, string Atmosphere, string AtmosphereType,
        double GravityMs2, double TemperatureK, string Volcanism = "");

    /// <summary>Typical bio-bearing worlds, one per major atmosphere, so reshuffles exercise different rules.</summary>
    private static readonly World[] Worlds =
    {
        new("High metal content body", "thin carbon dioxide atmosphere", "CarbonDioxide", 0.78, 182),
        new("Rocky body", "thin ammonia atmosphere", "Ammonia", 1.18, 171),
        new("Rocky body", "thin sulphur dioxide atmosphere", "SulphurDioxide", 2.35, 205),
        new("Icy body", "thin neon-rich atmosphere", "NeonRich", 1.47, 68),
        new("Rocky ice body", "thin argon atmosphere", "Argon", 0.98, 118, "minor water magma volcanism"),
        new("High metal content body", "thin water atmosphere", "Water", 1.96, 395),
    };
}

/// <summary>A random cargo hold for the Cargo card.</summary>
public sealed class CargoSampleSource : JournalSampleSource
{
    public override string CardKey => "cargo";
    public override string DisplayName => "Cargo";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var picks = SamplePools.PickDistinct(rng, SamplePools.Commodities, rng.Next(2, 7));
        var inventory = new JsonArray();
        var total = 0;
        foreach (var (sym, locName) in picks)
        {
            var count = rng.Next(1, 500);
            total += count;
            inventory.Add(new JsonObject
            {
                ["Name"] = sym,
                ["Name_Localised"] = locName,
                ["Count"] = count,
                ["Stolen"] = 0,
            });
        }

        return new[]
        {
            Event("Cargo", o =>
            {
                o["Vessel"] = "Ship";
                o["Count"] = total;
                o["Inventory"] = inventory;
            }),
            Event("Status", o => o["Cargo"] = total),
        };
    }
}

/// <summary>
/// Everything the unified shopping list reads held counts from: a cargo hold, ship materials
/// (reusing the Cargo and Materials samplers) and an on-foot <c>ShipLocker</c> drawn from the
/// Odyssey catalog, so commodity, engineering and on-foot rows all move on a reshuffle.
/// </summary>
public sealed class ShoppingListSampleSource : JournalSampleSource
{
    public override string CardKey => "shopping";
    public override string DisplayName => "Shopping list";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var lines = new List<string>();
        lines.AddRange(new CargoSampleSource().Sample(rng));
        lines.AddRange(new MaterialsSampleSource().Sample(rng));

        var pool = Odyssey.OdysseyCatalog.Default.Materials;
        lines.Add(Event("ShipLocker", o =>
        {
            foreach (var (prop, category) in new[]
                { ("Items", "Item"), ("Components", "Component"), ("Consumables", "Consumable"), ("Data", "Data") })
            {
                var arr = new JsonArray();
                var ofCategory = pool.Where(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
                if (ofCategory.Count > 0)
                    foreach (var m in SamplePools.PickDistinct(rng, ofCategory, rng.Next(1, ofCategory.Count + 1)))
                        arr.Add(new JsonObject { ["Name"] = m.Symbol, ["OwnerID"] = 0, ["Count"] = rng.Next(1, 30) });
                o[prop] = arr;
            }
        }));
        return lines;
    }
}

/// <summary>
/// A random colonisation construction depot for the Colonisation card, plus a small cargo hold
/// carrying some of what the depot still needs — so the "in hold" cross-reference highlight shows.
/// </summary>
public sealed class ColonisationSampleSource : JournalSampleSource
{
    public override string CardKey => "colonisation";
    public override string DisplayName => "Colonisation";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var system = Pick(rng, SamplePools.Systems);
        var station = "Orbital Construction Site: " + Pick(rng, SamplePools.ConstructionSites);
        var marketId = 3_900_000_000L + rng.Next(0, 99_999_999);

        var picks = SamplePools.PickDistinct(rng, SamplePools.Construction, rng.Next(8, 15));
        var resources = new JsonArray();
        long totalReq = 0, totalProv = 0;
        var outstanding = new List<(string Sym, string Loc, int Remaining)>();

        foreach (var (sym, loc) in picks)
        {
            var required = rng.Next(50, 15_000);
            var provided = rng.Next(0, required + 1);
            totalReq += required;
            totalProv += provided;
            if (required - provided > 0) outstanding.Add((sym, loc, required - provided));

            resources.Add(new JsonObject
            {
                ["Name"] = $"${sym}_name;",
                ["Name_Localised"] = loc,
                ["RequiredAmount"] = required,
                ["ProvidedAmount"] = provided,
                ["Payment"] = rng.Next(500, 12_000),
            });
        }

        var progress = totalReq > 0 ? Math.Round((double)totalProv / totalReq, 4) : 0;

        var events = new List<string>
        {
            // Docked first so the tracker can stamp the site's station name from live state.
            Event("Docked", o =>
            {
                o["StationName"] = station;
                o["StationType"] = "SurfaceStation";
                o["StarSystem"] = system;
            }),
            Event("ColonisationConstructionDepot", o =>
            {
                o["MarketID"] = marketId;
                o["ConstructionProgress"] = progress;
                o["ConstructionComplete"] = false;
                o["ConstructionFailed"] = false;
                o["ResourcesRequired"] = resources;
            }),
        };

        // Stock the hold with a couple of the needed commodities: one fully covered (green ✓),
        // the rest partial — so both cross-reference states are visible.
        var carried = SamplePools.PickDistinct(rng, outstanding, Math.Min(3, outstanding.Count));
        if (carried.Count > 0)
        {
            var inventory = new JsonArray();
            var total = 0;
            for (var i = 0; i < carried.Count; i++)
            {
                var (sym, loc, remaining) = carried[i];
                var count = i == 0 ? remaining + rng.Next(0, 50) : Math.Max(1, remaining / 2);
                total += count;
                inventory.Add(new JsonObject
                {
                    ["Name"] = sym,
                    ["Name_Localised"] = loc,
                    ["Count"] = count,
                    ["Stolen"] = 0,
                });
            }

            events.Add(Event("Cargo", o =>
            {
                o["Vessel"] = "Ship";
                o["Count"] = total;
                o["Inventory"] = inventory;
            }));
            events.Add(Event("Status", o => o["Cargo"] = total));
        }

        return events;
    }
}

/// <summary>
/// A random station commodity market for the Market card: a full board (some goods the station
/// buys, some it sells) plus a small cargo hold carrying a few of the commodities the station has
/// demand for — so the "your hold, sold here" valuation lights up.
/// </summary>
public sealed class MarketSampleSource : JournalSampleSource
{
    public override string CardKey => "market";
    public override string DisplayName => "Market";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var system = Pick(rng, SamplePools.Systems);
        var station = Pick(rng, SamplePools.Stations);
        var marketId = 3_700_000_000L + rng.Next(0, 99_999_999);

        var picks = SamplePools.PickDistinct(rng, SamplePools.MarketGoods, rng.Next(10, SamplePools.MarketGoods.Length + 1));
        var items = new JsonArray();
        var demanded = new List<(string Sym, string Loc)>();

        foreach (var (sym, loc, cat) in picks)
        {
            var mean = rng.Next(200, 200_000);
            var item = new JsonObject
            {
                ["Name"] = $"${sym}_name;",
                ["Name_Localised"] = loc,
                ["Category_Localised"] = cat,
                ["MeanPrice"] = mean,
                ["Rare"] = false,
            };

            // A commodity is usually either supplied (the station sells it) or demanded (it buys it).
            if (rng.Next(2) == 0)
            {
                // Demanded: the station buys it from the commander for roughly its mean price.
                var sell = (int)Math.Round(mean * (0.85 + rng.NextDouble() * 0.4));
                item["BuyPrice"] = 0;
                item["SellPrice"] = sell;
                item["Stock"] = 0;
                item["Demand"] = rng.Next(1, 25_000);
                demanded.Add((sym, loc));
            }
            else
            {
                // Supplied: the station sells it to the commander; no meaningful demand.
                item["BuyPrice"] = (int)Math.Round(mean * (0.8 + rng.NextDouble() * 0.3));
                item["SellPrice"] = 0;
                item["Stock"] = rng.Next(1, 40_000);
                item["Demand"] = 0;
            }

            items.Add(item);
        }

        var events = new List<string>
        {
            Event("Market", o =>
            {
                o["MarketID"] = marketId;
                o["StationName"] = station;
                o["StarSystem"] = system;
                o["Items"] = items;
            }),
        };

        // Stock the hold with a couple of the commodities the station has demand for, so the card's
        // valuation shows real numbers. If nothing is demanded the card falls back to "best sells".
        var carried = SamplePools.PickDistinct(rng, demanded, Math.Min(4, demanded.Count));
        if (carried.Count > 0)
        {
            var inventory = new JsonArray();
            var total = 0;
            foreach (var (sym, loc) in carried)
            {
                var count = rng.Next(4, 200);
                total += count;
                inventory.Add(new JsonObject
                {
                    ["Name"] = sym,
                    ["Name_Localised"] = loc,
                    ["Count"] = count,
                    ["Stolen"] = 0,
                });
            }

            events.Add(Event("Cargo", o =>
            {
                o["Vessel"] = "Ship";
                o["Count"] = total;
                o["Inventory"] = inventory;
            }));
            events.Add(Event("Status", o => o["Cargo"] = total));
        }

        return events;
    }
}

/// <summary>
/// A plausible massacre stack for the Missions card: several kill missions from different giver
/// factions against one common target, a couple against a second target, and a redirect so the
/// hand-in grouping has something ready in it.
/// </summary>
public sealed class MissionsSampleSource : JournalSampleSource
{
    public override string CardKey => "missions";
    public override string DisplayName => "Missions";

    private static readonly string[] FactionSuffixes =
    {
        "Front", "Purple Council", "Jet Comms", "Blue Society", "Crimson Legal Group",
        "Corporation", "Independents", "Interstellar", "Silver Mafia",
    };

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var system = Pick(rng, SamplePools.Systems);
        var station = Pick(rng, SamplePools.Stations);
        var target = $"{system} {Pick(rng, FactionSuffixes)}";

        var lines = new List<string>();
        var missionId = rng.Next(800_000_000, 900_000_000);

        // The stack: several boards, one target. Wing missions, as massacre stacking requires. The
        // first mission gets a short deadline so the card's near-expiry highlight is exercisable in
        // developer mode without waiting a week for a real one to run down.
        var stackSize = rng.Next(3, 7);
        var givers = SamplePools.PickDistinct(rng, FactionSuffixes, stackSize);
        for (var i = 0; i < stackSize; i++)
        {
            var kills = rng.Next(8, 60);
            var expiry = i == 0
                ? DateTimeOffset.UtcNow.AddMinutes(rng.Next(20, 110))
                : DateTimeOffset.UtcNow.AddDays(7);
            lines.Add(Accepted(++missionId, $"{system} {givers[i]}", target, kills,
                rng.Next(400_000, 3_000_000), system, station, expiry));
        }

        // A second, smaller target so the card shows more than one stack.
        var otherTarget = $"{Pick(rng, SamplePools.Systems)} {Pick(rng, FactionSuffixes)}";
        for (var i = 0; i < rng.Next(1, 3); i++)
            lines.Add(Accepted(++missionId, $"{system} {Pick(rng, FactionSuffixes)}", otherTarget,
                rng.Next(6, 25), rng.Next(200_000, 900_000), system, Pick(rng, SamplePools.Stations)));

        // One already finished, so a hand-in group shows as ready.
        lines.Add(Event("MissionRedirected", o =>
        {
            o["MissionID"] = missionId;
            o["Name"] = "Mission_MassacreWing";
            o["NewDestinationSystem"] = system;
            o["NewDestinationStation"] = station;
        }));

        // Kills logged against the main target, for the running tally.
        for (var i = 0; i < rng.Next(0, 15); i++)
            lines.Add(Event("Bounty", o =>
            {
                o["VictimFaction"] = target;
                o["TotalReward"] = rng.Next(20_000, 400_000);
            }));

        return lines;
    }

    private static string Accepted(
        long id, string giver, string target, int kills, int reward, string system, string station,
        DateTimeOffset? expiry = null) =>
        Event("MissionAccepted", o =>
        {
            o["MissionID"] = id;
            o["Faction"] = giver;
            o["Name"] = "Mission_MassacreWing";
            o["LocalisedName"] = $"Kill {kills} Pirates";
            o["TargetType"] = "$MissionUtil_FactionTag_Pirate;";
            o["TargetType_Localised"] = "Pirates";
            o["TargetFaction"] = target;
            o["KillCount"] = kills;
            o["DestinationSystem"] = system;
            o["DestinationStation"] = station;
            o["Expiry"] = (expiry ?? DateTimeOffset.UtcNow.AddDays(7)).ToString("O");
            o["Wing"] = true;
            o["Influence"] = "++";
            o["Reputation"] = "++";
            o["Reward"] = reward;
        });
}

/// <summary>
/// Sample source for the pilot-rank card. Emits the same trio the game does — a <c>Rank</c> snapshot,
/// a matching <c>Progress</c> snapshot, and sometimes a <c>Promotion</c> — so the card, the ladder
/// lookup and the promotion path all get exercised through the real parsers.
/// </summary>
public sealed class RanksSampleSource : JournalSampleSource
{
    public override string CardKey => "ranks";
    public override string DisplayName => "Ranks";

    // The journal's own field names. Mercenary is "Soldier" here, as the game writes it.
    private static readonly string[] Fields = { "Combat", "Trade", "Explore", "Exobiologist", "Soldier" };

    public override IReadOnlyList<string> Sample(Random rng)
    {
        // Bias towards the mid ladder so the card usually shows names rather than a wall of Elite,
        // but let it reach the Elite tiers often enough to exercise them.
        var indices = Fields.ToDictionary(f => f, _ => rng.Next(100) < 15 ? rng.Next(8, 14) : rng.Next(0, 8));

        var lines = new List<string>
        {
            Event("Rank", o =>
            {
                foreach (var (field, index) in indices) o[field] = index;
                o["Empire"] = rng.Next(0, 14);
                o["Federation"] = rng.Next(0, 14);
                o["CQC"] = 0;
            }),
            Event("Progress", o =>
            {
                foreach (var field in Fields) o[field] = rng.Next(0, 100);
                o["Empire"] = rng.Next(0, 100);
                o["Federation"] = rng.Next(0, 100);
                o["CQC"] = 0;
            }),
        };

        // Every so often, promote one rank that has somewhere left to climb.
        if (rng.Next(100) < 40)
        {
            var climbable = Fields.Where(f => indices[f] < 13).ToList();
            if (climbable.Count > 0)
            {
                var field = Pick(rng, climbable);
                lines.Add(Event("Promotion", o => o[field] = indices[field] + 1));
            }
        }

        return lines;
    }
}

/// <summary>
/// Sample source for the mining card: a fabricated <c>ProspectedAsteroid</c> hit — sometimes with a
/// deep-core motherlode — plus a <c>Market</c> snapshot quoting real-looking mean prices, so the
/// price-learning path and the "worth mining" highlight both get exercised without flying anywhere.
/// </summary>
public sealed class MiningSampleSource : JournalSampleSource
{
    public override string CardKey => "mining";
    public override string DisplayName => "Mining";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var picks = SamplePools.PickDistinct(rng, SamplePools.MiningMaterials, rng.Next(1, 4));
        var materials = new JsonArray();
        foreach (var (sym, loc, _) in picks)
            materials.Add(new JsonObject
            {
                ["Name"] = sym,
                ["Name_Localised"] = loc,
                ["Proportion"] = Math.Round(rng.NextDouble() * 35 + 5, 2),
            });

        var content = Pick(rng, new[] { "Low", "Medium", "High" });
        var lines = new List<string>
        {
            Event("ProspectedAsteroid", o =>
            {
                o["Materials"] = materials;
                if (rng.Next(100) < 30)
                {
                    var (sym, loc, _) = Pick(rng, SamplePools.MiningMaterials);
                    o["MotherlodeMaterial"] = sym;
                    o["MotherlodeMaterial_Localised"] = loc;
                }
                o["Content"] = $"$AsteroidMaterialContent_{content};";
                o["Content_Localised"] = $"Material Content: {content}";
                o["Remaining"] = Math.Round(100 - rng.NextDouble() * 40, 1);
            }),
        };

        // A market quote every so often, so the "known prices" count in the card actually moves.
        if (rng.Next(100) < 50)
        {
            var goods = new JsonArray();
            foreach (var (sym, loc, mean) in SamplePools.MiningMaterials)
                goods.Add(new JsonObject
                {
                    ["Name"] = sym,
                    ["Name_Localised"] = loc,
                    ["Category_Localised"] = "Minerals",
                    ["MeanPrice"] = mean,
                    ["BuyPrice"] = 0,
                    ["SellPrice"] = (int)Math.Round(mean * (0.9 + rng.NextDouble() * 0.2)),
                    ["Stock"] = 0,
                    ["Demand"] = rng.Next(1, 5000),
                    ["Rare"] = false,
                });

            lines.Add(Event("Market", o =>
            {
                o["MarketID"] = 3_700_000_000L + rng.Next(0, 99_999_999);
                o["StationName"] = Pick(rng, SamplePools.Stations);
                o["StarSystem"] = Pick(rng, SamplePools.Systems);
                o["Items"] = goods;
            }));
        }

        return lines;
    }
}

/// <summary>
/// Sample source for the Community Goals card. Emits a <c>CommunityGoal</c> snapshot with one or two
/// active goals, sometimes followed by a <c>CommunityGoalJoin</c> and occasionally a
/// <c>CommunityGoalReward</c>, so the card's joined/rewarded states get exercised too.
/// </summary>
public sealed class CommunityGoalSampleSource : JournalSampleSource
{
    public override string CardKey => "community-goals";
    public override string DisplayName => "Community Goals";

    private static readonly string[] Titles =
    {
        "Battle for the Core", "Defence of the Faucett Enterprise", "Helping the Hunt Legion Fight Back",
        "Bright Sun Imperium Combat Support", "Pirate Massacre at Robigo",
    };

    private static readonly string[] Tiers = { "Tier 1", "Tier 2", "Tier 3", "Tier 4", "Tier 5" };

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var lines = new List<string>();
        var goals = new JsonArray();

        var primaryId = rng.Next(10_000, 99_999);
        var system = Pick(rng, SamplePools.Systems);
        var station = Pick(rng, SamplePools.Stations);
        var title = Pick(rng, Titles);
        var total = rng.Next(500_000, 50_000_000);
        var mine = rng.Next(1_000, 200_000);
        var tierIndex = rng.Next(0, Tiers.Length);

        goals.Add(new JsonObject
        {
            ["CGID"] = primaryId,
            ["Title"] = title,
            ["SystemName"] = system,
            ["MarketName"] = station,
            ["Expiry"] = DateTimeOffset.UtcNow.AddDays(rng.Next(1, 10)).ToString("O"),
            ["IsComplete"] = false,
            ["CurrentTotal"] = total,
            ["PlayerContribution"] = mine,
            ["NumContributors"] = rng.Next(200, 8000),
            ["TopRankSize"] = 10,
            ["TopTier"] = new JsonObject { ["Name"] = Tiers[^1], ["Bonus"] = (5_000_000).ToString() },
            ["TierReached"] = Tiers[tierIndex],
            ["PlayerInTopRank"] = rng.Next(100) < 15,
            ["PlayerPercentileBand"] = rng.Next(1, 100),
        });

        // A second, smaller goal so the list shows more than one card.
        if (rng.Next(100) < 60)
        {
            var secondId = primaryId + 1;
            goals.Add(new JsonObject
            {
                ["CGID"] = secondId,
                ["Title"] = Pick(rng, Titles),
                ["SystemName"] = Pick(rng, SamplePools.Systems),
                ["MarketName"] = Pick(rng, SamplePools.Stations),
                ["Expiry"] = DateTimeOffset.UtcNow.AddDays(rng.Next(1, 14)).ToString("O"),
                ["IsComplete"] = false,
                ["CurrentTotal"] = rng.Next(100_000, 5_000_000),
                ["PlayerContribution"] = 0,
                ["NumContributors"] = rng.Next(50, 2000),
            });
        }

        lines.Add(Event("CommunityGoal", o => o["CurrentGoals"] = goals));

        // Most of the time this commander has actually joined the primary goal.
        if (rng.Next(100) < 70)
            lines.Add(Event("CommunityGoalJoin", o =>
            {
                o["CGID"] = primaryId;
                o["Name"] = title;
                o["System"] = system;
            }));

        // Occasionally show a completed, rewarded goal too.
        if (rng.Next(100) < 25)
            lines.Add(Event("CommunityGoalReward", o =>
            {
                o["CGID"] = primaryId;
                o["Name"] = title;
                o["System"] = system;
                o["Reward"] = rng.Next(50_000, 2_000_000);
                o["Bonus"] = rng.Next(0, 500_000);
            }));

        return lines;
    }
}

/// <summary>
/// Dev sample source for the overlay and voice-callout feature: fabricates a low-fuel status update,
/// a completed exobiology three-sample run, and a colonisation cargo delivery that fully covers a
/// shopping-list commodity — the three moments <see cref="Voice.VoiceCalloutTracker"/> turns into
/// spoken callouts, and the same state the overlay's content is built from — so both are exercisable
/// without flying anywhere. Has no dashboard card of its own; it's triggered from Developer Options.
/// </summary>
public sealed class OverlayVoiceSampleSource : JournalSampleSource
{
    public override string CardKey => "overlay-voice";
    public override string DisplayName => "Overlay & Voice";

    public override IReadOnlyList<string> Sample(Random rng)
    {
        var catalog = Exobio.ExobiologyCatalog.Default;
        var genus = Pick(rng, catalog.Genera);
        var species = Pick(rng, genus.Species);
        var systemAddress = (long)rng.Next(1_000_000, int.MaxValue) * 1000;
        var bodyId = rng.Next(1, 40);
        const double capacity = 32.0;

        var (constructionSym, constructionLoc) = Pick(rng, SamplePools.Construction);
        var marketId = 3_950_000_000L + rng.Next(0, 99_999_999);

        return new[]
        {
            // Fuel low enough to cross the callout threshold (well under a quarter of the tank).
            Event("Loadout", o => o["FuelCapacity"] = new JsonObject { ["Main"] = capacity }),
            Event("Status", o => o["Fuel"] = new JsonObject { ["FuelMain"] = Math.Round(capacity * 0.1, 1) }),

            // A completed three-sample scan run: Log, Sample, then the closing Analyse.
            ScanOrganicEvent("Log", species, systemAddress, bodyId),
            ScanOrganicEvent("Sample", species, systemAddress, bodyId),
            ScanOrganicEvent("Analyse", species, systemAddress, bodyId),

            // A depot needing one commodity, then a cargo hold that fully covers it.
            Event("ColonisationConstructionDepot", o =>
            {
                o["MarketID"] = marketId;
                o["ConstructionProgress"] = 0.5;
                o["ConstructionComplete"] = false;
                o["ConstructionFailed"] = false;
                o["ResourcesRequired"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Name"] = $"${constructionSym}_name;",
                        ["Name_Localised"] = constructionLoc,
                        ["RequiredAmount"] = 50,
                        ["ProvidedAmount"] = 0,
                        ["Payment"] = 1000,
                    },
                };
            }),
            Event("Cargo", o =>
            {
                o["Vessel"] = "Ship";
                o["Count"] = 60;
                o["Inventory"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Name"] = constructionSym,
                        ["Name_Localised"] = constructionLoc,
                        ["Count"] = 60,
                        ["Stolen"] = 0,
                    },
                };
            }),
        };
    }

    private static string ScanOrganicEvent(string stage, Exobio.BioSpecies species, long systemAddress, int bodyId) =>
        Event("ScanOrganic", o =>
        {
            o["ScanType"] = stage;
            o["Genus"] = species.GenusSymbol;
            o["Genus_Localised"] = species.Genus;
            o["Species"] = species.Symbol;
            o["Species_Localised"] = species.Name;
            o["SystemAddress"] = systemAddress;
            o["Body"] = bodyId;
        });
}
