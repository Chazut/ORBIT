using UnityEngine;

namespace Orbit.Sain;

/// <summary>
/// Hard-coded fallback values for squads with NO resolved SAIN archetype — PlayerScavs, and PMC squads with
/// SAIN personality turned OFF. These used to be the "global" main-objective F12 knobs, but PMC squads with
/// SAIN personality (the default) override them per-archetype (sections 09.1–09.5), so exposing them in F12
/// only misled users into tuning values that never applied. PlayerScav-specific knobs stay configurable in the
/// "PlayerScav" F12 section; everything an archetype overrides lives here as a constant.
/// </summary>
public static class PersonalityFallback
{
    public const int PmcMainCountMin = 1;
    public const int PmcMainCountMax = 5;
    public const float PmcMixQuest = 0.70f;
    public const float PmcMixKills = 0.15f;
    public const float PmcMixLootValue = 0.15f;
    public static readonly Vector2 KillsRoamDuration = new(60f, 300f);
    public const float LootCoverage = 0.7f;
    public const float ScavengeSweepRadius = 10f;
    public const float SplinterSearchRadius = 30f;
    public const int TopLootCellsMax = 10;
    public const float LockedDoorUnlockProba = 0.3f;
}
