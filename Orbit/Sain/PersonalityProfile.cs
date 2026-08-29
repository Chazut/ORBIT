using UnityEngine;
using Random = UnityEngine.Random;
using Orbit.Helpers;

namespace Orbit.Sain;

/// <summary>
/// Per-squad bundle of values rolled once at squad creation from the leader's archetype tables. PMC squads
/// with SAIN personality resolution active read these; scavs / PlayerScavs ignore the bundle entirely and use
/// the global knobs. Vector2 source ranges are rolled to single floats (or ints) here so dispatch reads are
/// cheap and behaviour is deterministic across the raid.
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

    // Per-member (stored on the squad — every member reads the squad's profile rather than rolling per-Agent
    // for cost reasons).
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
    /// Roll just the extract-loot threshold for the given archetype. Used by per-agent threshold resolution:
    /// each squad member rolls their own threshold based on their own SAIN brain so a mixed squad of Rat +
    /// Chad sums to Rat-range + Chad-range, not 2× one.
    /// </summary>
    public static float RollExtractThresholdFor(PersonalityArchetype archetype)
    {
        var range = ResolveTable(archetype).ExtractLootThreshold;
        return Random.Range(range.x, range.y);
    }

    /// <summary>
    /// Returns the configured per-archetype mini-loot value threshold. Scalar — no per-roll sampling.
    /// </summary>
    public static int GetMiniLootThresholdFor(PersonalityArchetype archetype)
    {
        return ResolveTable(archetype).MiniLootValueThreshold;
    }

    /// <summary>
    /// Roll a profile for the given archetype using the F12 tables. Each Vector2 range is sampled once;
    /// scalars are passed through. Called at squad registration when the leader is PMC and SAIN personality
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
            mainMixQ: ServerConfig.Personalities.Timmy.MainMixQ,
            mainMixK: ServerConfig.Personalities.Timmy.MainMixK,
            mainMixL: ServerConfig.Personalities.Timmy.MainMixL,
            mainCount: ServerConfig.Personalities.Timmy.MainCount,
            killsRoamDuration: ServerConfig.Personalities.Timmy.KillsRoamDuration,
            topLootCellsMax: ServerConfig.Personalities.Timmy.TopLootCellsMax,
            extractLootThreshold: ServerConfig.Personalities.Timmy.ExtractThreshold,
            lootCoverage: ServerConfig.Personalities.Timmy.LootCoverage,
            scavengeSweepRadius: ServerConfig.Personalities.Timmy.ScavengeSweepRadius,
            splinterSearchRadius: ServerConfig.Personalities.Timmy.SplinterSearchRadius,
            miniLootValueThreshold: ServerConfig.Personalities.Timmy.MiniLootThreshold,
            lockedDoorUnlockProba: ServerConfig.Personalities.Timmy.LockedDoorProba,
            sprintPropensity: ServerConfig.Personalities.Timmy.SprintPropensity),
        PersonalityArchetype.Cautious => new ArchetypeTable(
            mainMixQ: ServerConfig.Personalities.Cautious.MainMixQ,
            mainMixK: ServerConfig.Personalities.Cautious.MainMixK,
            mainMixL: ServerConfig.Personalities.Cautious.MainMixL,
            mainCount: ServerConfig.Personalities.Cautious.MainCount,
            killsRoamDuration: ServerConfig.Personalities.Cautious.KillsRoamDuration,
            topLootCellsMax: ServerConfig.Personalities.Cautious.TopLootCellsMax,
            extractLootThreshold: ServerConfig.Personalities.Cautious.ExtractThreshold,
            lootCoverage: ServerConfig.Personalities.Cautious.LootCoverage,
            scavengeSweepRadius: ServerConfig.Personalities.Cautious.ScavengeSweepRadius,
            splinterSearchRadius: ServerConfig.Personalities.Cautious.SplinterSearchRadius,
            miniLootValueThreshold: ServerConfig.Personalities.Cautious.MiniLootThreshold,
            lockedDoorUnlockProba: ServerConfig.Personalities.Cautious.LockedDoorProba,
            sprintPropensity: ServerConfig.Personalities.Cautious.SprintPropensity),
        PersonalityArchetype.Aggressive => new ArchetypeTable(
            mainMixQ: ServerConfig.Personalities.Aggressive.MainMixQ,
            mainMixK: ServerConfig.Personalities.Aggressive.MainMixK,
            mainMixL: ServerConfig.Personalities.Aggressive.MainMixL,
            mainCount: ServerConfig.Personalities.Aggressive.MainCount,
            killsRoamDuration: ServerConfig.Personalities.Aggressive.KillsRoamDuration,
            topLootCellsMax: ServerConfig.Personalities.Aggressive.TopLootCellsMax,
            extractLootThreshold: ServerConfig.Personalities.Aggressive.ExtractThreshold,
            lootCoverage: ServerConfig.Personalities.Aggressive.LootCoverage,
            scavengeSweepRadius: ServerConfig.Personalities.Aggressive.ScavengeSweepRadius,
            splinterSearchRadius: ServerConfig.Personalities.Aggressive.SplinterSearchRadius,
            miniLootValueThreshold: ServerConfig.Personalities.Aggressive.MiniLootThreshold,
            lockedDoorUnlockProba: ServerConfig.Personalities.Aggressive.LockedDoorProba,
            sprintPropensity: ServerConfig.Personalities.Aggressive.SprintPropensity),
        PersonalityArchetype.VeryAggressive => new ArchetypeTable(
            mainMixQ: ServerConfig.Personalities.VeryAggressive.MainMixQ,
            mainMixK: ServerConfig.Personalities.VeryAggressive.MainMixK,
            mainMixL: ServerConfig.Personalities.VeryAggressive.MainMixL,
            mainCount: ServerConfig.Personalities.VeryAggressive.MainCount,
            killsRoamDuration: ServerConfig.Personalities.VeryAggressive.KillsRoamDuration,
            topLootCellsMax: ServerConfig.Personalities.VeryAggressive.TopLootCellsMax,
            extractLootThreshold: ServerConfig.Personalities.VeryAggressive.ExtractThreshold,
            lootCoverage: ServerConfig.Personalities.VeryAggressive.LootCoverage,
            scavengeSweepRadius: ServerConfig.Personalities.VeryAggressive.ScavengeSweepRadius,
            splinterSearchRadius: ServerConfig.Personalities.VeryAggressive.SplinterSearchRadius,
            miniLootValueThreshold: ServerConfig.Personalities.VeryAggressive.MiniLootThreshold,
            lockedDoorUnlockProba: ServerConfig.Personalities.VeryAggressive.LockedDoorProba,
            sprintPropensity: ServerConfig.Personalities.VeryAggressive.SprintPropensity),
        // Average uses globally-tuned values routed through the same bundle so call sites can read uniformly.
        // Turning the master toggle OFF has the same effect as resolving Average for every PMC.
        _ => new ArchetypeTable(
            mainMixQ: ServerConfig.Personalities.Average.MainMixQ,
            mainMixK: ServerConfig.Personalities.Average.MainMixK,
            mainMixL: ServerConfig.Personalities.Average.MainMixL,
            mainCount: ServerConfig.Personalities.Average.MainCount,
            killsRoamDuration: ServerConfig.Personalities.Average.KillsRoamDuration,
            topLootCellsMax: ServerConfig.Personalities.Average.TopLootCellsMax,
            extractLootThreshold: ServerConfig.Personalities.Average.ExtractThreshold,
            lootCoverage: ServerConfig.Personalities.Average.LootCoverage,
            scavengeSweepRadius: ServerConfig.Personalities.Average.ScavengeSweepRadius,
            splinterSearchRadius: ServerConfig.Personalities.Average.SplinterSearchRadius,
            miniLootValueThreshold: ServerConfig.Personalities.Average.MiniLootThreshold,
            lockedDoorUnlockProba: ServerConfig.Personalities.Average.LockedDoorProba,
            sprintPropensity: ServerConfig.Personalities.Average.SprintPropensity),
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
