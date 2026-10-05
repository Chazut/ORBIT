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
    public float SniperMinZoom { get; set; } = 4;
    public float SniperDisengageDistance { get; set; } = 60;
    public bool SniperCombatHold { get; set; } = true;
    public int SniperCatalogueVersion { get; set; } = 1;
    public RushStyle Timmy { get; set; } = new(.05f, 0, 1, 1, 1) { SniperWeight = 2, SniperHoldMin = 90, SniperHoldMax = 180 };
    public RushStyle Cautious { get; set; } = new(.15f, 0, 1, 3, 1) { SniperWeight = 6, SniperHoldMin = 180, SniperHoldMax = 360 };
    public RushStyle Average { get; set; } = new(.25f, 0, 2, 2, 1);
    public RushStyle Aggressive { get; set; } = new(.5f, 3, 3, 2, 1) { SniperWeight = 1, SniperHoldMin = 60, SniperHoldMax = 120 };
    public RushStyle VeryAggressive { get; set; } = new(.8f, 5, 3, 2, 1) { SniperWeight = .5f, SniperHoldMin = 30, SniperHoldMax = 90 };
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
        MapBehaviorOverride.Range(SniperMinZoom, 1.5f, 20, "Sniper minimum zoom");
        MapBehaviorOverride.Range(SniperDisengageDistance, 50, 200, "Sniper close threat distance");
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
    public float SniperWeight { get; set; } = 3;
    public float SniperStartWindow { get; set; } = 600;
    public float SniperHoldMin { get; set; } = 120;
    public float SniperHoldMax { get; set; } = 240;
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
        "Sniper" when raidSeconds <= SniperStartWindow => SniperWeight,
        _ => 0
    };
    public void Validate()
    {
        MapBehaviorOverride.Range(Chance, 0, 1, "Rush chance");
        foreach (var w in new[] { SpawnWeight, BossWeight, MarkedWeight, CustomWeight, SniperWeight })
            MapBehaviorOverride.Range(w, 0, 100, "Rush type weight");
        foreach (var w in new[] { SpawnStartWindow, BossStartWindow, MarkedStartWindow, CustomStartWindow, SniperStartWindow })
            MapBehaviorOverride.Range(w, 0, 3600, "Rush start window");
        MapBehaviorOverride.Range(SearchScale, .1f, 5, "Rush search time scale");
        MapBehaviorOverride.Range(SniperHoldMin, 10, 1800, "Sniper minimum hold");
        MapBehaviorOverride.Range(SniperHoldMax, SniperHoldMin, 1800, "Sniper maximum hold");
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
    public float WatchYaw { get; set; }
    public float WatchArc { get; set; } = 120;
    public float WatchDistance { get; set; } = 150;
    public float WatchPitch { get; set; } = -5;
    // Authored seed is ground level; select a reachable roof 3-16 m above it at runtime.
    public bool Elevated { get; set; }
    public RushPoint Copy() => (RushPoint)MemberwiseClone();
    public void Validate()
    {
        if (Kind != "Spawn" && Kind != "Boss" && Kind != "Marked" && Kind != "Custom" && Kind != "Sniper")
            throw new ArgumentException("Unknown rush category.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Id.Length > 160)
            throw new ArgumentException("Invalid rush point name or ID.");
        foreach (var n in new[] { X, Y, Z, LootX, LootY, LootZ })
            MapBehaviorOverride.Range(n, -10000, 10000, "Rush coordinate");
        MapBehaviorOverride.Range(Radius, 1, 200, "Rush radius");
        MapBehaviorOverride.Range(SearchSeconds, 1, 600, "Rush search duration");
        MapBehaviorOverride.Range(Weight, 0, 100, "Rush point weight");
        MapBehaviorOverride.Range(WatchYaw, 0, 360, "Sniper watch bearing");
        MapBehaviorOverride.Range(WatchArc, 15, 360, "Sniper watch arc");
        MapBehaviorOverride.Range(WatchDistance, 50, 500, "Sniper sightline distance");
        MapBehaviorOverride.Range(WatchPitch, -45, 30, "Sniper watch elevation");
        if (Kind == "Marked" && string.IsNullOrWhiteSpace(DoorId))
            throw new ArgumentException("Marked rush needs an exact door ID.");
    }
}
