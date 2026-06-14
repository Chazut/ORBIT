using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Orbit.Brain;
using Orbit.Config;
using Orbit.Core;
using Orbit.Interop;
using Orbit.Looting;
using Orbit.Patches;
using Orbit.UI;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit;

/// <summary>
/// BepInEx entry point. Owns the F12 configuration surface for every tunable knob the dispatcher / looting /
/// SAIN integration reads at runtime. <see cref="LogSource"/> exposes the BepInEx ManualLogSource consumed by
/// <see cref="Log"/> — named <c>LogSource</c> rather than
/// <c>Log</c> so the static <see cref="Orbit.Log"/> helper class doesn't
/// collide.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, OrbitVersion)]
[BepInDependency("xyz.drakia.bigbrain")]
[BepInDependency("xyz.drakia.waypoints")]
[BepInDependency("me.sol.sain")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.chazut.orbit";
    public const string PluginName = "ORBIT";
    public const string OrbitVersion = "1.1.0";

    public static ManualLogSource LogSource;

    // ╔══════════════════════════════════════════════════════════════╗
    // ║ F12 ConfigEntries                                              ║
    // ╚══════════════════════════════════════════════════════════════╝

    // 01. General
    public static ConfigEntry<bool> RoamingScavs;
    public static ConfigEntry<bool> RoamingGoons;
    public static ConfigEntry<bool> VanillaScavs;
    public static ConfigEntry<bool> VanillaGoons;
    public static ConfigEntry<bool> VanillaCultists;
    public static ConfigEntry<bool> VanillaRaiders;

    // 02. POI guard duration
    public static ConfigEntry<Vector2> ObjectiveGuardDuration;
    public static ConfigEntry<Vector2> ObjectiveAdjustedGuardDuration;
    public static ConfigEntry<Vector2> ObjectiveGuardDurationCut;

    // 03. Advection zones
    public static ConfigEntry<float> AdvectionZoneRadiusScale;
    public static ConfigEntry<float> AdvectionZoneForceScale;
    public static ConfigEntry<float> AdvectionZoneRadiusDecayScale;
    public static ConfigEntry<bool> ConvergenceEnabled;
    public static ConfigEntry<float> ConvergenceRadiusScale;
    public static ConfigEntry<float> ConvergenceForceScale;

    // 04. Main objectives — setup
    public static ConfigEntry<bool> MainObjectivesEnabled;
    public static ConfigEntry<bool> MainObjectivesEnabledForPmc;
    public static ConfigEntry<bool> MainObjectivesEnabledForPlayerScav;
    public static ConfigEntry<int> MainObjectivesCountMinPmc;
    public static ConfigEntry<int> MainObjectivesCountMaxPmc;
    public static ConfigEntry<int> MainObjectivesCountMinPlayerScav;
    public static ConfigEntry<int> MainObjectivesCountMaxPlayerScav;
    public static ConfigEntry<float> MainObjectivesPmcQuestWeight;
    public static ConfigEntry<float> MainObjectivesPmcKillsWeight;
    public static ConfigEntry<float> MainObjectivesPmcLootValueWeight;
    public static ConfigEntry<float> MainObjectivesPlayerScavQuestWeight;
    public static ConfigEntry<float> MainObjectivesPlayerScavKillsWeight;
    public static ConfigEntry<float> MainObjectivesPlayerScavLootValueWeight;
    public static ConfigEntry<int> MainObjectivesTopLootCellsMaxCount;

    // 05. Main objectives — runtime tuning
    public static ConfigEntry<Vector2> MainObjectivesKillsRoamDuration;
    public static ConfigEntry<float> MainObjectivesAttractionMagnitude;
    public static ConfigEntry<float> MainObjectivesKillsRoamForceMagnitude;
    public static ConfigEntry<float> MainObjectivesLootValueTimeoutSeconds;
    public static ConfigEntry<float> MainObjectivesCombatCallerGraceSeconds;
    public static ConfigEntry<float> MainObjectivesRoamSplinterRadius;
    public static ConfigEntry<float> MainObjectivesUnlockProbabilityIntermediate;
    public static ConfigEntry<float> LootCoveragePct;
    public static ConfigEntry<bool> MainObjectivesExtractOnAllCompleted;
    public static ConfigEntry<Vector2> TimeExtractWindowPmc;
    public static ConfigEntry<Vector2> TimeExtractWindowPlayerScav;
    public static ConfigEntry<float> PmcLootCellCooldownSeconds;
    public static ConfigEntry<float> SyntheticVisitCooldownSeconds;
    public static ConfigEntry<float> OpportunisticCorpseScanIntervalSeconds;
    public static ConfigEntry<float> SplinterSearchRadius;
    public static ConfigEntry<float> ScavengeSweepRadius;
    public static ConfigEntry<float> SameFloorLootYTolerance;
    public static ConfigEntry<float> CrossFloorSplinterChance;

    // 06. Faction-mod takeover (consumers wired in Phase 7)
    public static ConfigEntry<bool> HijackUntar;
    public static ConfigEntry<bool> HijackRuaf;
    public static ConfigEntry<bool> HijackBlackDivision;
    public static ConfigEntry<bool> HijackIsb;

    // 07.0 SAIN personality — general
    public static ConfigEntry<bool> SainPersonalityEnabled;
    public static ConfigEntry<string> SainArchetypeTimmyBrains;
    public static ConfigEntry<string> SainArchetypeCautiousBrains;
    public static ConfigEntry<string> SainArchetypeAverageBrains;
    public static ConfigEntry<string> SainArchetypeAggressiveBrains;
    public static ConfigEntry<string> SainArchetypeVeryAggressiveBrains;
    public static ConfigEntry<bool> SainTimmyExtrasEnabled;

    // 07.1 SAIN personality — Timmy
    public static ConfigEntry<float> SainTimmyMainMixQ;
    public static ConfigEntry<float> SainTimmyMainMixK;
    public static ConfigEntry<float> SainTimmyMainMixL;
    public static ConfigEntry<Vector2> SainTimmyMainCount;
    public static ConfigEntry<Vector2> SainTimmyExtractThreshold;
    public static ConfigEntry<Vector2> SainTimmyLootCoverage;
    public static ConfigEntry<float> SainTimmySprintPropensity;
    public static ConfigEntry<float> SainTimmyLockedDoorProba;
    public static ConfigEntry<int> SainTimmyMiniLootThreshold;
    public static ConfigEntry<float> SainTimmyScavengeSweepRadius;
    public static ConfigEntry<float> SainTimmySplinterSearchRadius;
    public static ConfigEntry<Vector2> SainTimmyKillsRoamDuration;
    public static ConfigEntry<int> SainTimmyTopLootCellsMax;

    // 07.2 SAIN personality — Cautious
    public static ConfigEntry<float> SainCautiousMainMixQ;
    public static ConfigEntry<float> SainCautiousMainMixK;
    public static ConfigEntry<float> SainCautiousMainMixL;
    public static ConfigEntry<Vector2> SainCautiousMainCount;
    public static ConfigEntry<Vector2> SainCautiousExtractThreshold;
    public static ConfigEntry<Vector2> SainCautiousLootCoverage;
    public static ConfigEntry<float> SainCautiousSprintPropensity;
    public static ConfigEntry<float> SainCautiousLockedDoorProba;
    public static ConfigEntry<int> SainCautiousMiniLootThreshold;
    public static ConfigEntry<float> SainCautiousScavengeSweepRadius;
    public static ConfigEntry<float> SainCautiousSplinterSearchRadius;
    public static ConfigEntry<Vector2> SainCautiousKillsRoamDuration;
    public static ConfigEntry<int> SainCautiousTopLootCellsMax;

    // 07.3 SAIN personality — Average
    public static ConfigEntry<float> SainAverageMainMixQ;
    public static ConfigEntry<float> SainAverageMainMixK;
    public static ConfigEntry<float> SainAverageMainMixL;
    public static ConfigEntry<Vector2> SainAverageMainCount;
    public static ConfigEntry<Vector2> SainAverageExtractThreshold;
    public static ConfigEntry<Vector2> SainAverageLootCoverage;
    public static ConfigEntry<float> SainAverageSprintPropensity;
    public static ConfigEntry<float> SainAverageLockedDoorProba;
    public static ConfigEntry<int> SainAverageMiniLootThreshold;
    public static ConfigEntry<float> SainAverageScavengeSweepRadius;
    public static ConfigEntry<float> SainAverageSplinterSearchRadius;
    public static ConfigEntry<Vector2> SainAverageKillsRoamDuration;
    public static ConfigEntry<int> SainAverageTopLootCellsMax;

    // 07.4 SAIN personality — Aggressive
    public static ConfigEntry<float> SainAggressiveMainMixQ;
    public static ConfigEntry<float> SainAggressiveMainMixK;
    public static ConfigEntry<float> SainAggressiveMainMixL;
    public static ConfigEntry<Vector2> SainAggressiveMainCount;
    public static ConfigEntry<Vector2> SainAggressiveExtractThreshold;
    public static ConfigEntry<Vector2> SainAggressiveLootCoverage;
    public static ConfigEntry<float> SainAggressiveSprintPropensity;
    public static ConfigEntry<float> SainAggressiveLockedDoorProba;
    public static ConfigEntry<int> SainAggressiveMiniLootThreshold;
    public static ConfigEntry<float> SainAggressiveScavengeSweepRadius;
    public static ConfigEntry<float> SainAggressiveSplinterSearchRadius;
    public static ConfigEntry<Vector2> SainAggressiveKillsRoamDuration;
    public static ConfigEntry<int> SainAggressiveTopLootCellsMax;

    // 07.5 SAIN personality — Very aggressive
    public static ConfigEntry<float> SainVeryAggressiveMainMixQ;
    public static ConfigEntry<float> SainVeryAggressiveMainMixK;
    public static ConfigEntry<float> SainVeryAggressiveMainMixL;
    public static ConfigEntry<Vector2> SainVeryAggressiveMainCount;
    public static ConfigEntry<Vector2> SainVeryAggressiveExtractThreshold;
    public static ConfigEntry<Vector2> SainVeryAggressiveLootCoverage;
    public static ConfigEntry<float> SainVeryAggressiveSprintPropensity;
    public static ConfigEntry<float> SainVeryAggressiveLockedDoorProba;
    public static ConfigEntry<int> SainVeryAggressiveMiniLootThreshold;
    public static ConfigEntry<float> SainVeryAggressiveScavengeSweepRadius;
    public static ConfigEntry<float> SainVeryAggressiveSplinterSearchRadius;
    public static ConfigEntry<Vector2> SainVeryAggressiveKillsRoamDuration;
    public static ConfigEntry<int> SainVeryAggressiveTopLootCellsMax;

    // Faction-mod plugin GUIDs — same Chainloader detection raid-review uses.
    private const string UntarPluginGuid = "com.untargh.tacticaltoaster";
    private const string RuafPluginGuid = "com.ruafcomehome.tacticaltoaster";
    private const string BlackDivPluginGuid = "com.blackdiv.tacticaltoaster";
    // ISB's BepInEx entry is ISBNotify.dll ("ISB SOF Notifier") — the spawn-patch + role-detection
    // core, present whenever ISB bots are. The companion ISBSpecialForcesPlugin.dll is a plain library
    // (no BepInPlugin), so this is the GUID to detect.
    private const string IsbPluginGuid = "samc137.ISBinfo";

    private void Awake()
    {
        LogSource = Logger;

        // The version-label patch must run BEFORE the delayed coroutine — EFT calls PreloaderUI.method_6
        // during early scene init, well before our 5s delay completes. SAIN registers it immediately at boot
        // for the same reason.
        EnableSafe(new VersionLabelPatch());

        StartCoroutine(DelayedLoad());
    }

    private IEnumerator DelayedLoad()
    {
        // Wait for the user's other 500 mods to settle before binding config and registering patches —
        // early-boot races against handlers other mods install in Awake are otherwise too easy to lose.
        yield return new WaitForSeconds(5);

        try
        {
            SetupConfig();
            LootConfig.Init(Config);
        }
        catch (Exception ex)
        {
            // Config binding errors must not crash boot — they'd block the entire plugin from registering.
            // Log and continue with defaults.
            Log.Error($"ORBIT config bind failed (sub-systems will degrade to defaults): {ex}");
        }

        // Item pricing: ItemPriceLookup prefers EFT's Singleton<HandbookClass> when present, else the
        // server-fetched price cache below. HandbookClass is built by the main-menu/profile flow, which a
        // FIKA headless client skips, so we can't depend on it (issue #5).
        StartCoroutine(WaitForHandbook());

        Log.Info($"ORBIT {OrbitVersion} initialised");

        // Patches — wrap each in EnableSafe so one bad patch (wrong Harmony parameter name, missing target
        // method after a game update) can't collapse the rest of init. Without the guard a single failure
        // skips every subsequent .Enable() AND the BrainManager.AddCustomLayer call, leaving bots stranded on
        // BSG's vanilla brain.
        EnableSafe(new OrbitInitPatch());
        EnableSafe(new OrbitTickPatch());
        EnableSafe(new OrbitDisposePatch());

        EnableSafe(new DoorCarverShrinkPatch());

        EnableSafe(new SoftTeleportTracePatch());
        EnableSafe(new HardTeleportTracePatch());
        EnableSafe(new MovementContextHumanizePatch());
        EnableSafe(new BotVaultingPatch());
        EnableSafe(new ManualFixedUpdateSkipPatch());

        // Inventory subsystem patches
        EnableSafe(new AirdropLandedPatch());
        EnableSafe(new InventoryChangePatch());
        EnableSafe(new CorpseRegistrationPatch());
        EnableSafe(new RescueInterceptPatch());

        // BSG layer bypasses
        EnableSafe(new AssaultEnemyFarBypassPatch());  // takes over scavs at long range
        EnableSafe(new ExfilLayerBypassPatch());       // high-priority layer (79) that strands bots near exfils
        EnableSafe(new PtrlBirdEyeBypassPatch());      // splits Bird Eye away from the Goons

        // Faction-mod takeover toggles. OrbitBrainLayer always registers against the standard PMC / Scav /
        // Goon brain names, so mods like UNTAR / RUAF / BlackDiv whose bots use BaseBrain="PMC" are hijacked
        // by default. When a toggle is OFF we publish the mod's WildSpawnType-name substring to
        // OrbitBrainLayer's exclusion list, and the layer stays inert for matching bots so their own custom
        // layers (GoToCheckpoint / HuntTarget / …) win instead.
        ApplyFactionTakeoverToggle(UntarPluginGuid,    "UNTAR",         HijackUntar,        "untar");
        // RUAF Come Home ships two factions: the base RUAF roles (ruaf*) and the RUAF Hardcore "Remnant"
        // roles (remnant*). Both belong to the same mod, so the toggle must exclude both substrings.
        ApplyFactionTakeoverToggle(RuafPluginGuid,     "RUAF",          HijackRuaf,         "ruaf", "remnant");
        ApplyFactionTakeoverToggle(BlackDivPluginGuid, "BlackDivision", HijackBlackDivision, "blackDiv");
        // ISB's WildSpawnType members all begin with "ISB" (ISBSpecialForces, ISBTeamLeader,
        // ISBFirefly*, …), so the single substring covers the whole faction (match is case-insensitive
        // and no vanilla role name contains "isb").
        ApplyFactionTakeoverToggle(IsbPluginGuid,      "ISB",           HijackIsb,           "ISB");

        OrbitBrainLayer.SetVanillaScavExclusion(VanillaScavs.Value);
        OrbitBrainLayer.SetVanillaGoonExclusion(VanillaGoons.Value);
        OrbitBrainLayer.SetVanillaCultistExclusion(VanillaCultists.Value);
        OrbitBrainLayer.SetVanillaRaiderExclusion(VanillaRaiders.Value);
        if (VanillaScavs.Value) Logger.LogInfo("Disable ORBIT on scavs ON — bot scavs running on BSG's vanilla brain (PlayerScavs unaffected).");
        if (VanillaGoons.Value) Logger.LogInfo("Disable ORBIT on goons ON — Goons (Knight / Big Pipe / Bird Eye) running on BSG's vanilla brain.");
        if (VanillaCultists.Value) Logger.LogInfo("Disable ORBIT on cultists ON — Cultists (Priest / Warriors / cursed scavs) running on BSG's vanilla brain.");
        if (VanillaRaiders.Value) Logger.LogInfo("Disable ORBIT on raiders ON — Raiders (pmcBot) and Rogues (exUsec) running on BSG's vanilla brain.");

        var brains = new List<string>
        {
            nameof(BsgBrain.PMC),
            nameof(BsgBrain.PmcUsec),
            nameof(BsgBrain.PmcBear),
            nameof(BsgBrain.Assault),
            nameof(BsgBrain.Knight),
            nameof(BsgBrain.BigPipe),
            nameof(BsgBrain.BirdEye),
            nameof(BsgBrain.SectantPriest),
            nameof(BsgBrain.SectantWarrior)
        };

        BrainManager.AddCustomLayer(typeof(OrbitBrainLayer), brains, 19);

        // BSG's native LootPatrol layer (priority 3) steals control from OrbitBrainLayer whenever we briefly
        // go inactive in post-combat, leaving bots stuck in vanilla loot wandering — which would prevent
        // LootContainerAction from ever winning the utility roll. Strip it for every brain we route.
        BrainManager.RemoveLayer("LootPatrol", brains);

        Log.Info($"ORBIT {OrbitVersion} fully loaded — BrainManager wired");
    }

    private IEnumerator WaitForHandbook()
    {
        // Populate the server-backed price cache up front so headless clients (where HandbookClass never
        // appears) still have loot values. The SPT HTTP backend is up well before any raid; if the fetch
        // somehow fails it falls back to the on-disk handbook.json (see HandbookPriceCache).
        Looting.HandbookPriceCache.Init();

        // Normal clients also get Singleton<HandbookClass> once the menu builds it; ItemPriceLookup uses it
        // directly when present. This is just a log — pricing already works via the cache either way, so we
        // don't block on it forever like before (headless would never satisfy the old loop).
        var attempts = 0;
        while (Singleton<HandbookClass>.Instance == null && attempts < 60)
        {
            attempts++;
            yield return new WaitForSeconds(1f);
        }
        Log.Info(Singleton<HandbookClass>.Instance != null
            ? $"HandbookClass ready after {attempts}s — ItemPriceLookup using it directly"
            : "HandbookClass absent (headless client?) — ItemPriceLookup using the server price cache");
    }

    /// <summary>
    /// Detects a faction-mod by BepInEx plugin GUID. When OFF AND the mod is present, the role-name substring
    /// is registered with OrbitBrainLayer's exclusion list so matching bots stay on their mod's behaviour
    /// layers.
    /// </summary>
    private static void ApplyFactionTakeoverToggle(string pluginGuid, string label, ConfigEntry<bool> toggle, params string[] roleSubstrings)
    {
        var detected = Chainloader.PluginInfos.ContainsKey(pluginGuid);
        if (!detected)
        {
            LogSource.LogDebug($"{label}: plugin '{pluginGuid}' not present — toggle inert");
            return;
        }
        if (toggle.Value)
        {
            LogSource.LogInfo($"{label}: detected and takeover ON — ORBIT will run its bots");
        }
        else
        {
            foreach (var sub in roleSubstrings)
                OrbitBrainLayer.AddExcludedRoleSubstring(sub);
            LogSource.LogInfo($"{label}: detected and takeover OFF — bots with role containing [{string.Join(", ", roleSubstrings)}] will skip ORBIT");
        }
    }

    private static void EnableSafe(ModulePatch patch)
    {
        try
        {
            patch.Enable();
        }
        catch (Exception ex)
        {
            LogSource.LogError($"Patch {patch.GetType().Name} failed to enable: {ex.Message} — ORBIT will continue without it");
        }
    }

    private void SetupConfig()
    {
        const string general = "01. General";
        const string poiGuard = "02. POI guard duration (RESTART)";
        const string zones = "03. Advection zones";
        const string mainSetup = "04. Main objectives - setup (RESTART)";
        const string mainTune = "05. Main objectives - runtime tuning";
        const string takeover = "06. Faction-mod takeover (RESTART)";

        // ── 01. General ─────────────────────────────────────────────
        // Underlying keys kept as "Vanilla X" so 1.0.x configs aren't broken. F12 displays use DispName
        // to surface the clearer "Disable ORBIT on X" wording without flipping polarity (ON still means
        // detach ORBIT → vanilla brain).
        VanillaScavs = Config.Bind(general, "Vanilla scavs (RESTART)", false, new ConfigDescription(
            "OFF (default): bot scavs are controlled by ORBIT (cell dispatch, home pull, loot routing). ON: bot scavs run on BSG's vanilla brain — ORBIT doesn't attach to them, so 'Roaming Scavs' below has no effect. PlayerScavs always stay on ORBIT regardless of this toggle.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on scavs (RESTART)", Order = 5 }));
        VanillaGoons = Config.Bind(general, "Vanilla goons (RESTART)", false, new ConfigDescription(
            "OFF (default): Goons (Knight + Big Pipe + Bird Eye) are controlled by ORBIT. ON: Goons run on BSG's vanilla brain.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on goons (RESTART)", Order = 4 }));
        VanillaCultists = Config.Bind(general, "Vanilla cultists (RESTART)", false, new ConfigDescription(
            "OFF (default): Cultists (Priest + Warriors + cursed scavs) are controlled by ORBIT. ON: Cultists run on BSG's vanilla brain.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on cultists (RESTART)", Order = 3 }));
        VanillaRaiders = Config.Bind(general, "Vanilla raiders (RESTART)", true, new ConfigDescription(
            "OFF (default): Raiders (pmcBot — Reserve / Labs) and Rogues (exUsec — Lighthouse) are controlled by ORBIT. ON: they run on BSG's vanilla brain.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on raiders (RESTART)", Order = 2 }));
        RoamingScavs = Config.Bind(general, "Roaming Scavs", false, new ConfigDescription(
            "OFF (default): scavs stay near their spawn quartier (current cell + 8 neighbours). ON: scavs roam the whole map like PMCs. Ignored when Vanilla scavs is ON.",
            null, new ConfigurationManagerAttributes { Order = 1 }));
        RoamingGoons = Config.Bind(general, "Roaming Goons", true, new ConfigDescription(
            "OFF: Goons stay near their spawn quartier. ON (default): Goons roam the whole map. Ignored when Vanilla goons is ON.",
            null, new ConfigurationManagerAttributes { Order = 0 }));

        // ── 02. POI guard duration ──────────────────────────────────
        ObjectiveGuardDuration = Config.Bind(poiGuard, "Base guard duration (s, min..max)", new Vector2(60f, 180f), new ConfigDescription(
            "Time a squad holds at a quest/cover POI before requesting a new one. Longer = more static map.",
            null, new ConfigurationManagerAttributes { Order = 3 }));
        ObjectiveAdjustedGuardDuration = Config.Bind(poiGuard, "Synthetic POI guard duration (s, min..max)", new Vector2(3.5f, 6.5f), new ConfigDescription(
            "Same idea but for Synthetic POIs (virtual patrol coords with no real loot/quest). Kept short.",
            null, new ConfigurationManagerAttributes { Order = 2 }));
        ObjectiveGuardDurationCut = Config.Bind(poiGuard, "Loot/Quest guard duration cut (×, min..max)", new Vector2(0.2f, 0.5f), new ConfigDescription(
            "Once the whole squad has arrived at a loot/quest POI, the base guard wait is multiplied by a factor in this range (0.2-0.5 = 20-50% of base).",
            null, new ConfigurationManagerAttributes { Order = 1 }));

        // ── 03. Advection zones ─────────────────────────────────────
        AdvectionZoneRadiusScale = Config.Bind(zones, "Zone radius scale", 1f, new ConfigDescription(
            "Multiplier on the radius of per-map advection zones. 1.0 = author defaults.",
            new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { Order = 3 }));
        AdvectionZoneRadiusScale.SettingChanged += AdvectionZoneParametersChanged;

        AdvectionZoneForceScale = Config.Bind(zones, "Zone force scale", 1f, new ConfigDescription(
            "Multiplier on advection force strength. NEGATIVE flips attractors↔repulsors. 0 disables advection entirely.",
            new AcceptableValueRange<float>(-10f, 10f), new ConfigurationManagerAttributes { Order = 2 }));
        AdvectionZoneForceScale.SettingChanged += AdvectionZoneParametersChanged;

        AdvectionZoneRadiusDecayScale = Config.Bind(zones, "Zone falloff scale", 1f, new ConfigDescription(
            "How fast a zone's force decays with distance. Larger = tighter to the zone. 1.0 = linear-ish.",
            new AcceptableValueRange<float>(0f, 5f), new ConfigurationManagerAttributes { Order = 1 }));
        AdvectionZoneRadiusDecayScale.SettingChanged += AdvectionZoneParametersChanged;

        ConvergenceEnabled = Config.Bind(zones, "Player convergence", false, new ConfigDescription(
            "Master toggle for the player-convergence pull: every cell of the dispatch grid receives a vector toward the living human player(s), so the world drifts toward where the action is. OFF (default): the field stays zero everywhere, regardless of the per-map JSON settings.",
            null, new ConfigurationManagerAttributes { Order = 0 }));
        ConvergenceEnabled.SettingChanged += ConvergenceParametersChanged;

        ConvergenceRadiusScale = Config.Bind(zones, "Convergence radius scale", 1f, new ConfigDescription(
            "Multiplier on the radius of the convergence pull emitted from living human players (per-map base radius in Maps/Zones JSON). 1.0 = author defaults.",
            new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { Order = -1 }));
        ConvergenceRadiusScale.SettingChanged += ConvergenceParametersChanged;

        ConvergenceForceScale = Config.Bind(zones, "Convergence force scale", 1f, new ConfigDescription(
            "Multiplier on the strength of the convergence pull emitted from living human players. NEGATIVE pushes bots AWAY from players. 0 disables the pull.",
            new AcceptableValueRange<float>(-10f, 10f), new ConfigurationManagerAttributes { Order = -2 }));
        ConvergenceForceScale.SettingChanged += ConvergenceParametersChanged;

        // ── 04. Main objectives - setup ─────────────────────────────
        MainObjectivesEnabled = Config.Bind(mainSetup, "Enabled", true, new ConfigDescription(
            "Master toggle for the main-objectives system. OFF = baseline dispatch.",
            null, new ConfigurationManagerAttributes { Order = 100 }));
        MainObjectivesEnabledForPmc = Config.Bind(mainSetup, "Enabled for PMC", true, new ConfigDescription(
            "Per-faction opt-out: if OFF, PMC squads skip the main-objectives system.",
            null, new ConfigurationManagerAttributes { Order = 99 }));
        MainObjectivesEnabledForPlayerScav = Config.Bind(mainSetup, "Enabled for PlayerScav", true, new ConfigDescription(
            "Per-faction opt-out: if OFF, PlayerScav squads skip the system.",
            null, new ConfigurationManagerAttributes { Order = 98 }));
        MainObjectivesCountMinPmc = Config.Bind(mainSetup, "PMC: main count min", 1, new ConfigDescription(
            "Lower bound on number of mains per PMC squad (uniform random with max).",
            new AcceptableValueRange<int>(1, 20), new ConfigurationManagerAttributes { Order = 91 }));
        MainObjectivesCountMaxPmc = Config.Bind(mainSetup, "PMC: main count max", 5, new ConfigDescription(
            "Upper bound on number of mains per PMC squad.",
            new AcceptableValueRange<int>(1, 20), new ConfigurationManagerAttributes { Order = 90 }));
        MainObjectivesCountMinPlayerScav = Config.Bind(mainSetup, "PlayerScav: main count min", 1, new ConfigDescription(
            "Lower bound on number of mains per PlayerScav squad.",
            new AcceptableValueRange<int>(1, 20), new ConfigurationManagerAttributes { Order = 89 }));
        MainObjectivesCountMaxPlayerScav = Config.Bind(mainSetup, "PlayerScav: main count max", 5, new ConfigDescription(
            "Upper bound on number of mains per PlayerScav squad.",
            new AcceptableValueRange<int>(1, 20), new ConfigurationManagerAttributes { Order = 88 }));
        MainObjectivesPmcQuestWeight = Config.Bind(mainSetup, "PMC mix — Quest %", 0.70f, new ConfigDescription(
            "Quest share of PMC main rolls (auto-normalised against the other two — only the ratio matters).",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 79 }));
        MainObjectivesPmcKillsWeight = Config.Bind(mainSetup, "PMC mix — Kills %", 0.15f, new ConfigDescription(
            "Kills share of PMC main rolls. Higher = more PMCs target PvP hotspots.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 78 }));
        MainObjectivesPmcLootValueWeight = Config.Bind(mainSetup, "PMC mix — LootValue %", 0.15f, new ConfigDescription(
            "LootValue share of PMC main rolls. Higher = more PMCs target loot-rich cells.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 77 }));
        MainObjectivesPlayerScavQuestWeight = Config.Bind(mainSetup, "PlayerScav mix — Quest %", 0.10f, new ConfigDescription(
            "Quest share of PlayerScav main rolls.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 69 }));
        MainObjectivesPlayerScavKillsWeight = Config.Bind(mainSetup, "PlayerScav mix — Kills %", 0.30f, new ConfigDescription(
            "Kills share of PlayerScav main rolls.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 68 }));
        MainObjectivesPlayerScavLootValueWeight = Config.Bind(mainSetup, "PlayerScav mix — LootValue %", 0.60f, new ConfigDescription(
            "LootValue share of PlayerScav main rolls.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 67 }));
        MainObjectivesTopLootCellsMaxCount = Config.Bind(mainSetup, "Top loot cells max", 10, new ConfigDescription(
            "How many of the richest cells are kept in the LootValue main-anchor pool. Lower = stronger PvP concentration on the top spots.",
            new AcceptableValueRange<int>(1, 50), new ConfigurationManagerAttributes { Order = 27 }));

        // ── 05. Main objectives - runtime tuning ────────────────────
        MainObjectivesExtractOnAllCompleted = Config.Bind(mainTune, "Extract when all mains done", true, new ConfigDescription(
            "ON (default): squad auto-extracts once every main is completed.",
            null, new ConfigurationManagerAttributes { Order = 100 }));
        LootCoveragePct = Config.Bind(mainTune, "Loot coverage %", 0.7f, new ConfigDescription(
            "Per-POI pick probability — 0.7 = ~70% of items get looted, the rest are silently skipped (simulates player miss rate). 1.0 disables.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 95 }));
        MainObjectivesUnlockProbabilityIntermediate = Config.Bind(mainTune, "Locked door unlock % (intermediate)", 0.3f, new ConfigDescription(
            "Chance a PMC squad force-unlocks a locked door blocking an intermediate (non-main-anchor) POI. Main-anchor POIs always 100%. Scavs never unlock.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 90 }));
        MainObjectivesAttractionMagnitude = Config.Bind(mainTune, "Main pull strength", 4.0f, new ConfigDescription(
            "Maximum total pull magnitude across pending mains. Larger = squads commit harder to goals.",
            new AcceptableValueRange<float>(0f, 20f), new ConfigurationManagerAttributes { Order = 80 }));
        MainObjectivesKillsRoamForceMagnitude = Config.Bind(mainTune, "Kills roam pull strength", 3.0f, new ConfigDescription(
            "Constant pull toward the Kills anchor during roam phase.",
            new AcceptableValueRange<float>(0f, 20f), new ConfigurationManagerAttributes { Order = 79 }));
        MainObjectivesKillsRoamDuration = Config.Bind(mainTune, "Kills roam duration (s, min..max)", new Vector2(60f, 300f), new ConfigDescription(
            "Time the squad spends roaming around a Kills anchor before the main completes.",
            null, new ConfigurationManagerAttributes { Order = 78 }));
        MainObjectivesLootValueTimeoutSeconds = Config.Bind(mainTune, "LootValue timeout (s)", 300f, new ConfigDescription(
            "Safety net — a LootValue main auto-completes after this many seconds of engaged-time without cell-clean.",
            new AcceptableValueRange<float>(30f, 1800f), new ConfigurationManagerAttributes { Order = 70 }));
        MainObjectivesCombatCallerGraceSeconds = Config.Bind(mainTune, "Combat caller grace (s)", 5f, new ConfigDescription(
            "How long the combat-convergence override stays active after the last detected combat signal.",
            new AcceptableValueRange<float>(0f, 60f), new ConfigurationManagerAttributes { Order = 69 }));
        MainObjectivesRoamSplinterRadius = Config.Bind(mainTune, "Roam splinter radius (m)", 50f, new ConfigDescription(
            "Search radius each member uses when picking their own splinter during Kills roam / LootValue active.",
            new AcceptableValueRange<float>(10f, 200f), new ConfigurationManagerAttributes { Order = 68 }));
        SplinterSearchRadius = Config.Bind(mainTune, "Follower splinter search radius (m)", 30f, new ConfigDescription(
            "When a squad reaches its objective cell, followers are dispatched to splinter POIs within this radius around the leader.",
            new AcceptableValueRange<float>(5f, 100f), new ConfigurationManagerAttributes { Order = 67 }));
        ScavengeSweepRadius = Config.Bind(mainTune, "Scavenge sweep radius (m)", 10f, new ConfigDescription(
            "After finishing a loot, the bot chains to the nearest LooseLoot/Corpse within this radius. Each candidate is still gated by Loot Coverage %.",
            new AcceptableValueRange<float>(0f, 50f), new ConfigurationManagerAttributes { Order = 66 }));
        SameFloorLootYTolerance = Config.Bind(mainTune, "Same-floor sweep tolerance (m)", 2.5f, new ConfigDescription(
            "During chain-loot sweeps, candidates within this vertical Y delta of the bot are treated as 'same floor' and preferred over cross-floor candidates. Two-pass: same-floor first, cross-floor only if nothing same-floor is in range. 0 disables the bias (chain-loot ignores floors). Stops the elevator yo-yo on Resort.",
            new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { Order = 65 }));
        CrossFloorSplinterChance = Config.Bind(mainTune, "Cross-floor splinter chance", 0.2f, new ConfigDescription(
            "Per-pick probability that a roam splinter ignores the same-floor preference and picks floor-blind. Keeps bots from vacuuming an entire floor before ever touching a staircase: they loot some of the current floor, then naturally drift up or down. 0 = never change floor until the current one is exhausted; 1 = floor-blind picking (the old staircase yo-yo).",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 64 }));
        TimeExtractWindowPmc = Config.Bind(mainTune, "Time extract window — PMC (%)", new Vector2(5f, 30f), new ConfigDescription(
            "Per-squad random window (% of total raid duration remaining) at which ExtractRequested flips. Rolled once per squad. Matches SAIN's default range.",
            null, new ConfigurationManagerAttributes { Order = 60 }));
        TimeExtractWindowPlayerScav = Config.Bind(mainTune, "Time extract window — PlayerScav (%)", new Vector2(5f, 30f), new ConfigDescription(
            "Same as PMC but applied to PlayerScavs.",
            null, new ConfigurationManagerAttributes { Order = 59 }));
        PmcLootCellCooldownSeconds = Config.Bind(mainTune, "PMC loot cell cooldown (s)", 600f, new ConfigDescription(
            "How long after looting a cell that cell is invisible to the same PMC squad. Stops boomeranging back. 0 disables.",
            new AcceptableValueRange<float>(0f, 3600f), new ConfigurationManagerAttributes { Order = 50 }));
        SyntheticVisitCooldownSeconds = Config.Bind(mainTune, "Synthetic POI cooldown (s)", 180f, new ConfigDescription(
            "How long after finishing a Synthetic POI it's invisible to the same squad. 0 disables.",
            new AcceptableValueRange<float>(0f, 1800f), new ConfigurationManagerAttributes { Order = 49 }));
        OpportunisticCorpseScanIntervalSeconds = Config.Bind(mainTune, "Opportunistic corpse scan (s)", 2.5f, new ConfigDescription(
            "How often each squad re-runs the 'do I see a fresh corpse nearby?' raycast scan. Per-squad raycasts add up under heavy bot counts; default raised from 0.5s to 2.5s to lower the CPU baseline. Drop it back down if you want hyper-reactive corpse pickup.",
            new AcceptableValueRange<float>(0.5f, 10f), new ConfigurationManagerAttributes { Order = 48 }));

        // ── 06. Faction-mod takeover ────────────────────────────────
        HijackUntar = Config.Bind(takeover, "Take over UNTAR bots", false, new ConfigDescription(
            "OFF (default): UNTAR bots run on their own 'Go Home' behaviour. ON: ORBIT dispatcher takes them over and routes them like PMCs.",
            null, new ConfigurationManagerAttributes { Order = 3 }));
        HijackRuaf = Config.Bind(takeover, "Take over RUAF bots", false, new ConfigDescription(
            "OFF (default): RUAF bots run on their own 'Come Home' behaviour. ON: ORBIT routes them like PMCs.",
            null, new ConfigurationManagerAttributes { Order = 2 }));
        HijackBlackDivision = Config.Bind(takeover, "Take over Black Division bots", false, new ConfigDescription(
            "OFF (default): Black Division bots run on their own behaviour. ON: ORBIT routes them like PMCs.",
            null, new ConfigurationManagerAttributes { Order = 1 }));
        HijackIsb = Config.Bind(takeover, "Take over ISB bots", false, new ConfigDescription(
            "OFF (default): ISB bots run on their own behaviour. ON: ORBIT routes them like PMCs.",
            null, new ConfigurationManagerAttributes { Order = 0 }));

        // ── 07.x SAIN personality ───────────────────────────────────
        BindSainPersonalityConfigs();
    }

    private void BindSainPersonalityConfigs()
    {
        const string gen = "07.0 SAIN personality - General";
        const string timmy = "07.1 SAIN personality - Timmy";
        const string cautio = "07.2 SAIN personality - Cautious";
        const string averag = "07.3 SAIN personality - Average";
        const string aggres = "07.4 SAIN personality - Aggressive";
        const string veryag = "07.5 SAIN personality - Very aggressive";

        SainPersonalityEnabled = Config.Bind(gen, "Enable SAIN personality", true, new ConfigDescription(
            "When ON (and SAIN installed), PMC squads run per-archetype values from 07.1-5 instead of the global ORBIT knobs. Scavs/PlayerScavs always use globals.",
            null, new ConfigurationManagerAttributes { Order = 100 }));
        SainArchetypeTimmyBrains = Config.Bind(gen, "Brain names → Timmy (RESTART)", "Timmy", new ConfigDescription(
            "Comma-separated SAIN brain names that map to Timmy (random/erratic). Case-insensitive.",
            null, new ConfigurationManagerAttributes { Order = 95 }));
        SainArchetypeCautiousBrains = Config.Bind(gen, "Brain names → Cautious (RESTART)", "Rat, Coward, SnappingTurtle", new ConfigDescription(
            "Comma-separated SAIN brain names that map to Cautious (low-risk, loot-focused).",
            null, new ConfigurationManagerAttributes { Order = 90 }));
        SainArchetypeAverageBrains = Config.Bind(gen, "Brain names → Average (RESTART)", "Normal", new ConfigDescription(
            "Comma-separated SAIN brain names that map to Average (balanced). Any brain NOT matched in any of the 5 lists also falls back to Average.",
            null, new ConfigurationManagerAttributes { Order = 87 }));
        SainArchetypeAggressiveBrains = Config.Bind(gen, "Brain names → Aggressive (RESTART)", "Wreckless, Chad", new ConfigDescription(
            "Comma-separated SAIN brain names that map to Aggressive.",
            null, new ConfigurationManagerAttributes { Order = 85 }));
        SainArchetypeVeryAggressiveBrains = Config.Bind(gen, "Brain names → Very aggressive (RESTART)", "GigaChad", new ConfigDescription(
            "Comma-separated SAIN brain names that map to Very aggressive.",
            null, new ConfigurationManagerAttributes { Order = 80 }));
        SainTimmyExtrasEnabled = Config.Bind(gen, "Timmy: erratic extras", true, new ConfigDescription(
            "When ON, Timmy squads also get 20% wrong-cell pick and 5% forget-blacklist behaviours on top of their 07.1 numeric tunings.",
            null, new ConfigurationManagerAttributes { Order = 75 }));

        BindArchetype(timmy,
            mixQ: 1.0f, mixK: 1.0f, mixL: 1.5f,
            mainCount: new Vector2(1, 2), extract: new Vector2(100_000, 300_000), coverage: new Vector2(0.30f, 0.50f),
            sprint: 0.0f, lockedDoor: 0.10f, miniLoot: 0, sweep: 10f, splinter: 30f,
            killsRoam: new Vector2(30, 150), topLoot: 10,
            outQ: e => SainTimmyMainMixQ = e, outK: e => SainTimmyMainMixK = e, outL: e => SainTimmyMainMixL = e,
            outMainCount: e => SainTimmyMainCount = e, outExtract: e => SainTimmyExtractThreshold = e,
            outCoverage: e => SainTimmyLootCoverage = e, outSprint: e => SainTimmySprintPropensity = e,
            outLockedDoor: e => SainTimmyLockedDoorProba = e, outMiniLoot: e => SainTimmyMiniLootThreshold = e,
            outSweep: e => SainTimmyScavengeSweepRadius = e, outSplinter: e => SainTimmySplinterSearchRadius = e,
            outKillsRoam: e => SainTimmyKillsRoamDuration = e, outTopLoot: e => SainTimmyTopLootCellsMax = e);

        BindArchetype(cautio,
            mixQ: 0.8f, mixK: 0.2f, mixL: 2.5f,
            mainCount: new Vector2(2, 4), extract: new Vector2(200_000, 500_000), coverage: new Vector2(0.85f, 0.95f),
            sprint: 0.2f, lockedDoor: 0.10f, miniLoot: 5000, sweep: 15f, splinter: 18f,
            killsRoam: new Vector2(30, 150), topLoot: 10,
            outQ: e => SainCautiousMainMixQ = e, outK: e => SainCautiousMainMixK = e, outL: e => SainCautiousMainMixL = e,
            outMainCount: e => SainCautiousMainCount = e, outExtract: e => SainCautiousExtractThreshold = e,
            outCoverage: e => SainCautiousLootCoverage = e, outSprint: e => SainCautiousSprintPropensity = e,
            outLockedDoor: e => SainCautiousLockedDoorProba = e, outMiniLoot: e => SainCautiousMiniLootThreshold = e,
            outSweep: e => SainCautiousScavengeSweepRadius = e, outSplinter: e => SainCautiousSplinterSearchRadius = e,
            outKillsRoam: e => SainCautiousKillsRoamDuration = e, outTopLoot: e => SainCautiousTopLootCellsMax = e);

        BindArchetype(averag,
            mixQ: 1.0f, mixK: 1.0f, mixL: 1.0f,
            mainCount: new Vector2(1, 5), extract: new Vector2(500_000, 1_000_000), coverage: new Vector2(0.65f, 0.75f),
            sprint: 0.5f, lockedDoor: 0.30f, miniLoot: 10000, sweep: 10f, splinter: 30f,
            killsRoam: new Vector2(60, 300), topLoot: 10,
            outQ: e => SainAverageMainMixQ = e, outK: e => SainAverageMainMixK = e, outL: e => SainAverageMainMixL = e,
            outMainCount: e => SainAverageMainCount = e, outExtract: e => SainAverageExtractThreshold = e,
            outCoverage: e => SainAverageLootCoverage = e, outSprint: e => SainAverageSprintPropensity = e,
            outLockedDoor: e => SainAverageLockedDoorProba = e, outMiniLoot: e => SainAverageMiniLootThreshold = e,
            outSweep: e => SainAverageScavengeSweepRadius = e, outSplinter: e => SainAverageSplinterSearchRadius = e,
            outKillsRoam: e => SainAverageKillsRoamDuration = e, outTopLoot: e => SainAverageTopLootCellsMax = e);

        BindArchetype(aggres,
            mixQ: 0.7f, mixK: 2.5f, mixL: 0.7f,
            mainCount: new Vector2(2, 4), extract: new Vector2(1_000_000, 1_500_000), coverage: new Vector2(0.50f, 0.60f),
            sprint: 0.8f, lockedDoor: 0.45f, miniLoot: 15000, sweep: 8f, splinter: 39f,
            killsRoam: new Vector2(90, 450), topLoot: 5,
            outQ: e => SainAggressiveMainMixQ = e, outK: e => SainAggressiveMainMixK = e, outL: e => SainAggressiveMainMixL = e,
            outMainCount: e => SainAggressiveMainCount = e, outExtract: e => SainAggressiveExtractThreshold = e,
            outCoverage: e => SainAggressiveLootCoverage = e, outSprint: e => SainAggressiveSprintPropensity = e,
            outLockedDoor: e => SainAggressiveLockedDoorProba = e, outMiniLoot: e => SainAggressiveMiniLootThreshold = e,
            outSweep: e => SainAggressiveScavengeSweepRadius = e, outSplinter: e => SainAggressiveSplinterSearchRadius = e,
            outKillsRoam: e => SainAggressiveKillsRoamDuration = e, outTopLoot: e => SainAggressiveTopLootCellsMax = e);

        BindArchetype(veryag,
            mixQ: 0.3f, mixK: 4.0f, mixL: 0.5f,
            mainCount: new Vector2(2, 5), extract: new Vector2(1_500_000, 3_000_000), coverage: new Vector2(0.30f, 0.45f),
            sprint: 1.0f, lockedDoor: 0.60f, miniLoot: 20000, sweep: 5f, splinter: 45f,
            killsRoam: new Vector2(150, 750), topLoot: 3,
            outQ: e => SainVeryAggressiveMainMixQ = e, outK: e => SainVeryAggressiveMainMixK = e, outL: e => SainVeryAggressiveMainMixL = e,
            outMainCount: e => SainVeryAggressiveMainCount = e, outExtract: e => SainVeryAggressiveExtractThreshold = e,
            outCoverage: e => SainVeryAggressiveLootCoverage = e, outSprint: e => SainVeryAggressiveSprintPropensity = e,
            outLockedDoor: e => SainVeryAggressiveLockedDoorProba = e, outMiniLoot: e => SainVeryAggressiveMiniLootThreshold = e,
            outSweep: e => SainVeryAggressiveScavengeSweepRadius = e, outSplinter: e => SainVeryAggressiveSplinterSearchRadius = e,
            outKillsRoam: e => SainVeryAggressiveKillsRoamDuration = e, outTopLoot: e => SainVeryAggressiveTopLootCellsMax = e);
    }

    /// <summary>
    /// Generic per-archetype binder. Takes the section name, all 13 default values, and 13 assignment lambdas
    /// to land the resulting ConfigEntries on the right Plugin static fields. Pulled out because hand-writing
    /// 5 × 13 = 65 Config.Bind calls inline was unreadable.
    /// </summary>
    private void BindArchetype(string section,
        float mixQ, float mixK, float mixL,
        Vector2 mainCount, Vector2 extract, Vector2 coverage,
        float sprint, float lockedDoor, int miniLoot, float sweep, float splinter,
        Vector2 killsRoam, int topLoot,
        Action<ConfigEntry<float>> outQ, Action<ConfigEntry<float>> outK, Action<ConfigEntry<float>> outL,
        Action<ConfigEntry<Vector2>> outMainCount, Action<ConfigEntry<Vector2>> outExtract, Action<ConfigEntry<Vector2>> outCoverage,
        Action<ConfigEntry<float>> outSprint, Action<ConfigEntry<float>> outLockedDoor, Action<ConfigEntry<int>> outMiniLoot,
        Action<ConfigEntry<float>> outSweep, Action<ConfigEntry<float>> outSplinter,
        Action<ConfigEntry<Vector2>> outKillsRoam, Action<ConfigEntry<int>> outTopLoot)
    {
        outQ(Config.Bind(section, "Main mix — Quest weight",     mixQ, new ConfigDescription("Roll weight for Quest mains. Final mix is normalized against the sum Q+K+L.", null, new ConfigurationManagerAttributes { Order = 100 })));
        outK(Config.Bind(section, "Main mix — Kills weight",     mixK, new ConfigDescription("Roll weight for Kills mains.", null, new ConfigurationManagerAttributes { Order = 99 })));
        outL(Config.Bind(section, "Main mix — LootValue weight", mixL, new ConfigDescription("Roll weight for LootValue mains.", null, new ConfigurationManagerAttributes { Order = 98 })));
        outMainCount(Config.Bind(section, "Main count (min, max)", mainCount, new ConfigDescription("Number of mains rolled per squad, uniform in [min..max].", null, new ConfigurationManagerAttributes { Order = 97 })));
        outExtract(Config.Bind(section, "Extract loot threshold (₽, min..max)", extract, new ConfigDescription("Rouble value that flips ExtractRequested. Rolled once per squad.", null, new ConfigurationManagerAttributes { Order = 96 })));
        outCoverage(Config.Bind(section, "Loot coverage % (min..max)", coverage, new ConfigDescription("Per-POI pick probability (LootValue cell + scavenge sweep). 1.0 = vacuum, 0.3 = skips most.", null, new ConfigurationManagerAttributes { Order = 95 })));
        outSprint(Config.Bind(section, "Sprint propensity (0..1)", sprint, new ConfigDescription("0 = never sprint, 1 = always.", null, new ConfigurationManagerAttributes { Order = 94 })));
        outLockedDoor(Config.Bind(section, "Locked door unlock %", lockedDoor, new ConfigDescription("Probability of force-unlocking a locked door for an intermediate POI. Main-anchor POIs always roll 100%.", null, new ConfigurationManagerAttributes { Order = 93 })));
        outMiniLoot(Config.Bind(section, "Mini-loot threshold (₽)", miniLoot, new ConfigDescription("Minimum item value the bot bothers picking up.", null, new ConfigurationManagerAttributes { Order = 92 })));
        outSweep(Config.Bind(section, "Scavenge sweep radius (m)", sweep, new ConfigDescription("After a loot, chain to the nearest LooseLoot/Corpse within this radius.", null, new ConfigurationManagerAttributes { Order = 91 })));
        outSplinter(Config.Bind(section, "Follower splinter radius (m)", splinter, new ConfigDescription("Non-leader members spread to splinter POIs within this radius of the leader.", null, new ConfigurationManagerAttributes { Order = 90 })));
        outKillsRoam(Config.Bind(section, "Kills roam duration (s, min..max)", killsRoam, new ConfigDescription("Time the squad spends roaming the Kills anchor before completing.", null, new ConfigurationManagerAttributes { Order = 89 })));
        outTopLoot(Config.Bind(section, "Top loot cells max", topLoot, new ConfigDescription("LootValue rolls are restricted to the top-N richest cells. Smaller = more concentration (more PvP).", null, new ConfigurationManagerAttributes { Order = 88 })));
    }

    private static void AdvectionZoneParametersChanged(object sender, EventArgs args)
    {
        // Phase 7 wires this to OrbitManager so live F12 edits propagate into the waypoint system's force
        // field. Until then it's a no-op.
    }

    private static void ConvergenceParametersChanged(object sender, EventArgs args)
    {
        Comfort.Common.Singleton<Core.OrbitManager>.Instance?.WaypointSystem?.CalculateConvergence();
    }
}
