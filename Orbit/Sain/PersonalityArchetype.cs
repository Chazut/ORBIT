namespace Orbit.Sain;

/// <summary>
/// Coarse personality buckets mapped from SAIN brain names. PMC-only — scavs / PlayerScavs always use the
/// global Orbit knobs even when SAIN personality is enabled. The mapping table from SAIN brain → archetype is
/// F12-configurable; default mapping: Timmy→Timmy ; Rat/Coward/SnappingTurtle→Cautious ; Normal→Average ;
/// Wreckless/Chad→Aggressive ; GigaChad→VeryAggressive ; any unmatched brain falls back to Average.
/// </summary>
public enum PersonalityArchetype
{
    Timmy,
    Cautious,
    Average,
    Aggressive,
    VeryAggressive,
}
