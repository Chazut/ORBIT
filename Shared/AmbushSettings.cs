#nullable disable
using System;

namespace Orbit.Settings;

public enum CampSiteKind { Hotspot, Extract, Airdrop }

public sealed class AmbushSettings
{
    public bool Enabled { get; set; } = true;
    public bool Pmc { get; set; } = true;
    public bool PlayerScav { get; set; } = true;
    public bool RequireCover { get; set; } = true;
    public float CheckInterval { get; set; } = 60;
    public float Cooldown { get; set; } = 300;
    public float TravelTimeout { get; set; } = 120;
    public int MaxActiveSquads { get; set; } = 3;
    public int MaxCampsPerSquad { get; set; } = 2;
    public float MemberSpacing { get; set; } = 4;
    public CampSiteSettings Hotspots { get; set; } = new();
    public CampSiteSettings Extracts { get; set; } = new() { Chance = .1f, DurationMin = 60, DurationMax = 180, DistanceMin = 20, DistanceMax = 45 };
    public CampSiteSettings Airdrops { get; set; } = new() { Chance = .3f, DistanceMin = 15, DistanceMax = 40 };

    public CampSiteSettings For(CampSiteKind kind) => kind switch
    {
        CampSiteKind.Extract => Extracts,
        CampSiteKind.Airdrop => Airdrops,
        _ => Hotspots
    };

    public bool Allows(string type) => Enabled && (type == "PMC" && Pmc || type == "PlayerScav" && PlayerScav);

    public void Validate()
    {
        MapBehaviorOverride.Range(CheckInterval, 15, 600, "Ambush check interval");
        MapBehaviorOverride.Range(Cooldown, 30, 1800, "Ambush cooldown");
        MapBehaviorOverride.Range(TravelTimeout, 15, 300, "Ambush travel timeout");
        MapBehaviorOverride.Range(MaxActiveSquads, 1, 12, "Ambush active squads");
        MapBehaviorOverride.Range(MaxCampsPerSquad, 1, 10, "Ambush camps per squad");
        MapBehaviorOverride.Range(MemberSpacing, 3, 12, "Ambush member spacing");
        foreach (CampSiteKind kind in Enum.GetValues(typeof(CampSiteKind)))
            (For(kind) ?? throw new ArgumentException("Ambush target settings must be objects.")).Validate();
    }
}

public sealed class CampSiteSettings
{
    public bool Enabled { get; set; } = true;
    // One roll per nearby target category at each check, never one roll per point.
    public float Chance { get; set; } = .15f;
    public float DurationMin { get; set; } = 45;
    public float DurationMax { get; set; } = 120;
    public float DistanceMin { get; set; } = 10;
    public float DistanceMax { get; set; } = 35;
    public float SearchRadius { get; set; } = 200;
    public int MaxSquads { get; set; } = 1;

    public void Validate()
    {
        MapBehaviorOverride.Range(Chance, 0, 1, "Ambush chance");
        MapBehaviorOverride.Range(DurationMin, 10, 600, "Ambush minimum duration");
        MapBehaviorOverride.Range(DurationMax, DurationMin, 600, "Ambush maximum duration");
        MapBehaviorOverride.Range(DistanceMin, 5, 80, "Ambush minimum distance");
        MapBehaviorOverride.Range(DistanceMax, DistanceMin, 80, "Ambush maximum distance");
        MapBehaviorOverride.Range(SearchRadius, 25, 400, "Ambush search radius");
        MapBehaviorOverride.Range(MaxSquads, 1, 4, "Ambush squads per target");
    }
}
