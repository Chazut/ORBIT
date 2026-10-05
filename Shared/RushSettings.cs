#nullable disable
using System;
using System.Collections.Generic;

namespace Orbit.Settings;

public sealed class RushSettings
{
    public bool Enabled { get; set; } = true;
    public float TravelTimeout { get; set; } = 600;
    public float OwnSpawnExclusion { get; set; } = 80;
    public int SpawnSectors { get; set; } = 2;
    public float SpawnDeadline { get; set; } = 300;
    public RushStyle Timmy { get; set; } = new(.05f, 0, 1, 1, 1);
    public RushStyle Cautious { get; set; } = new(.15f, 0, 1, 3, 1);
    public RushStyle Average { get; set; } = new(.25f, 0, 2, 2, 1);
    public RushStyle Aggressive { get; set; } = new(.5f, 3, 3, 2, 1);
    public RushStyle VeryAggressive { get; set; } = new(.8f, 5, 3, 2, 1);
    // Lists are complete map catalogues. Variants inherit the base map until explicitly customized.
    public Dictionary<string, List<RushPoint>> Maps { get; set; } = RushDefaults.Create();
    public RushStyle Style(string archetype) => archetype switch
    {
        "Timmy" => Timmy, "Cautious" => Cautious, "Aggressive" => Aggressive,
        "VeryAggressive" => VeryAggressive, _ => Average
    };
    public List<RushPoint> Points(string map)
    {
        foreach (var pair in Maps)
            if (string.Equals(pair.Key, map, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        var at = map.IndexOf('@');
        return at >= 0 ? Points(map.Substring(0, at)) : new List<RushPoint>();
    }
    public bool Allows(string category) => Enabled && category == "PMC";
    public void Validate()
    {
        MapBehaviorOverride.Range(TravelTimeout, 30, 1800, "Rush travel timeout");
        MapBehaviorOverride.Range(OwnSpawnExclusion, 10, 500, "Rush own spawn exclusion");
        MapBehaviorOverride.Range(SpawnSectors, 1, 10, "Rush spawn sectors");
        MapBehaviorOverride.Range(SpawnDeadline, 30, 1200, "Rush spawn deadline");
        foreach (var s in new[] { Timmy, Cautious, Average, Aggressive, VeryAggressive })
            (s ?? throw new ArgumentException("Rush styles must be objects.")).Validate();
        if (Maps == null || Maps.Count > 40) throw new ArgumentException("Invalid rush map catalogue.");
        foreach (var map in Maps)
        {
            if (map.Value == null || map.Value.Count > 256) throw new ArgumentException("At most 256 rush points per map.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in map.Value)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id))
                    throw new ArgumentException("Rush points need unique non-empty IDs within a map.");
                p.Validate();
                if (p.Kind == "Boss" && !RushDefaults.IsResident(map.Key, p.Boss))
                    throw new ArgumentException("Boss rush requires a resident boss of this map.");
            }
        }
    }
}

public sealed class RushStyle
{
    public float Chance { get; set; } = .25f;
    public float SpawnWeight { get; set; } = 1;
    public float BossWeight { get; set; } = 2;
    public float MarkedWeight { get; set; } = 2;
    public float CustomWeight { get; set; } = 1;
    public float SpawnStartWindow { get; set; } = 120;
    public float BossStartWindow { get; set; } = 600;
    public float MarkedStartWindow { get; set; } = 600;
    public float CustomStartWindow { get; set; } = 600;
    public float SearchScale { get; set; } = 1;
    public RushStyle() { }
    public RushStyle(float chance, float spawn, float boss, float marked, float custom)
    { Chance = chance; SpawnWeight = spawn; BossWeight = boss; MarkedWeight = marked; CustomWeight = custom; }
    public float Weight(string kind, float raidSeconds) => kind switch
    {
        "Spawn" when raidSeconds <= SpawnStartWindow => SpawnWeight,
        "Boss" when raidSeconds <= BossStartWindow => BossWeight,
        "Marked" when raidSeconds <= MarkedStartWindow => MarkedWeight,
        "Custom" when raidSeconds <= CustomStartWindow => CustomWeight,
        _ => 0
    };
    public void Validate()
    {
        MapBehaviorOverride.Range(Chance, 0, 1, "Rush chance");
        foreach (var w in new[] { SpawnWeight, BossWeight, MarkedWeight, CustomWeight })
            MapBehaviorOverride.Range(w, 0, 100, "Rush type weight");
        foreach (var w in new[] { SpawnStartWindow, BossStartWindow, MarkedStartWindow, CustomStartWindow })
            MapBehaviorOverride.Range(w, 0, 3600, "Rush start window");
        MapBehaviorOverride.Range(SearchScale, .1f, 5, "Rush search time scale");
    }
}

public sealed class RushPoint
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Rush point";
    public string Kind { get; set; } = "Custom";
    public bool Enabled { get; set; } = true;
    public string Boss { get; set; } = "";
    public string DoorId { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float LootX { get; set; }
    public float LootY { get; set; }
    public float LootZ { get; set; }
    public float Radius { get; set; } = 20;
    public float SearchSeconds { get; set; } = 25;
    public float Weight { get; set; } = 1;
    public string FloorId { get; set; }
    public RushPoint Copy() => (RushPoint)MemberwiseClone();
    public void Validate()
    {
        if (Kind != "Spawn" && Kind != "Boss" && Kind != "Marked" && Kind != "Custom")
            throw new ArgumentException("Unknown rush category.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Id.Length > 160)
            throw new ArgumentException("Invalid rush point name or ID.");
        foreach (var n in new[] { X, Y, Z, LootX, LootY, LootZ })
            MapBehaviorOverride.Range(n, -10000, 10000, "Rush coordinate");
        MapBehaviorOverride.Range(Radius, 1, 200, "Rush radius");
        MapBehaviorOverride.Range(SearchSeconds, 1, 600, "Rush search duration");
        MapBehaviorOverride.Range(Weight, 0, 100, "Rush point weight");
        if (Kind == "Marked" && string.IsNullOrWhiteSpace(DoorId))
            throw new ArgumentException("Marked rush needs an exact door ID.");
    }
}
