#nullable disable
using System;

namespace Orbit.Settings;

public sealed class TravelStyle
{
    public bool Enabled { get; set; } = true;
    public float CoverPreference { get; set; } = .35f;
    public float MaxDetourRatio { get; set; } = 1.2f;
    public float PauseChance { get; set; } = .12f;
    public float PauseDurationMin { get; set; } = .5f;
    public float PauseDurationMax { get; set; } = 1.5f;
    public float PauseInterval { get; set; } = 40f;
    public float ExposedSprintChance { get; set; } = .65f;

    public static TravelStyle For(string archetype) => archetype switch
    {
        "Timmy" => new() { CoverPreference = .5f, MaxDetourRatio = 1.3f, PauseChance = .65f,
            PauseDurationMin = 1.5f, PauseDurationMax = 3.5f, PauseInterval = 22f, ExposedSprintChance = .35f },
        "Cautious" => new() { CoverPreference = 1f, MaxDetourRatio = 1.6f, PauseChance = .35f,
            PauseDurationMin = 1f, PauseDurationMax = 2.5f, PauseInterval = 30f, ExposedSprintChance = .9f },
        "Aggressive" => new() { CoverPreference = .12f, MaxDetourRatio = 1.1f, PauseChance = .04f,
            PauseDurationMin = .3f, PauseDurationMax = 1f, PauseInterval = 50f, ExposedSprintChance = .9f },
        "VeryAggressive" => new() { CoverPreference = 0f, MaxDetourRatio = 1f, PauseChance = 0f,
            PauseDurationMin = 0f, PauseDurationMax = 0f, PauseInterval = 60f, ExposedSprintChance = 1f },
        _ => new(),
    };

    public TravelStyle Copy() => (TravelStyle)MemberwiseClone();
    public void Validate()
    {
        MapBehaviorOverride.Range(CoverPreference, 0, 1, "Travel cover preference");
        MapBehaviorOverride.Range(MaxDetourRatio, 1, 2, "Travel maximum detour");
        MapBehaviorOverride.Range(PauseChance, 0, 1, "Travel observation chance");
        MapBehaviorOverride.Range(PauseDurationMin, 0, 5, "Travel observation duration min");
        MapBehaviorOverride.Range(PauseDurationMax, PauseDurationMin, 5, "Travel observation duration max");
        MapBehaviorOverride.Range(PauseInterval, 10, 180, "Travel observation interval");
        MapBehaviorOverride.Range(ExposedSprintChance, 0, 1, "Travel exposed sprint chance");
    }

    // Exposure is geometric, independent of undiscovered enemies or the human player's position.
    public float RouteCost(float length, float exposedDistance) => length + CoverPreference * 4f * exposedDistance;
    public bool Accepts(float baselineLength, float baselineExposure, float candidateLength, float candidateExposure)
        => candidateLength <= baselineLength * MaxDetourRatio
            && RouteCost(candidateLength, candidateExposure) + .5f < RouteCost(baselineLength, baselineExposure);
}
