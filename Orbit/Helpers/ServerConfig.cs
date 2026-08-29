using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbit.Config;
using SPT.Common.Http;
using UnityEngine;

namespace Orbit.Helpers;

/// <summary>
/// Client-side mirror of the ORBIT server mod's config (served at /orbit/config, edited via the
/// server web UI at /orbit). Fetched once at plugin boot and again at every raid start, so a Save
/// in the web UI applies on the next raid without restarting the game. Every default here MUST
/// stay identical to Orbit.Server's OrbitServerConfig defaults -- when the server mod isn't
/// installed the fetch fails and these fallbacks reproduce the historical F12 defaults exactly.
/// Vector2 / flag-enum reads used by the rest of the codebase are exposed as computed properties
/// over the flattened min/max + boolean wire fields.
/// </summary>
public static class ServerConfig
{
    public sealed class FactionsSection
    {
        [JsonProperty("take_over_untar")] public bool TakeOverUntar;
        [JsonProperty("take_over_ruaf")] public bool TakeOverRuaf;
        [JsonProperty("take_over_black_division")] public bool TakeOverBlackDivision;
        [JsonProperty("take_over_isb")] public bool TakeOverIsb = true;
        [JsonProperty("take_over_combine_soldiers")] public bool TakeOverCombineSoldiers;

        [JsonProperty("vanilla_scavs")] public bool VanillaScavs;
        [JsonProperty("vanilla_goons")] public bool VanillaGoons;
        [JsonProperty("vanilla_cultists")] public bool VanillaCultists;
        [JsonProperty("vanilla_raiders")] public bool VanillaRaiders = true;
        [JsonProperty("vanilla_bloodhounds")] public bool VanillaBloodhounds;

        [JsonProperty("scav_area_roaming_pct")] public int ScavAreaRoamingPct = 20;
        [JsonProperty("goon_area_roaming_pct")] public int GoonAreaRoamingPct = 100;
        [JsonProperty("bloodhound_area_roaming_pct")] public int BloodhoundAreaRoamingPct = 100;
    }

    public sealed class GeneralSection
    {
        [JsonProperty("squad_rally")] public bool SquadRally = true;
        [JsonProperty("emergency_extract_enabled")] public bool EmergencyExtractEnabled = true;
    }

    public sealed class LootSection
    {
        [JsonProperty("loot_pmc")] public bool LootPmc = true;
        [JsonProperty("loot_scav")] public bool LootScav = true;
        [JsonProperty("loot_player_scav")] public bool LootPlayerScav = true;
        [JsonProperty("loot_goons")] public bool LootGoons;

        [JsonProperty("detect_distance")] public float DetectDistance = 80f;
        [JsonProperty("corpse_requires_sight_or_squad_kill")] public bool CorpseRequiresSightOrSquadKill = true;
        [JsonProperty("keep_spawn_weapons")] public bool KeepSpawnWeapons;

        [JsonProperty("extract_pmc")] public bool ExtractPmc = true;
        [JsonProperty("extract_player_scav")] public bool ExtractPlayerScav = true;
        [JsonProperty("solo_loot_extract_chance_pct")] public int SoloLootExtractChancePct = 50;
        [JsonProperty("scav_loot_chance_pct")] public int ScavLootChancePct = 30;

        [JsonIgnore] public LootingFaction LootingEnabled =>
            (LootPmc ? LootingFaction.Pmc : LootingFaction.None)
            | (LootScav ? LootingFaction.Scav : LootingFaction.None)
            | (LootPlayerScav ? LootingFaction.PlayerScav : LootingFaction.None)
            | (LootGoons ? LootingFaction.Goon : LootingFaction.None);

        [JsonIgnore] public ExtractFaction ExtractAllowedFor =>
            (ExtractPmc ? ExtractFaction.Pmc : ExtractFaction.None)
            | (ExtractPlayerScav ? ExtractFaction.PlayerScav : ExtractFaction.None);
    }

    public sealed class PlayerScavSection
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("main_count_min")] public int MainCountMin = 1;
        [JsonProperty("main_count_max")] public int MainCountMax = 5;
        [JsonProperty("main_mix_quest")] public float MainMixQuest = 0.10f;
        [JsonProperty("main_mix_kills")] public float MainMixKills = 0.30f;
        [JsonProperty("main_mix_loot_value")] public float MainMixLootValue = 0.60f;
        [JsonProperty("time_extract_window_min")] public float TimeExtractWindowMin = 10f;
        [JsonProperty("time_extract_window_max")] public float TimeExtractWindowMax = 30f;
        [JsonProperty("extract_at_loot_value")] public float ExtractAtLootValue = 200_000f;

        [JsonIgnore] public Vector2 TimeExtractWindow => new Vector2(TimeExtractWindowMin, TimeExtractWindowMax);
    }

    public sealed class MainObjectivesSection
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("enabled_for_pmc")] public bool EnabledForPmc = true;
        [JsonProperty("extract_on_all_completed")] public bool ExtractOnAllCompleted = true;
        [JsonProperty("attraction_magnitude")] public float AttractionMagnitude = 4.0f;
        [JsonProperty("kills_roam_force_magnitude")] public float KillsRoamForceMagnitude = 3.0f;
        [JsonProperty("loot_value_timeout_seconds")] public float LootValueTimeoutSeconds = 300f;
        [JsonProperty("combat_caller_grace_seconds")] public float CombatCallerGraceSeconds = 5f;
        [JsonProperty("roam_splinter_radius")] public float RoamSplinterRadius = 50f;
        [JsonProperty("same_floor_loot_y_tolerance")] public float SameFloorLootYTolerance = 2.5f;
        [JsonProperty("cross_floor_splinter_chance")] public float CrossFloorSplinterChance = 0.1f;
        [JsonProperty("time_extract_window_min")] public float TimeExtractWindowMin = 10f;
        [JsonProperty("time_extract_window_max")] public float TimeExtractWindowMax = 30f;
        [JsonProperty("pmc_loot_cell_cooldown_seconds")] public float PmcLootCellCooldownSeconds = 600f;
        [JsonProperty("synthetic_visit_cooldown_seconds")] public float SyntheticVisitCooldownSeconds = 180f;
        [JsonProperty("opportunistic_corpse_scan_interval_seconds")] public float OpportunisticCorpseScanIntervalSeconds = 2.5f;

        [JsonIgnore] public Vector2 TimeExtractWindow => new Vector2(TimeExtractWindowMin, TimeExtractWindowMax);
    }

    public sealed class PoiGuardSection
    {
        [JsonProperty("guard_duration_min")] public float GuardDurationMin = 60f;
        [JsonProperty("guard_duration_max")] public float GuardDurationMax = 180f;
        [JsonProperty("synthetic_guard_duration_min")] public float SyntheticGuardDurationMin = 3.5f;
        [JsonProperty("synthetic_guard_duration_max")] public float SyntheticGuardDurationMax = 6.5f;
        [JsonProperty("guard_duration_cut_min")] public float GuardDurationCutMin = 0.2f;
        [JsonProperty("guard_duration_cut_max")] public float GuardDurationCutMax = 0.5f;

        [JsonIgnore] public Vector2 GuardDuration => new Vector2(GuardDurationMin, GuardDurationMax);
        [JsonIgnore] public Vector2 SyntheticGuardDuration => new Vector2(SyntheticGuardDurationMin, SyntheticGuardDurationMax);
        [JsonIgnore] public Vector2 GuardDurationCut => new Vector2(GuardDurationCutMin, GuardDurationCutMax);
    }

    public sealed class AiLimiterSection
    {
        // RC ONLY: ON by default (must match the server default). Flip back for the stable release.
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("ghost_movement")] public bool GhostMovement = true;
        [JsonProperty("ghost_fights_mode")] public string GhostFightsMode = "simulated";
        [JsonProperty("ghost_fight_sounds")] public bool GhostFightSounds = true;
        [JsonProperty("ghost_looting")] public bool GhostLooting = true;
        [JsonProperty("ghost_fight_frequency")] public string GhostFightFrequency = "normal";
        [JsonProperty("ghost_fight_lethality")] public float GhostFightLethality = 1f;

        [JsonProperty("dormant_scavs")] public bool DormantScavs = true;
        [JsonProperty("dormant_goons")] public bool DormantGoons = true;
        [JsonProperty("dormant_bosses")] public bool DormantBosses = true;
        [JsonProperty("dormant_cultists")] public bool DormantCultists = true;
        [JsonProperty("dormant_raiders")] public bool DormantRaiders = true;
        [JsonProperty("dormant_bloodhounds")] public bool DormantBloodhounds = true;
        [JsonProperty("dormant_others")] public bool DormantOthers = true;
        [JsonProperty("full_sleep")] public bool FullSleep = true;
        [JsonProperty("min_awake_bots")] public int MinAwakeBots = 6;
        [JsonProperty("sleep_distance")] public float SleepDistance = 250f;
        [JsonProperty("wake_distance")] public float WakeDistance = 200f;
        [JsonProperty("hostile_wake_distance")] public float HostileWakeDistance = 75f;
        [JsonProperty("scoped_wake")] public bool ScopedWake = true;
        [JsonProperty("scoped_wake_max_distance")] public float ScopedWakeMaxDistance = 800f;
    }

    public sealed class ZonesSection
    {
        [JsonProperty("zone_radius_scale")] public float ZoneRadiusScale = 1f;
        [JsonProperty("zone_force_scale")] public float ZoneForceScale = 1f;
        [JsonProperty("zone_falloff_scale")] public float ZoneFalloffScale = 1f;
        [JsonProperty("convergence_enabled")] public bool ConvergenceEnabled;
        [JsonProperty("convergence_radius_scale")] public float ConvergenceRadiusScale = 1f;
        [JsonProperty("convergence_force_scale")] public float ConvergenceForceScale = 1f;
    }

    public sealed class ArchetypeSection
    {
        [JsonProperty("main_mix_quest")] public float MainMixQ;
        [JsonProperty("main_mix_kills")] public float MainMixK;
        [JsonProperty("main_mix_loot_value")] public float MainMixL;
        [JsonProperty("main_count_min")] public float MainCountMin;
        [JsonProperty("main_count_max")] public float MainCountMax;
        [JsonProperty("extract_threshold_min")] public float ExtractThresholdMin;
        [JsonProperty("extract_threshold_max")] public float ExtractThresholdMax;
        [JsonProperty("loot_coverage_min")] public float LootCoverageMin;
        [JsonProperty("loot_coverage_max")] public float LootCoverageMax;
        [JsonProperty("sprint_propensity")] public float SprintPropensity;
        [JsonProperty("locked_door_chance")] public float LockedDoorProba;
        [JsonProperty("mini_loot_threshold")] public int MiniLootThreshold;
        [JsonProperty("scavenge_sweep_radius")] public float ScavengeSweepRadius;
        [JsonProperty("splinter_search_radius")] public float SplinterSearchRadius;
        [JsonProperty("kills_roam_duration_min")] public float KillsRoamDurationMin;
        [JsonProperty("kills_roam_duration_max")] public float KillsRoamDurationMax;
        [JsonProperty("top_loot_cells_max")] public int TopLootCellsMax;

        [JsonIgnore] public Vector2 MainCount => new Vector2(MainCountMin, MainCountMax);
        [JsonIgnore] public Vector2 ExtractThreshold => new Vector2(ExtractThresholdMin, ExtractThresholdMax);
        [JsonIgnore] public Vector2 LootCoverage => new Vector2(LootCoverageMin, LootCoverageMax);
        [JsonIgnore] public Vector2 KillsRoamDuration => new Vector2(KillsRoamDurationMin, KillsRoamDurationMax);
    }

    public sealed class PersonalitiesSection
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("timmy_extras_enabled")] public bool TimmyExtrasEnabled = true;

        [JsonProperty("brains_timmy")] public string BrainsTimmy = "Timmy";
        [JsonProperty("brains_cautious")] public string BrainsCautious = "Rat, Coward, SnappingTurtle";
        [JsonProperty("brains_average")] public string BrainsAverage = "Normal";
        [JsonProperty("brains_aggressive")] public string BrainsAggressive = "Wreckless, Chad";
        [JsonProperty("brains_very_aggressive")] public string BrainsVeryAggressive = "GigaChad";

        [JsonProperty("timmy")] public ArchetypeSection Timmy = new ArchetypeSection
        {
            MainMixQ = 0.29f, MainMixK = 0.29f, MainMixL = 0.42f,
            MainCountMin = 1, MainCountMax = 2,
            ExtractThresholdMin = 100_000f, ExtractThresholdMax = 300_000f,
            LootCoverageMin = 0.30f, LootCoverageMax = 0.50f,
            SprintPropensity = 0.0f, LockedDoorProba = 0.10f, MiniLootThreshold = 0,
            ScavengeSweepRadius = 10f, SplinterSearchRadius = 30f,
            KillsRoamDurationMin = 30f, KillsRoamDurationMax = 150f, TopLootCellsMax = 10,
        };

        [JsonProperty("cautious")] public ArchetypeSection Cautious = new ArchetypeSection
        {
            MainMixQ = 0.23f, MainMixK = 0.06f, MainMixL = 0.71f,
            MainCountMin = 2, MainCountMax = 4,
            ExtractThresholdMin = 200_000f, ExtractThresholdMax = 500_000f,
            LootCoverageMin = 0.85f, LootCoverageMax = 0.95f,
            SprintPropensity = 0.2f, LockedDoorProba = 0.10f, MiniLootThreshold = 5000,
            ScavengeSweepRadius = 15f, SplinterSearchRadius = 18f,
            KillsRoamDurationMin = 30f, KillsRoamDurationMax = 150f, TopLootCellsMax = 10,
        };

        [JsonProperty("average")] public ArchetypeSection Average = new ArchetypeSection
        {
            MainMixQ = 0.34f, MainMixK = 0.33f, MainMixL = 0.33f,
            MainCountMin = 1, MainCountMax = 5,
            ExtractThresholdMin = 500_000f, ExtractThresholdMax = 1_000_000f,
            LootCoverageMin = 0.65f, LootCoverageMax = 0.75f,
            SprintPropensity = 0.5f, LockedDoorProba = 0.30f, MiniLootThreshold = 10000,
            ScavengeSweepRadius = 10f, SplinterSearchRadius = 30f,
            KillsRoamDurationMin = 60f, KillsRoamDurationMax = 300f, TopLootCellsMax = 10,
        };

        [JsonProperty("aggressive")] public ArchetypeSection Aggressive = new ArchetypeSection
        {
            MainMixQ = 0.18f, MainMixK = 0.64f, MainMixL = 0.18f,
            MainCountMin = 2, MainCountMax = 4,
            ExtractThresholdMin = 1_000_000f, ExtractThresholdMax = 1_500_000f,
            LootCoverageMin = 0.50f, LootCoverageMax = 0.60f,
            SprintPropensity = 0.8f, LockedDoorProba = 0.45f, MiniLootThreshold = 15000,
            ScavengeSweepRadius = 8f, SplinterSearchRadius = 39f,
            KillsRoamDurationMin = 90f, KillsRoamDurationMax = 450f, TopLootCellsMax = 5,
        };

        [JsonProperty("very_aggressive")] public ArchetypeSection VeryAggressive = new ArchetypeSection
        {
            MainMixQ = 0.06f, MainMixK = 0.83f, MainMixL = 0.11f,
            MainCountMin = 2, MainCountMax = 5,
            ExtractThresholdMin = 1_500_000f, ExtractThresholdMax = 3_000_000f,
            LootCoverageMin = 0.30f, LootCoverageMax = 0.45f,
            SprintPropensity = 1.0f, LockedDoorProba = 0.60f, MiniLootThreshold = 20000,
            ScavengeSweepRadius = 5f, SplinterSearchRadius = 45f,
            KillsRoamDurationMin = 150f, KillsRoamDurationMax = 750f, TopLootCellsMax = 3,
        };
    }

    private sealed class Root
    {
        [JsonProperty("config_version")] public int ConfigVersion;
        [JsonProperty("factions")] public FactionsSection Factions = new FactionsSection();
        [JsonProperty("general")] public GeneralSection General = new GeneralSection();
        [JsonProperty("loot")] public LootSection Loot = new LootSection();
        [JsonProperty("player_scav")] public PlayerScavSection PlayerScav = new PlayerScavSection();
        [JsonProperty("main_objectives")] public MainObjectivesSection MainObjectives = new MainObjectivesSection();
        [JsonProperty("poi_guard")] public PoiGuardSection PoiGuard = new PoiGuardSection();
        [JsonProperty("zones")] public ZonesSection Zones = new ZonesSection();
        [JsonProperty("personalities")] public PersonalitiesSection Personalities = new PersonalitiesSection();
        [JsonProperty("ai_limiter")] public AiLimiterSection AiLimiter = new AiLimiterSection();
    }

    public static FactionsSection Factions { get; private set; } = new FactionsSection();
    public static GeneralSection General { get; private set; } = new GeneralSection();
    public static LootSection Loot { get; private set; } = new LootSection();
    public static PlayerScavSection PlayerScav { get; private set; } = new PlayerScavSection();
    public static MainObjectivesSection MainObjectives { get; private set; } = new MainObjectivesSection();
    public static PoiGuardSection PoiGuard { get; private set; } = new PoiGuardSection();
    public static ZonesSection Zones { get; private set; } = new ZonesSection();
    public static PersonalitiesSection Personalities { get; private set; } = new PersonalitiesSection();
    public static AiLimiterSection AiLimiter { get; private set; } = new AiLimiterSection();
    public static bool Fetched { get; private set; }

    /// <summary>
    /// Synchronous fetch from the SPT backend. Called at plugin boot and at every raid start
    /// (OrbitInitPatch), so web-UI edits apply on the next raid. On any failure the current
    /// values are kept: defaults on the first call, the last good fetch afterwards.
    /// </summary>
    public static void Fetch()
    {
        try
        {
            var json = RequestHandler.GetJson("/orbit/config");
            var root = JsonConvert.DeserializeObject<Root>(json);
            if (root != null)
            {
                Factions = root.Factions ?? Factions;
                General = root.General ?? General;
                Loot = root.Loot ?? Loot;
                PlayerScav = root.PlayerScav ?? PlayerScav;
                MainObjectives = root.MainObjectives ?? MainObjectives;
                PoiGuard = root.PoiGuard ?? PoiGuard;
                Zones = root.Zones ?? Zones;
                Personalities = root.Personalities ?? Personalities;
                AiLimiter = root.AiLimiter ?? AiLimiter;
                if (!Fetched)
                    Log.Always($"Server config fetched (v{root.ConfigVersion}) - settings applied from the server web UI (/orbit)");
                Fetched = true;
                FetchZones();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Server config fetch failed: {ex.Message}");
        }

        if (!Fetched)
            Log.Warning("ORBIT server mod not reachable (/orbit/config) - using default settings. Install the ORBIT server mod to configure ORBIT from the server web UI (required for headless setups).");
    }

    // ── Per-map zone overrides (server web UI zone editor) ──────────────

    /// <summary>Raw zone JSON per map id, fetched from /orbit/zones alongside the config. Null when the
    /// server mod is unreachable or predates the zone editor — the local Config/Maps/Zones files then
    /// stay authoritative.</summary>
    private static Dictionary<string, string> _zoneOverrides;
    private static bool _zonesLogged;

    /// <summary>Called from Fetch(); separate try so a zones failure never blocks the config fetch.</summary>
    private static void FetchZones()
    {
        try
        {
            var json = RequestHandler.GetJson("/orbit/zones");
            if (string.IsNullOrEmpty(json)) return;
            var root = JObject.Parse(json);
            var dict = new Dictionary<string, string>();
            foreach (var prop in root.Properties())
                dict[prop.Name] = prop.Value.ToString();
            if (dict.Count == 0) return;
            _zoneOverrides = dict;
            if (!_zonesLogged)
                Log.Always($"Server zones fetched ({dict.Count} maps) - hotspots applied from the server web UI zone editor");
            _zonesLogged = true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Server zones fetch failed (local zone files stay authoritative): {ex.Message}");
        }
    }

    /// <summary>Deserialized zone override for a map, or false when the server has none.</summary>
    public static bool TryGetZoneOverride(string mapId, out WaypointConfig.MapZone zones)
    {
        zones = null;
        if (_zoneOverrides == null || string.IsNullOrEmpty(mapId)) return false;
        if (!_zoneOverrides.TryGetValue(mapId, out var json)) return false;
        try
        {
            zones = JsonConvert.DeserializeObject<WaypointConfig.MapZone>(json);
            return zones != null;
        }
        catch (Exception ex)
        {
            Log.Warning($"Server zone override for {mapId} failed to parse - local file kept: {ex.Message}");
            return false;
        }
    }
}
