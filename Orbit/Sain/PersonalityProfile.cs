using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Sain;

/// <summary>
/// Per-squad bundle of values rolled once at squad creation from the
/// leader's archetype tables. PMC squads with SAIN personality
/// resolution active read these; scavs / PlayerScavs ignore the bundle
/// entirely and use the global knobs. Vector2 source ranges are rolled
/// to single floats (or ints) here so dispatch reads are cheap and
/// behaviour is deterministic across the raid.
/// </summary>
public sealed class PersonalityProfile
{
    public readonly PersonalityArchetype Archetype;

    // Main objective generation
    public readonly float MainMixQuestWeight;
    public readonly float MainMixKillsWeight;
    public readonly float MainMixLootValueWeight;
    public readonly int MainCount;
    public readonly Vector2 KillsRoamDuration; // kept as range — re-rolled per-Kills-main inside generator
    public readonly int TopLootCellsMax;

    // Loot behaviour
    public readonly float ExtractLootThreshold;
    public readonly float LootCoverage;
    public readonly float ScavengeSweepRadius;
    public readonly float SplinterSearchRadius;
    public readonly int MiniLootValueThreshold;
    public readonly float LockedDoorUnlockProba;

    // Per-member (stored on the squad — every member reads the squad's
    // profile rather than rolling per-Agent for cost reasons).
    public readonly float SprintPropensity;

    private PersonalityProfile(
        PersonalityArchetype archetype,
        float mainQ, float mainK, float mainL,
        int mainCount,
        Vector2 killsRoamDuration,
        int topLootCellsMax,
        float extractLootThreshold,
        float lootCoverage,
        float sweepRadius,
        float splinterRadius,
        int miniLootValueThreshold,
        float lockedDoorProba,
        float sprintPropensity)
    {
        Archetype = archetype;
        MainMixQuestWeight = mainQ;
        MainMixKillsWeight = mainK;
        MainMixLootValueWeight = mainL;
        MainCount = mainCount;
        KillsRoamDuration = killsRoamDuration;
        TopLootCellsMax = topLootCellsMax;
        ExtractLootThreshold = extractLootThreshold;
        LootCoverage = lootCoverage;
        ScavengeSweepRadius = sweepRadius;
        SplinterSearchRadius = splinterRadius;
        MiniLootValueThreshold = miniLootValueThreshold;
        LockedDoorUnlockProba = lockedDoorProba;
        SprintPropensity = sprintPropensity;
    }

    /// <summary>
    /// Roll just the extract-loot threshold for the given archetype.
    /// Used by per-agent threshold resolution: each squad member rolls
    /// their own threshold based on their own SAIN brain so a mixed
    /// squad of Rat + Chad sums to Rat-range + Chad-range, not 2× one.
    /// </summary>
    public static float RollExtractThresholdFor(PersonalityArchetype archetype)
    {
        var range = ResolveTable(archetype).ExtractLootThreshold;
        return Random.Range(range.x, range.y);
    }

    /// <summary>
    /// Roll a profile for the given archetype using the F12 tables. Each
    /// Vector2 range is sampled once; scalars are passed through. Called
    /// at squad registration when the leader is PMC and SAIN personality
    /// resolution is enabled.
    /// </summary>
    public static PersonalityProfile Roll(PersonalityArchetype archetype)
    {
        var t = ResolveTable(archetype);
        var mainCountRange = t.MainCount;
        var extractRange = t.ExtractLootThreshold;
        var coverageRange = t.LootCoverage;
        return new PersonalityProfile(
            archetype,
            mainQ: t.MainMixQuest,
            mainK: t.MainMixKills,
            mainL: t.MainMixLootValue,
            mainCount: Random.Range(Mathf.RoundToInt(mainCountRange.x), Mathf.RoundToInt(mainCountRange.y) + 1),
            killsRoamDuration: t.KillsRoamDuration,
            topLootCellsMax: t.TopLootCellsMax,
            extractLootThreshold: Random.Range(extractRange.x, extractRange.y),
            lootCoverage: Random.Range(coverageRange.x, coverageRange.y),
            sweepRadius: t.ScavengeSweepRadius,
            splinterRadius: t.SplinterSearchRadius,
            miniLootValueThreshold: t.MiniLootValueThreshold,
            lockedDoorProba: t.LockedDoorUnlockProba,
            sprintPropensity: t.SprintPropensity);
    }

    private static ArchetypeTable ResolveTable(PersonalityArchetype a) => a switch
    {
        PersonalityArchetype.Timmy => new ArchetypeTable(
            mainMixQ: Plugin.SainTimmyMainMixQ.Value,
            mainMixK: Plugin.SainTimmyMainMixK.Value,
            mainMixL: Plugin.SainTimmyMainMixL.Value,
            mainCount: Plugin.SainTimmyMainCount.Value,
            killsRoamDuration: Plugin.SainTimmyKillsRoamDuration.Value,
            topLootCellsMax: Plugin.SainTimmyTopLootCellsMax.Value,
            extractLootThreshold: Plugin.SainTimmyExtractThreshold.Value,
            lootCoverage: Plugin.SainTimmyLootCoverage.Value,
            scavengeSweepRadius: Plugin.SainTimmyScavengeSweepRadius.Value,
            splinterSearchRadius: Plugin.SainTimmySplinterSearchRadius.Value,
            miniLootValueThreshold: Plugin.SainTimmyMiniLootThreshold.Value,
            lockedDoorUnlockProba: Plugin.SainTimmyLockedDoorProba.Value,
            sprintPropensity: Plugin.SainTimmySprintPropensity.Value),
        PersonalityArchetype.Cautious => new ArchetypeTable(
            mainMixQ: Plugin.SainCautiousMainMixQ.Value,
            mainMixK: Plugin.SainCautiousMainMixK.Value,
            mainMixL: Plugin.SainCautiousMainMixL.Value,
            mainCount: Plugin.SainCautiousMainCount.Value,
            killsRoamDuration: Plugin.SainCautiousKillsRoamDuration.Value,
            topLootCellsMax: Plugin.SainCautiousTopLootCellsMax.Value,
            extractLootThreshold: Plugin.SainCautiousExtractThreshold.Value,
            lootCoverage: Plugin.SainCautiousLootCoverage.Value,
            scavengeSweepRadius: Plugin.SainCautiousScavengeSweepRadius.Value,
            splinterSearchRadius: Plugin.SainCautiousSplinterSearchRadius.Value,
            miniLootValueThreshold: Plugin.SainCautiousMiniLootThreshold.Value,
            lockedDoorUnlockProba: Plugin.SainCautiousLockedDoorProba.Value,
            sprintPropensity: Plugin.SainCautiousSprintPropensity.Value),
        PersonalityArchetype.Aggressive => new ArchetypeTable(
            mainMixQ: Plugin.SainAggressiveMainMixQ.Value,
            mainMixK: Plugin.SainAggressiveMainMixK.Value,
            mainMixL: Plugin.SainAggressiveMainMixL.Value,
            mainCount: Plugin.SainAggressiveMainCount.Value,
            killsRoamDuration: Plugin.SainAggressiveKillsRoamDuration.Value,
            topLootCellsMax: Plugin.SainAggressiveTopLootCellsMax.Value,
            extractLootThreshold: Plugin.SainAggressiveExtractThreshold.Value,
            lootCoverage: Plugin.SainAggressiveLootCoverage.Value,
            scavengeSweepRadius: Plugin.SainAggressiveScavengeSweepRadius.Value,
            splinterSearchRadius: Plugin.SainAggressiveSplinterSearchRadius.Value,
            miniLootValueThreshold: Plugin.SainAggressiveMiniLootThreshold.Value,
            lockedDoorUnlockProba: Plugin.SainAggressiveLockedDoorProba.Value,
            sprintPropensity: Plugin.SainAggressiveSprintPropensity.Value),
        PersonalityArchetype.VeryAggressive => new ArchetypeTable(
            mainMixQ: Plugin.SainVeryAggressiveMainMixQ.Value,
            mainMixK: Plugin.SainVeryAggressiveMainMixK.Value,
            mainMixL: Plugin.SainVeryAggressiveMainMixL.Value,
            mainCount: Plugin.SainVeryAggressiveMainCount.Value,
            killsRoamDuration: Plugin.SainVeryAggressiveKillsRoamDuration.Value,
            topLootCellsMax: Plugin.SainVeryAggressiveTopLootCellsMax.Value,
            extractLootThreshold: Plugin.SainVeryAggressiveExtractThreshold.Value,
            lootCoverage: Plugin.SainVeryAggressiveLootCoverage.Value,
            scavengeSweepRadius: Plugin.SainVeryAggressiveScavengeSweepRadius.Value,
            splinterSearchRadius: Plugin.SainVeryAggressiveSplinterSearchRadius.Value,
            miniLootValueThreshold: Plugin.SainVeryAggressiveMiniLootThreshold.Value,
            lockedDoorUnlockProba: Plugin.SainVeryAggressiveLockedDoorProba.Value,
            sprintPropensity: Plugin.SainVeryAggressiveSprintPropensity.Value),
        // Average uses globally-tuned values routed through the same
        // bundle so call sites can read uniformly. Turning the master
        // toggle OFF has the same effect as resolving Average for every
        // PMC.
        _ => new ArchetypeTable(
            mainMixQ: Plugin.SainAverageMainMixQ.Value,
            mainMixK: Plugin.SainAverageMainMixK.Value,
            mainMixL: Plugin.SainAverageMainMixL.Value,
            mainCount: Plugin.SainAverageMainCount.Value,
            killsRoamDuration: Plugin.SainAverageKillsRoamDuration.Value,
            topLootCellsMax: Plugin.SainAverageTopLootCellsMax.Value,
            extractLootThreshold: Plugin.SainAverageExtractThreshold.Value,
            lootCoverage: Plugin.SainAverageLootCoverage.Value,
            scavengeSweepRadius: Plugin.SainAverageScavengeSweepRadius.Value,
            splinterSearchRadius: Plugin.SainAverageSplinterSearchRadius.Value,
            miniLootValueThreshold: Plugin.SainAverageMiniLootThreshold.Value,
            lockedDoorUnlockProba: Plugin.SainAverageLockedDoorProba.Value,
            sprintPropensity: Plugin.SainAverageSprintPropensity.Value),
    };

    private readonly struct ArchetypeTable
    {
        public readonly float MainMixQuest;
        public readonly float MainMixKills;
        public readonly float MainMixLootValue;
        public readonly Vector2 MainCount;
        public readonly Vector2 KillsRoamDuration;
        public readonly int TopLootCellsMax;
        public readonly Vector2 ExtractLootThreshold;
        public readonly Vector2 LootCoverage;
        public readonly float ScavengeSweepRadius;
        public readonly float SplinterSearchRadius;
        public readonly int MiniLootValueThreshold;
        public readonly float LockedDoorUnlockProba;
        public readonly float SprintPropensity;

        public ArchetypeTable(
            float mainMixQ, float mainMixK, float mainMixL,
            Vector2 mainCount,
            Vector2 killsRoamDuration,
            int topLootCellsMax,
            Vector2 extractLootThreshold,
            Vector2 lootCoverage,
            float scavengeSweepRadius,
            float splinterSearchRadius,
            int miniLootValueThreshold,
            float lockedDoorUnlockProba,
            float sprintPropensity)
        {
            MainMixQuest = mainMixQ;
            MainMixKills = mainMixK;
            MainMixLootValue = mainMixL;
            MainCount = mainCount;
            KillsRoamDuration = killsRoamDuration;
            TopLootCellsMax = topLootCellsMax;
            ExtractLootThreshold = extractLootThreshold;
            LootCoverage = lootCoverage;
            ScavengeSweepRadius = scavengeSweepRadius;
            SplinterSearchRadius = splinterSearchRadius;
            MiniLootValueThreshold = miniLootValueThreshold;
            LockedDoorUnlockProba = lockedDoorUnlockProba;
            SprintPropensity = sprintPropensity;
        }
    }
}
