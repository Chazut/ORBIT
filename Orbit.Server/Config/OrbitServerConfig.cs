namespace Orbit.Server.Config;

/// <summary>
/// Server-side ORBIT configuration. Source of truth for everything that drives bot behaviour --
/// the client fetches it at game start and again at every raid start, which also makes headless
/// setups work: the config applies wherever the bots actually run. Client-only concerns (log
/// levels, performance logging) stay in the BepInEx F12 config.
///
/// v2 scope: every behaviour section (factions, looting, player scav, main objectives, POI guard,
/// zones and SAIN personalities). Defaults must stay identical to the client's fallback defaults
/// in Orbit/Helpers/ServerConfig.cs -- a client without the server mod behaves exactly like a
/// fresh server config.
/// </summary>
public class OrbitServerConfig
{
    public int ConfigVersion { get; set; } = 2;
    public FactionsConfig Factions { get; set; } = new();
    public GeneralConfig General { get; set; } = new();
    public LootConfig Loot { get; set; } = new();
    public PlayerScavConfig PlayerScav { get; set; } = new();
    public MainObjectivesConfig MainObjectives { get; set; } = new();
    public PoiGuardConfig PoiGuard { get; set; } = new();
    public ZonesConfig Zones { get; set; } = new();
    public PersonalitiesConfig Personalities { get; set; } = new();
    public AiLimiterConfig AiLimiter { get; set; } = new();
}

/// <summary>
/// The built-in AI limiter: far-away ORBIT squads sleep body-first (the bot GameObject is disabled,
/// killing the per-bot BSG/SAIN cost) while ORBIT keeps thinking for them. OFF by default while the
/// feature gathers feedback. Not compatible with external limiters (AILimit) or Questing Bots
/// sleeping: two SetActive owners fight each other.
/// </summary>
public class AiLimiterConfig
{
    // RC ONLY: ON by default to gather feedback. Flip back to default-OFF for the stable release.
    public bool Enabled { get; set; } = true;
    public bool GhostMovement { get; set; } = true;
    // "simulated" (statistical resolution, zero cost) | "real" (wake both units on contact and let
    // the AI fight it out) | "off" (ghosts ignore each other).
    public string GhostFightsMode { get; set; } = "simulated";
    // Audible distant gunfire when a simulated fight resolves (the fighters' real weapon sounds).
    public bool GhostFightSounds { get; set; } = true;

    // Default-dormant bot types: every bot that is not a PMC or PlayerScav sleeps by default (tight
    // ring, no population-floor slot) when its type toggle is ON, whether ORBIT drives it (ghost
    // movement) or it is vanilla (frozen in place). OFF = ORBIT bots of that type use the standard
    // PMC-like rules, vanilla bots of that type are left untouched.
    public bool DormantScavs { get; set; } = true;
    public bool DormantGoons { get; set; } = true;
    public bool DormantBosses { get; set; } = true;
    public bool DormantCultists { get; set; } = true;
    public bool DormantRaiders { get; set; } = true;
    public bool DormantBloodhounds { get; set; } = true;
    public bool DormantOthers { get; set; } = true;
    // ON (default): no population floor at all, far from every human the whole map may sleep.
    // OFF: MinAwakeBots standard-policy bots stay awake as a permanent "real world" backdrop.
    public bool FullSleep { get; set; } = true;
    public int MinAwakeBots { get; set; } = 6;
    public float SleepDistance { get; set; } = 250f;
    public float WakeDistance { get; set; } = 200f;
    public float HostileWakeDistance { get; set; } = 75f;

    // ON (default): sleeping ORBIT squads keep looting along their routes. OFF: sleepers walk past
    // everything and the loot waits for the players.
    public bool GhostLooting { get; set; } = true;
    // "rare" | "normal" | "frequent": scales the ghost-fight contact odds and the per-pair cooldown.
    public string GhostFightFrequency { get; set; } = "normal";
    // 0.5 to 2.0: casualty multiplier for simulated fights (0.5 = often bloodless, 2 = bloodbaths).
    public float GhostFightLethality { get; set; } = 1f;
    // ON (default): aiming through a magnified optic stretches the wake ring forward, capped below.
    public bool ScopedWake { get; set; } = true;
    public float ScopedWakeMaxDistance { get; set; } = 800f;
}

/// <summary>
/// Mirrors the F12 "02. Factions" section: faction-mod takeover toggles (OFF = the faction mod's
/// own behaviour wins), vanilla-behaviour opt-outs (ON = ORBIT leaves those vanilla bots alone)
/// and the roaming toggles.
/// </summary>
public class FactionsConfig
{
    public bool TakeOverUntar { get; set; }
    public bool TakeOverRuaf { get; set; }
    public bool TakeOverBlackDivision { get; set; }
    public bool TakeOverIsb { get; set; } = true;
    public bool TakeOverCombineSoldiers { get; set; }

    public bool VanillaScavs { get; set; }
    public bool VanillaGoons { get; set; }
    public bool VanillaCultists { get; set; }
    public bool VanillaRaiders { get; set; } = true;
    public bool VanillaBloodhounds { get; set; }

    // Percentage of each faction's squads that roll permission to leave their spawn area and use
    // the map-wide waypoint fallback (rolled once per squad per raid). 0 = whole faction stays
    // local while still running ORBIT; 100 = unrestricted roaming. (Community PR #18 by Andrewgdewar.)
    public int ScavAreaRoamingPct { get; set; } = 20;
    public int GoonAreaRoamingPct { get; set; } = 100;
    public int BloodhoundAreaRoamingPct { get; set; } = 100;
}

/// <summary>General behaviour toggles (F12 "01. Essentials" gameplay entries).</summary>
public class GeneralConfig
{
    public bool SquadRally { get; set; } = true;
    public bool EmergencyExtractEnabled { get; set; } = true;
}

/// <summary>F12 "04. Looting". The faction flag enums are flattened into per-faction booleans.</summary>
public class LootConfig
{
    public bool LootPmc { get; set; } = true;
    public bool LootScav { get; set; } = true;
    public bool LootPlayerScav { get; set; } = true;
    public bool LootGoons { get; set; }

    public float DetectDistance { get; set; } = 80f;
    public bool CorpseRequiresSightOrSquadKill { get; set; } = true;
    public bool KeepSpawnWeapons { get; set; }

    public bool ExtractPmc { get; set; } = true;
    public bool ExtractPlayerScav { get; set; } = true;
    public int SoloLootExtractChancePct { get; set; } = 50;
    public int ScavLootChancePct { get; set; } = 30;
}

/// <summary>F12 "03. PlayerScav". PlayerScavs get no SAIN archetype, so these drive them directly.</summary>
public class PlayerScavConfig
{
    public bool Enabled { get; set; } = true;
    public int MainCountMin { get; set; } = 1;
    public int MainCountMax { get; set; } = 5;
    public float MainMixQuest { get; set; } = 0.10f;
    public float MainMixKills { get; set; } = 0.30f;
    public float MainMixLootValue { get; set; } = 0.60f;
    public float TimeExtractWindowMin { get; set; } = 10f;
    public float TimeExtractWindowMax { get; set; } = 30f;
    public float ExtractAtLootValue { get; set; } = 200_000f;
}

/// <summary>F12 "08. Main objectives" global knobs (per-archetype values live in Personalities).</summary>
public class MainObjectivesConfig
{
    public bool Enabled { get; set; } = true;
    public bool EnabledForPmc { get; set; } = true;
    public bool ExtractOnAllCompleted { get; set; } = true;
    public float AttractionMagnitude { get; set; } = 4.0f;
    public float KillsRoamForceMagnitude { get; set; } = 3.0f;
    public float LootValueTimeoutSeconds { get; set; } = 300f;
    public float CombatCallerGraceSeconds { get; set; } = 5f;
    public float RoamSplinterRadius { get; set; } = 50f;
    public float SameFloorLootYTolerance { get; set; } = 2.5f;
    public float CrossFloorSplinterChance { get; set; } = 0.1f;
    public float TimeExtractWindowMin { get; set; } = 10f;
    public float TimeExtractWindowMax { get; set; } = 30f;
    public float PmcLootCellCooldownSeconds { get; set; } = 600f;
    public float SyntheticVisitCooldownSeconds { get; set; } = 180f;
    public float OpportunisticCorpseScanIntervalSeconds { get; set; } = 2.5f;
}

/// <summary>F12 "06. POI guard": how long squads hold a spot before moving on.</summary>
public class PoiGuardConfig
{
    public float GuardDurationMin { get; set; } = 60f;
    public float GuardDurationMax { get; set; } = 180f;
    public float SyntheticGuardDurationMin { get; set; } = 3.5f;
    public float SyntheticGuardDurationMax { get; set; } = 6.5f;
    public float GuardDurationCutMin { get; set; } = 0.2f;
    public float GuardDurationCutMax { get; set; } = 0.5f;
}

/// <summary>F12 "07. Advection &amp; convergence" plus the convergence master toggle.</summary>
public class ZonesConfig
{
    public float ZoneRadiusScale { get; set; } = 1f;
    public float ZoneForceScale { get; set; } = 1f;
    public float ZoneFalloffScale { get; set; } = 1f;
    public bool ConvergenceEnabled { get; set; }
    public float ConvergenceRadiusScale { get; set; } = 1f;
    public float ConvergenceForceScale { get; set; } = 1f;
}

/// <summary>
/// F12 "09.x SAIN personality". Brain-name lists are read once at game start on the client, so
/// changing them still needs a game restart; the numeric archetype values apply on the next raid.
/// </summary>
public class PersonalitiesConfig
{
    public bool Enabled { get; set; } = true;
    public bool TimmyExtrasEnabled { get; set; } = true;

    public string BrainsTimmy { get; set; } = "Timmy";
    public string BrainsCautious { get; set; } = "Rat, Coward, SnappingTurtle";
    public string BrainsAverage { get; set; } = "Normal";
    public string BrainsAggressive { get; set; } = "Wreckless, Chad";
    public string BrainsVeryAggressive { get; set; } = "GigaChad";

    public ArchetypeConfig Timmy { get; set; } = new()
    {
        MainMixQuest = 0.29f, MainMixKills = 0.29f, MainMixLootValue = 0.42f,
        MainCountMin = 1, MainCountMax = 2,
        ExtractThresholdMin = 100_000f, ExtractThresholdMax = 300_000f,
        LootCoverageMin = 0.30f, LootCoverageMax = 0.50f,
        SprintPropensity = 0.0f, LockedDoorChance = 0.10f, MiniLootThreshold = 0,
        ScavengeSweepRadius = 10f, SplinterSearchRadius = 30f,
        KillsRoamDurationMin = 30f, KillsRoamDurationMax = 150f, TopLootCellsMax = 10,
    };

    public ArchetypeConfig Cautious { get; set; } = new()
    {
        MainMixQuest = 0.23f, MainMixKills = 0.06f, MainMixLootValue = 0.71f,
        MainCountMin = 2, MainCountMax = 4,
        ExtractThresholdMin = 200_000f, ExtractThresholdMax = 500_000f,
        LootCoverageMin = 0.85f, LootCoverageMax = 0.95f,
        SprintPropensity = 0.2f, LockedDoorChance = 0.10f, MiniLootThreshold = 5000,
        ScavengeSweepRadius = 15f, SplinterSearchRadius = 18f,
        KillsRoamDurationMin = 30f, KillsRoamDurationMax = 150f, TopLootCellsMax = 10,
    };

    public ArchetypeConfig Average { get; set; } = new()
    {
        MainMixQuest = 0.34f, MainMixKills = 0.33f, MainMixLootValue = 0.33f,
        MainCountMin = 1, MainCountMax = 5,
        ExtractThresholdMin = 500_000f, ExtractThresholdMax = 1_000_000f,
        LootCoverageMin = 0.65f, LootCoverageMax = 0.75f,
        SprintPropensity = 0.5f, LockedDoorChance = 0.30f, MiniLootThreshold = 10000,
        ScavengeSweepRadius = 10f, SplinterSearchRadius = 30f,
        KillsRoamDurationMin = 60f, KillsRoamDurationMax = 300f, TopLootCellsMax = 10,
    };

    public ArchetypeConfig Aggressive { get; set; } = new()
    {
        MainMixQuest = 0.18f, MainMixKills = 0.64f, MainMixLootValue = 0.18f,
        MainCountMin = 2, MainCountMax = 4,
        ExtractThresholdMin = 1_000_000f, ExtractThresholdMax = 1_500_000f,
        LootCoverageMin = 0.50f, LootCoverageMax = 0.60f,
        SprintPropensity = 0.8f, LockedDoorChance = 0.45f, MiniLootThreshold = 15000,
        ScavengeSweepRadius = 8f, SplinterSearchRadius = 39f,
        KillsRoamDurationMin = 90f, KillsRoamDurationMax = 450f, TopLootCellsMax = 5,
    };

    public ArchetypeConfig VeryAggressive { get; set; } = new()
    {
        MainMixQuest = 0.06f, MainMixKills = 0.83f, MainMixLootValue = 0.11f,
        MainCountMin = 2, MainCountMax = 5,
        ExtractThresholdMin = 1_500_000f, ExtractThresholdMax = 3_000_000f,
        LootCoverageMin = 0.30f, LootCoverageMax = 0.45f,
        SprintPropensity = 1.0f, LockedDoorChance = 0.60f, MiniLootThreshold = 20000,
        ScavengeSweepRadius = 5f, SplinterSearchRadius = 45f,
        KillsRoamDurationMin = 150f, KillsRoamDurationMax = 750f, TopLootCellsMax = 3,
    };
}

/// <summary>The 13 per-archetype knobs (min/max pairs flattened from the old Vector2 entries).</summary>
public class ArchetypeConfig
{
    public float MainMixQuest { get; set; }
    public float MainMixKills { get; set; }
    public float MainMixLootValue { get; set; }
    public float MainCountMin { get; set; }
    public float MainCountMax { get; set; }
    public float ExtractThresholdMin { get; set; }
    public float ExtractThresholdMax { get; set; }
    public float LootCoverageMin { get; set; }
    public float LootCoverageMax { get; set; }
    public float SprintPropensity { get; set; }
    public float LockedDoorChance { get; set; }
    public int MiniLootThreshold { get; set; }
    public float ScavengeSweepRadius { get; set; }
    public float SplinterSearchRadius { get; set; }
    public float KillsRoamDurationMin { get; set; }
    public float KillsRoamDurationMax { get; set; }
    public int TopLootCellsMax { get; set; }
}
