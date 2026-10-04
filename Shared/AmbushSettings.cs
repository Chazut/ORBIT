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
    public float MemberSpacing { get; set; } = 4;
    public CampSiteSettings Hotspots { get; set; } = new();
    public CampSiteSettings Extracts { get; set; } = new() { DistanceMin = 20, DistanceMax = 45 };
    public CampSiteSettings Airdrops { get; set; } = new() { DistanceMin = 15, DistanceMax = 40 };

    public AmbushStyleSettings Timmy { get; set; } = new(.15f, .05f, 30, 90, .20f, 20, 60);
    public AmbushStyleSettings Cautious { get; set; } = new(.65f, .25f, 120, 300, .65f, 90, 180);
    public AmbushStyleSettings Average { get; set; } = new(.30f, .10f, 60, 150, .40f, 45, 120);
    public AmbushStyleSettings Aggressive { get; set; } = new(.15f, .05f, 30, 90, .25f, 20, 60);
    public AmbushStyleSettings VeryAggressive { get; set; } = new(.05f, .02f, 20, 60, .15f, 10, 40);
    public AmbushStyleSettings PlayerScavStyle { get; set; } = new(.25f, .10f, 45, 120, .45f, 30, 90);

    public AmbushStyleSettings Style(string archetype, bool playerScav = false) => playerScav ? PlayerScavStyle : archetype switch
    {
        "Timmy" => Timmy,
        "Cautious" => Cautious,
        "Aggressive" => Aggressive,
        "VeryAggressive" => VeryAggressive,
        _ => Average
    };

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
        MapBehaviorOverride.Range(MemberSpacing, 3, 12, "Ambush member spacing");
        foreach (CampSiteKind kind in Enum.GetValues(typeof(CampSiteKind)))
            (For(kind) ?? throw new ArgumentException("Ambush target settings must be objects.")).Validate();
        foreach (var style in new[] { Timmy, Cautious, Average, Aggressive, VeryAggressive, PlayerScavStyle })
            (style ?? throw new ArgumentException("Ambush personality settings must be objects.")).Validate();
    }
}

public sealed class CampSiteSettings
{
    public bool Enabled { get; set; } = true;
    public float DistanceMin { get; set; } = 10;
    public float DistanceMax { get; set; } = 35;
    public float SearchRadius { get; set; } = 200;

    public void Validate()
    {
        MapBehaviorOverride.Range(DistanceMin, 5, 80, "Ambush minimum distance");
        MapBehaviorOverride.Range(DistanceMax, DistanceMin, 80, "Ambush maximum distance");
        MapBehaviorOverride.Range(SearchRadius, 25, 400, "Ambush search radius");
    }
}

public sealed class AmbushStyleSettings
{
    public float KillAmbushChance { get; set; }
    public float ExtractMainChance { get; set; }
    public float ExtractDurationMin { get; set; }
    public float ExtractDurationMax { get; set; }
    public float AirdropChance { get; set; }
    public float AirdropDurationMin { get; set; }
    public float AirdropDurationMax { get; set; }

    public AmbushStyleSettings() : this(.30f, .10f, 60, 150, .40f, 45, 120) { }

    public AmbushStyleSettings(float kill, float extract, float extractMin, float extractMax,
        float airdrop, float airdropMin, float airdropMax)
    {
        KillAmbushChance = kill; ExtractMainChance = extract;
        ExtractDurationMin = extractMin; ExtractDurationMax = extractMax;
        AirdropChance = airdrop; AirdropDurationMin = airdropMin; AirdropDurationMax = airdropMax;
    }

    public void Validate()
    {
        MapBehaviorOverride.Range(KillAmbushChance, 0, 1, "Kill ambush chance");
        MapBehaviorOverride.Range(ExtractMainChance, 0, 1, "Extract camp main chance");
        MapBehaviorOverride.Range(AirdropChance, 0, 1, "Airdrop ambush chance");
        MapBehaviorOverride.Range(ExtractDurationMin, 10, 1800, "Extract camp minimum duration");
        MapBehaviorOverride.Range(ExtractDurationMax, ExtractDurationMin, 1800, "Extract camp maximum duration");
        MapBehaviorOverride.Range(AirdropDurationMin, 10, 1800, "Airdrop ambush minimum duration");
        MapBehaviorOverride.Range(AirdropDurationMax, AirdropDurationMin, 1800, "Airdrop ambush maximum duration");
    }
}
