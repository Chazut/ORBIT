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
    public const string OrbitVersion = "1.3.0";

    public static ManualLogSource LogSource;

    // ╔══════════════════════════════════════════════════════════════╗
    // ║ F12 ConfigEntries                                              ║
    // ╚══════════════════════════════════════════════════════════════╝

    // 02. Factions (faction toggles) + 01. Essentials (rally, quiet, tickrate)
    public static ConfigEntry<bool> RoamingScavs;
    public static ConfigEntry<bool> RoamingGoons;
    public static ConfigEntry<bool> VanillaScavs;
    public static ConfigEntry<bool> VanillaGoons;
    public static ConfigEntry<bool> VanillaCultists;
    public static ConfigEntry<bool> VanillaRaiders;
    public static ConfigEntry<bool> VanillaBloodhounds;
    public static ConfigEntry<bool> RoamingBloodhounds;
    public static ConfigEntry<bool> SquadRally;
    public static ConfigEntry<bool> QuietLogging;
    public static ConfigEntry<OrbitLogLevel> LogLevels;
    public static ConfigEntry<bool> PerfLogging;
    public static ConfigEntry<bool> DegradedTickrateEnabled;
    public static ConfigEntry<float> DegradedTickrateNearDistance;
    public static ConfigEntry<float> DegradedTickrateFarIntervalSeconds;

    // 08. POI guard
    public static ConfigEntry<Vector2> ObjectiveGuardDuration;
    public static ConfigEntry<Vector2> ObjectiveAdjustedGuardDuration;
    public static ConfigEntry<Vector2> ObjectiveGuardDurationCut;

    // 07. Advection & convergence (ConvergenceEnabled shown in 01. Essentials)
    public static ConfigEntry<float> AdvectionZoneRadiusScale;
    public static ConfigEntry<float> AdvectionZoneForceScale;
    public static ConfigEntry<float> AdvectionZoneRadiusDecayScale;
    public static ConfigEntry<bool> ConvergenceEnabled;
    public static ConfigEntry<float> ConvergenceRadiusScale;
    public static ConfigEntry<float> ConvergenceForceScale;

    // 03. PlayerScav (no SAIN archetype, so these actually drive PlayerScav behaviour)
    public static ConfigEntry<bool> MainObjectivesEnabledForPlayerScav;
    public static ConfigEntry<int> MainObjectivesCountMinPlayerScav;
    public static ConfigEntry<int> MainObjectivesCountMaxPlayerScav;
    public static ConfigEntry<float> MainObjectivesPlayerScavQuestWeight;
    public static ConfigEntry<float> MainObjectivesPlayerScavKillsWeight;
    public static ConfigEntry<float> MainObjectivesPlayerScavLootValueWeight;
    public static ConfigEntry<Vector2> TimeExtractWindowPlayerScav;

    // 06. Main objectives (global knobs only; per-archetype values are in 09.x, archetype-overridden fallbacks
    // are hard-coded in Sain.PersonalityFallback and no longer exposed in F12).
    public static ConfigEntry<bool> MainObjectivesEnabled;
    public static ConfigEntry<bool> MainObjectivesEnabledForPmc;
    public static ConfigEntry<bool> MainObjectivesExtractOnAllCompleted;
    public static ConfigEntry<float> MainObjectivesAttractionMagnitude;
    public static ConfigEntry<float> MainObjectivesKillsRoamForceMagnitude;
    public static ConfigEntry<float> MainObjectivesLootValueTimeoutSeconds;
    public static ConfigEntry<float> MainObjectivesCombatCallerGraceSeconds;
    public static ConfigEntry<float> MainObjectivesRoamSplinterRadius;
    public static ConfigEntry<Vector2> TimeExtractWindowPmc;
    public static ConfigEntry<float> PmcLootCellCooldownSeconds;
    public static ConfigEntry<float> SyntheticVisitCooldownSeconds;
    public static ConfigEntry<float> OpportunisticCorpseScanIntervalSeconds;
    public static ConfigEntry<float> SameFloorLootYTolerance;
    public static ConfigEntry<float> CrossFloorSplinterChance;

    // Faction-mod takeover (shown in 02. Factions, under Advanced)
    public static ConfigEntry<bool> HijackUntar;
    public static ConfigEntry<bool> HijackRuaf;
    public static ConfigEntry<bool> HijackBlackDivision;
    public static ConfigEntry<bool> HijackIsb;
    public static ConfigEntry<bool> HijackCombineSoldiers;

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
    // (no BepInPlugin), so this is the GUID to detect. Matched as a substring (see
    // ApplyFactionTakeoverToggle) so a future com.-prefixed variant ("com.samc137.ISBinfo") still registers.
    private const string IsbPluginGuid = "samc137.ISBinfo";
    private const string CombineSoldiersPluginGuid = "com.manimal.combinesoldiers";

    private void Awake()
    {
        LogSource = Logger;

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
        }
        catch (Exception ex)
        {
            // Config binding errors must not crash boot — they'd block the entire plugin from registering.
            // Log and continue with defaults.
            Log.Error($"ORBIT config bind failed (sub-systems will degrade to defaults): {ex}");
        }

        // Item pricing: ItemPriceLookup prefers EFT's Singleton<EFT.HandBook.Handbook> when present, else the
        // server-fetched price cache below. EFT.HandBook.Handbook is built by the main-menu/profile flow, which a
        // FIKA headless client skips, so we can't depend on it (issue #5).
        StartCoroutine(WaitForHandbook());

        Log.Always($"ORBIT {OrbitVersion} initialised");

        // Patches — wrap each in EnableSafe so one bad patch (wrong Harmony parameter name, missing target
        // method after a game update) can't collapse the rest of init. Without the guard a single failure
        // skips every subsequent .Enable() AND the BrainManager.AddCustomLayer call, leaving bots stranded on
        // BSG's vanilla brain.
        EnableSafe(new OrbitInitPatch());
        EnableSafe(new OrbitTickPatch());
        EnableSafe(new OrbitDisposePatch());

        EnableSafe(new DoorCarverShrinkPatch());
        EnableSafe(new DoorUnlockTracePatch());

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
        ApplyFactionTakeoverToggle(CombineSoldiersPluginGuid, "CombineSoldiers", HijackCombineSoldiers, "Combine");

        OrbitBrainLayer.SetVanillaScavExclusion(VanillaScavs.Value);
        OrbitBrainLayer.SetVanillaGoonExclusion(VanillaGoons.Value);
        OrbitBrainLayer.SetVanillaCultistExclusion(VanillaCultists.Value);
        OrbitBrainLayer.SetVanillaRaiderExclusion(VanillaRaiders.Value);
        OrbitBrainLayer.SetVanillaBloodhoundExclusion(VanillaBloodhounds.Value);
        if (VanillaScavs.Value) Logger.LogInfo("Disable ORBIT on scavs ON — bot scavs running on BSG's vanilla brain (PlayerScavs unaffected).");
        if (VanillaGoons.Value) Logger.LogInfo("Disable ORBIT on goons ON — Goons (Knight / Big Pipe / Bird Eye) running on BSG's vanilla brain.");
        if (VanillaCultists.Value) Logger.LogInfo("Disable ORBIT on cultists ON — Cultists (Priest / Warriors / cursed scavs) running on BSG's vanilla brain.");
        if (VanillaRaiders.Value) Logger.LogInfo("Disable ORBIT on raiders ON — Raiders (pmcBot) and Rogues (exUsec) running on BSG's vanilla brain.");
        if (VanillaBloodhounds.Value) Logger.LogInfo("Disable ORBIT on bloodhounds ON — Bloodhounds (Smugglers / arena spawns) running on BSG's vanilla brain.");

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

        // BSG's native LootPatrol layer (priority 3) steals control from OrbitBrainLayer whenever we briefly go
        // inactive post-combat, stranding bots in vanilla loot wandering. Strip it only for the brains ORBIT
        // fully owns, not the borrowed brains added below whose shared vanilla bots must stay 100% vanilla.
        // Pass a COPY: BigBrain keeps the list reference, so the brains.Add(...) below for the borrowed
        // brains would silently extend the LootPatrol strip to them too.
        BrainManager.RemoveLayer("LootPatrol", new List<string>(brains));

        // Custom factions borrow these vanilla brains, so register the layer here to drive them. The real vanilla
        // bots sharing them are excluded unconditionally in OrbitBrainLayer.IsExcludedRole, so the layer is inert
        // for them and their LootPatrol stays intact (not stripped above), keeping them 100% vanilla.
        brains.Add(nameof(BsgBrain.ExUsec));
        brains.Add(nameof(BsgBrain.BossGluhar));
        brains.Add(nameof(BsgBrain.FollowerGluharScout));
        BrainManager.AddCustomLayer(typeof(OrbitBrainLayer), brains, 19);

        Log.Always($"ORBIT {OrbitVersion} fully loaded — BrainManager wired");
    }

    private IEnumerator WaitForHandbook()
    {
        // Populate the server-backed price cache up front so headless clients (where EFT.HandBook.Handbook never
        // appears) still have loot values. The SPT HTTP backend is up well before any raid; if the fetch
        // somehow fails it falls back to the on-disk handbook.json (see HandbookPriceCache).
        Looting.HandbookPriceCache.Init();

        // Normal clients also get Singleton<EFT.HandBook.Handbook> once the menu builds it; ItemPriceLookup uses it
        // directly when present. This is just a log — pricing already works via the cache either way, so we
        // don't block on it forever like before (headless would never satisfy the old loop).
        var attempts = 0;
        while (Singleton<EFT.HandBook.Handbook>.Instance == null && attempts < 60)
        {
            attempts++;
            yield return new WaitForSeconds(1f);
        }
        Log.Info(Singleton<EFT.HandBook.Handbook>.Instance != null
            ? $"EFT.HandBook.Handbook ready after {attempts}s — ItemPriceLookup using it directly"
            : "EFT.HandBook.Handbook absent (headless client?) — ItemPriceLookup using the server price cache");
    }

    /// <summary>
    /// Detects a faction-mod by BepInEx plugin GUID. When OFF AND the mod is present, the role-name substring
    /// is registered with OrbitBrainLayer's exclusion list so matching bots stay on their mod's behaviour
    /// layers.
    /// </summary>
    private static void ApplyFactionTakeoverToggle(string pluginGuid, string label, ConfigEntry<bool> toggle, params string[] roleSubstrings)
    {
        // Exact GUID match first, then a case-insensitive substring fallback so a faction whose
        // plugin GUID gains/loses a prefix (e.g. a "com." on ISB's notifier) is still detected.
        var detected = Chainloader.PluginInfos.ContainsKey(pluginGuid);
        if (!detected)
        {
            foreach (var key in Chainloader.PluginInfos.Keys)
            {
                if (key.IndexOf(pluginGuid, StringComparison.OrdinalIgnoreCase) >= 0) { detected = true; break; }
            }
        }
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
        const string essentials = "01. Essentials";
        const string general = "02. Factions";
        const string takeover = "02. Factions";
        const string playerScav = "03. PlayerScav";
        const string mainSetup = "08. Main objectives";
        const string mainTune = "08. Main objectives";
        const string zones = "07. Advection & convergence";
        const string poiGuard = "06. POI guard (RESTART)";

        // ── 01. Essentials ──────────────────────────────────────────
        SquadRally = Config.Bind(essentials, "Squad rally", true, new ConfigDescription(
            "ON (default): when a squadmate takes fire, the others break off and converge to support (ORBIT routes them in, SAIN fights). OFF: everyone fights their own fight.",
            null, new ConfigurationManagerAttributes { Order = 9 }));
        QuietLogging = Config.Bind(essentials, "Quiet logging", true, new ConfigDescription(
            "ON (default): clean log — only warnings & errors, regardless of the Log levels below. Turn it OFF to use the Log levels (e.g. tick Debug there before sending a bug report).",
            null, new ConfigurationManagerAttributes { Order = 1 }));
        LogLevels = Config.Bind(essentials, "Log levels", OrbitLogLevel.Info | OrbitLogLevel.Warning | OrbitLogLevel.Error, new ConfigDescription(
            "Which message levels ORBIT writes (used when Quiet logging is OFF). Default: everything except Debug. Tick Debug for a detailed bug-report log — it works in the release build now, not just debug builds.",
            null, new ConfigurationManagerAttributes { Order = 0 }));
        PerfLogging = Config.Bind(essentials, "Performance logging", false, new ConfigDescription(
            "ON: writes a one-line 'PERF' summary (fps, hitches, GC, ORBIT activity counters) to the log every 30s, regardless of the other logging settings. Turn it on before recording a raid for a performance report.",
            null, new ConfigurationManagerAttributes { Order = -1, IsAdvanced = true }));

        // ── 02. Factions ────────────────────────────────────────────
        // "Disable ORBIT on X" keys are stored as "Vanilla X" so old configs aren't broken; ON = detach ORBIT
        // (vanilla brain), the DispName just shows the clearer wording.
        VanillaScavs = Config.Bind(general, "Vanilla scavs (RESTART)", false, new ConfigDescription(
            "ON: bot scavs run BSG's vanilla brain instead of ORBIT (so 'Roaming Scavs' does nothing). OFF (default): ORBIT controls them. PlayerScavs always stay on ORBIT.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on scavs (RESTART)", Order = 8 }));
        VanillaGoons = Config.Bind(general, "Vanilla goons (RESTART)", false, new ConfigDescription(
            "ON: Goons (Knight, Big Pipe, Bird Eye) run BSG's vanilla brain. OFF (default): ORBIT controls them.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on goons (RESTART)", Order = 7 }));
        VanillaCultists = Config.Bind(general, "Vanilla cultists (RESTART)", false, new ConfigDescription(
            "ON: Cultists run BSG's vanilla brain. OFF (default): ORBIT controls them.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on cultists (RESTART)", Order = 6 }));
        VanillaRaiders = Config.Bind(general, "Vanilla raiders (RESTART)", true, new ConfigDescription(
            "ON (default): Raiders (Reserve / Labs) and Rogues (Lighthouse) run BSG's vanilla brain. OFF: ORBIT controls them.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on raiders (RESTART)", Order = 5 }));
        RoamingScavs = Config.Bind(general, "Roaming Scavs", false, new ConfigDescription(
            "OFF (default): scavs stay near their spawn area. ON: they roam the whole map like PMCs.",
            null, new ConfigurationManagerAttributes { Order = 1 }));
        RoamingGoons = Config.Bind(general, "Roaming Goons", true, new ConfigDescription(
            "ON (default): Goons roam the whole map freely. OFF: ORBIT keeps nudging them back toward their spawn area whenever they're not fighting (a steady pull toward spawn) — but they still cover a lot of ground, since Goons hear and see from very far.",
            null, new ConfigurationManagerAttributes { Order = 0 }));
        VanillaBloodhounds = Config.Bind(general, "Vanilla bloodhounds (RESTART)", false, new ConfigDescription(
            "ON: Bloodhounds (Smugglers / arena spawns) run BSG's vanilla brain. OFF (default): ORBIT controls them.",
            null, new ConfigurationManagerAttributes { DispName = "Disable ORBIT on bloodhounds (RESTART)", Order = 4 }));
        RoamingBloodhounds = Config.Bind(general, "Roaming Bloodhounds", true, new ConfigDescription(
            "OFF: Bloodhounds stay near their spawn. ON (default): they roam the whole map.",
            null, new ConfigurationManagerAttributes { Order = -2 }));
        // ── 03. PlayerScav ──────────────────────────────────────────
        // PlayerScavs get no SAIN archetype, so (unlike PMCs) these values actually drive their behaviour.
        MainObjectivesEnabledForPlayerScav = Config.Bind(playerScav, "Enabled (goal system)", true, new ConfigDescription(
            "OFF: PlayerScav squads skip the Quest / Kills / LootValue goal system.",
            null, new ConfigurationManagerAttributes { Order = 100 }));
        MainObjectivesCountMinPlayerScav = Config.Bind(playerScav, "Main count min", 1, new ConfigDescription(
            "Fewest objectives a PlayerScav squad rolls.",
            new AcceptableValueRange<int>(1, 20), new ConfigurationManagerAttributes { Order = 90 }));
        MainObjectivesCountMaxPlayerScav = Config.Bind(playerScav, "Main count max", 5, new ConfigDescription(
            "Most objectives a PlayerScav squad rolls.",
            new AcceptableValueRange<int>(1, 20), new ConfigurationManagerAttributes { Order = 89 }));
        MainObjectivesPlayerScavQuestWeight = Config.Bind(playerScav, "Main mix — Quest %", 0.10f, new ConfigDescription(
            "Share of Quest objectives in the PlayerScav mix (normalized against Kills + LootValue).",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 80, ShowRangeAsPercent = true }));
        MainObjectivesPlayerScavKillsWeight = Config.Bind(playerScav, "Main mix — Kills %", 0.30f, new ConfigDescription(
            "Share of Kills objectives in the PlayerScav mix.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 79, ShowRangeAsPercent = true }));
        MainObjectivesPlayerScavLootValueWeight = Config.Bind(playerScav, "Main mix — LootValue %", 0.60f, new ConfigDescription(
            "Share of LootValue objectives in the PlayerScav mix.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 78, ShowRangeAsPercent = true }));
        TimeExtractWindowPlayerScav = Config.Bind(playerScav, "Time extract window (%)", new Vector2(10f, 30f), new ConfigDescription(
            "Random window (as % of raid time left) when a PlayerScav squad decides to head out.",
            null, new ConfigurationManagerAttributes { Order = 70 }));

        // 04. Looting + 05. Performance — bound here so the sections register in menu order.
        LootConfig.Init(Config);
        BindPerformanceConfig();

        // POI guard — how long squads hold a spot before moving on (advanced).
        ObjectiveGuardDuration = Config.Bind(poiGuard, "Base guard duration (s, min..max)", new Vector2(60f, 180f), new ConfigDescription(
            "How long a squad holds a quest/cover spot before picking a new objective. Higher = more static map.",
            null, new ConfigurationManagerAttributes { Order = 3, IsAdvanced = true }));
        ObjectiveAdjustedGuardDuration = Config.Bind(poiGuard, "Synthetic POI guard duration (s, min..max)", new Vector2(3.5f, 6.5f), new ConfigDescription(
            "Same, but for virtual patrol points (no real loot/quest). Kept short so bots keep moving.",
            null, new ConfigurationManagerAttributes { Order = 2, IsAdvanced = true }));
        ObjectiveGuardDurationCut = Config.Bind(poiGuard, "Loot/Quest guard duration cut (×, min..max)", new Vector2(0.2f, 0.5f), new ConfigDescription(
            "Once the whole squad has arrived at a loot/quest spot, the guard wait is cut to this fraction (e.g. 0.2–0.5 = 20–50% of base).",
            null, new ConfigurationManagerAttributes { Order = 1, IsAdvanced = true }));

        // Advection zone scales — fine-tuning of the per-map force field (advanced).
        AdvectionZoneRadiusScale = Config.Bind(zones, "Zone radius scale", 1f, new ConfigDescription(
            "Multiplier on per-map advection-zone radius. 1.0 = author defaults.",
            new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { Order = 3, IsAdvanced = true }));
        AdvectionZoneRadiusScale.SettingChanged += AdvectionZoneParametersChanged;

        AdvectionZoneForceScale = Config.Bind(zones, "Zone force scale", 1f, new ConfigDescription(
            "Multiplier on advection force. Negative flips attractors↔repulsors; 0 disables advection.",
            new AcceptableValueRange<float>(-10f, 10f), new ConfigurationManagerAttributes { Order = 2, IsAdvanced = true }));
        AdvectionZoneForceScale.SettingChanged += AdvectionZoneParametersChanged;

        AdvectionZoneRadiusDecayScale = Config.Bind(zones, "Zone falloff scale", 1f, new ConfigDescription(
            "How fast a zone's force fades with distance. Larger = tighter to the zone.",
            new AcceptableValueRange<float>(0f, 5f), new ConfigurationManagerAttributes { Order = 1, IsAdvanced = true }));
        AdvectionZoneRadiusDecayScale.SettingChanged += AdvectionZoneParametersChanged;

        ConvergenceEnabled = Config.Bind(essentials, "Player convergence", false, new ConfigDescription(
            "ON: the world gently drifts toward the human player(s), bringing more action your way. OFF (default): no pull. (Strength/range are under Advection in Advanced.)",
            null, new ConfigurationManagerAttributes { Order = 7 }));
        ConvergenceEnabled.SettingChanged += ConvergenceParametersChanged;

        ConvergenceRadiusScale = Config.Bind(zones, "Convergence radius scale", 1f, new ConfigDescription(
            "Multiplier on how far the pull toward players reaches. 1.0 = author defaults.",
            new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { Order = -1, IsAdvanced = true }));
        ConvergenceRadiusScale.SettingChanged += ConvergenceParametersChanged;

        ConvergenceForceScale = Config.Bind(zones, "Convergence force scale", 1f, new ConfigDescription(
            "Multiplier on how strongly bots are pulled toward players. Negative pushes them AWAY; 0 disables.",
            new AcceptableValueRange<float>(-10f, 10f), new ConfigurationManagerAttributes { Order = -2, IsAdvanced = true }));
        ConvergenceForceScale.SettingChanged += ConvergenceParametersChanged;

        // ── 08. Main objectives (advanced) ──────────────────────────
        // PMC squads with SAIN personality (the default) use their per-archetype values in section 09; the knobs
        // an archetype overrides are NOT exposed here (hard-coded in PersonalityFallback), since tuning them only
        // ever affected the rare SAIN-off case and confused users. What's left are the genuinely-global knobs.
        MainObjectivesEnabled = Config.Bind(mainSetup, "Enabled", true, new ConfigDescription(
            "Master switch for the goal system (Quest / Kills / LootValue objectives). OFF = plain dispatch.",
            null, new ConfigurationManagerAttributes { Order = 100, IsAdvanced = true }));
        MainObjectivesEnabledForPmc = Config.Bind(mainSetup, "Enabled for PMC", true, new ConfigDescription(
            "OFF: PMC squads skip the goal system.",
            null, new ConfigurationManagerAttributes { Order = 99, IsAdvanced = true }));
        MainObjectivesExtractOnAllCompleted = Config.Bind(mainTune, "Extract when all mains done", true, new ConfigDescription(
            "ON (default): the squad heads for an exfil once every objective is finished.",
            null, new ConfigurationManagerAttributes { Order = 90, IsAdvanced = true }));
        MainObjectivesAttractionMagnitude = Config.Bind(mainTune, "Main pull strength", 4.0f, new ConfigDescription(
            "How hard squads commit to their objectives. Larger = more goal-focused.",
            new AcceptableValueRange<float>(0f, 20f), new ConfigurationManagerAttributes { Order = 80, IsAdvanced = true }));
        MainObjectivesKillsRoamForceMagnitude = Config.Bind(mainTune, "Kills roam pull strength", 3.0f, new ConfigDescription(
            "Pull toward a Kills objective while hunting around it.",
            new AcceptableValueRange<float>(0f, 20f), new ConfigurationManagerAttributes { Order = 79, IsAdvanced = true }));
        MainObjectivesLootValueTimeoutSeconds = Config.Bind(mainTune, "LootValue timeout (s)", 300f, new ConfigDescription(
            "Safety net: a LootValue objective auto-finishes after this many seconds of engaged time.",
            new AcceptableValueRange<float>(30f, 1800f), new ConfigurationManagerAttributes { Order = 70, IsAdvanced = true }));
        MainObjectivesCombatCallerGraceSeconds = Config.Bind(mainTune, "Combat caller grace (s)", 5f, new ConfigDescription(
            "How long the rally-to-combat pull lingers after the last shots.",
            new AcceptableValueRange<float>(0f, 60f), new ConfigurationManagerAttributes { Order = 69, IsAdvanced = true }));
        MainObjectivesRoamSplinterRadius = Config.Bind(mainTune, "Roam splinter radius (m)", 50f, new ConfigDescription(
            "How far each member spreads to pick its own spot while roaming a Kills / LootValue objective.",
            new AcceptableValueRange<float>(10f, 200f), new ConfigurationManagerAttributes { Order = 68, IsAdvanced = true }));
        SameFloorLootYTolerance = Config.Bind(mainTune, "Same-floor sweep tolerance (m)", 2.5f, new ConfigDescription(
            "Loot within this vertical distance counts as 'same floor' and is preferred over loot on another floor (stops the staircase yo-yo). 0 = ignore floors.",
            new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { Order = 65, IsAdvanced = true }));
        CrossFloorSplinterChance = Config.Bind(mainTune, "Cross-floor splinter chance", 0.1f, new ConfigDescription(
            "Chance a bot picks loot on another floor instead of finishing the current one, so it drifts up/down naturally. 0% = clear a floor before moving; 100% = ignore floors.",
            new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 64, IsAdvanced = true, ShowRangeAsPercent = true }));
        TimeExtractWindowPmc = Config.Bind(mainTune, "Time extract window — PMC (%)", new Vector2(10f, 30f), new ConfigDescription(
            "Random window (as % of raid time left) when a PMC squad decides to head out. Rolled once per squad.",
            null, new ConfigurationManagerAttributes { Order = 60, IsAdvanced = true }));
        PmcLootCellCooldownSeconds = Config.Bind(mainTune, "PMC loot cell cooldown (s)", 600f, new ConfigDescription(
            "After looting a cell, how long before the same PMC squad will go back. Stops boomeranging. 0 = no cooldown.",
            new AcceptableValueRange<float>(0f, 3600f), new ConfigurationManagerAttributes { Order = 50, IsAdvanced = true }));
        SyntheticVisitCooldownSeconds = Config.Bind(mainTune, "Synthetic POI cooldown (s)", 180f, new ConfigDescription(
            "After finishing a patrol point, how long before the same squad revisits it. 0 = no cooldown.",
            new AcceptableValueRange<float>(0f, 1800f), new ConfigurationManagerAttributes { Order = 49, IsAdvanced = true }));
        OpportunisticCorpseScanIntervalSeconds = Config.Bind(mainTune, "Opportunistic corpse scan (s)", 2.5f, new ConfigDescription(
            "How often a squad checks for a fresh corpse nearby. Lower = snappier corpse looting but more CPU under heavy bot counts.",
            new AcceptableValueRange<float>(0.5f, 10f), new ConfigurationManagerAttributes { Order = 48, IsAdvanced = true }));

        // Faction-mod takeover — shown in the Factions section, under "Advanced settings" (niche custom factions).
        HijackUntar = Config.Bind(takeover, "Take over UNTAR bots", false, new ConfigDescription(
            "ON: ORBIT routes UNTAR bots like PMCs. OFF (default): they run on their own 'Go Home' behaviour.",
            null, new ConfigurationManagerAttributes { Order = -10, IsAdvanced = true }));
        HijackRuaf = Config.Bind(takeover, "Take over RUAF bots", false, new ConfigDescription(
            "ON: ORBIT routes RUAF bots like PMCs. OFF (default): they run on their own 'Come Home' behaviour.",
            null, new ConfigurationManagerAttributes { Order = -11, IsAdvanced = true }));
        HijackBlackDivision = Config.Bind(takeover, "Take over Black Division bots", false, new ConfigDescription(
            "ON: ORBIT routes Black Division bots like PMCs. OFF (default): they run on their own behaviour.",
            null, new ConfigurationManagerAttributes { Order = -12, IsAdvanced = true }));
        HijackIsb = Config.Bind(takeover, "Take over ISB bots", true, new ConfigDescription(
            "ON (default): ORBIT routes ISB bots like PMCs. OFF: they run on their own behaviour.",
            null, new ConfigurationManagerAttributes { Order = -13, IsAdvanced = true }));
        HijackCombineSoldiers = Config.Bind(takeover, "Take over Combine Soldiers bots", false, new ConfigDescription(
            "ON: ORBIT routes Manimal's Combine Soldiers like PMCs. OFF (default): they run on their own behaviour.",
            null, new ConfigurationManagerAttributes { Order = -14, IsAdvanced = true }));

        // ── 09.x SAIN personality ───────────────────────────────────
        BindSainPersonalityConfigs();
    }

    // 04. Performance — the two tickrate knobs (the on/off toggle lives in 01. Essentials).
    private void BindPerformanceConfig()
    {
        const string perf = "05. Performance";
        DegradedTickrateEnabled = Config.Bind("01. Essentials", "Degraded tickrate for off-screen squads", false, new ConfigDescription(
            "ON: squads far from every player re-decide much less often to save CPU. Movement/aiming/doors still run, they just defer NEW decisions. NOTE: the gain is small — ORBIT is a thin slice of a bot's cost (SAIN/EFT combat, vision, pathing dominate), so leaving this OFF (default) is fine for most setups.",
            null, new ConfigurationManagerAttributes { Order = 6 }));
        DegradedTickrateNearDistance = Config.Bind(perf, "Full-rate distance (m)", 200f, new ConfigDescription(
            "Squads within this distance of any player always run at full rate. Beyond it, the throttle below kicks in.",
            new AcceptableValueRange<float>(0f, 1000f), new ConfigurationManagerAttributes { Order = 1, IsAdvanced = true }));
        DegradedTickrateFarIntervalSeconds = Config.Bind(perf, "Far decision interval (s)", 6f, new ConfigDescription(
            "How often a far/off-screen squad re-decides. Higher = more CPU saved, slower reactions far away. 5–10s is a good range.",
            new AcceptableValueRange<float>(0.5f, 30f), new ConfigurationManagerAttributes { Order = 0, IsAdvanced = true }));
    }

    private void BindSainPersonalityConfigs()
    {
        const string gen = "09.0 SAIN personality - General";
        const string timmy = "09.1 SAIN personality - Timmy";
        const string cautio = "09.2 SAIN personality - Cautious";
        const string averag = "09.3 SAIN personality - Average";
        const string aggres = "09.4 SAIN personality - Aggressive";
        const string veryag = "09.5 SAIN personality - Very aggressive";

        SainPersonalityEnabled = Config.Bind(gen, "Enable SAIN personality", true, new ConfigDescription(
            "ON (default): PMC squads use the per-archetype values in 09.1–09.5 (matched from their SAIN brain) instead of the fallback knobs in section 05/06. Scavs/PlayerScavs always use the fallback.",
            null, new ConfigurationManagerAttributes { Order = 100 }));
        SainArchetypeTimmyBrains = Config.Bind(gen, "Brain names → Timmy (RESTART)", "Timmy", new ConfigDescription(
            "SAIN brain names (comma-separated, case-insensitive) treated as Timmy (random/erratic).",
            null, new ConfigurationManagerAttributes { Order = 95, IsAdvanced = true }));
        SainArchetypeCautiousBrains = Config.Bind(gen, "Brain names → Cautious (RESTART)", "Rat, Coward, SnappingTurtle", new ConfigDescription(
            "SAIN brain names treated as Cautious (low-risk, loot-focused).",
            null, new ConfigurationManagerAttributes { Order = 90, IsAdvanced = true }));
        SainArchetypeAverageBrains = Config.Bind(gen, "Brain names → Average (RESTART)", "Normal", new ConfigDescription(
            "SAIN brain names treated as Average (balanced). Any brain not listed in the five lists also falls back to Average.",
            null, new ConfigurationManagerAttributes { Order = 87, IsAdvanced = true }));
        SainArchetypeAggressiveBrains = Config.Bind(gen, "Brain names → Aggressive (RESTART)", "Wreckless, Chad", new ConfigDescription(
            "SAIN brain names treated as Aggressive.",
            null, new ConfigurationManagerAttributes { Order = 85, IsAdvanced = true }));
        SainArchetypeVeryAggressiveBrains = Config.Bind(gen, "Brain names → Very aggressive (RESTART)", "GigaChad", new ConfigDescription(
            "SAIN brain names treated as Very aggressive.",
            null, new ConfigurationManagerAttributes { Order = 80, IsAdvanced = true }));
        SainTimmyExtrasEnabled = Config.Bind(gen, "Timmy: erratic extras", true, new ConfigDescription(
            "ON: Timmy squads also get a 20% wrong-cell pick and 5% forget-blacklist quirk on top of their 09.1 numbers.",
            null, new ConfigurationManagerAttributes { Order = 75, IsAdvanced = true }));

        BindArchetype(timmy,
            mixQ: 0.29f, mixK: 0.29f, mixL: 0.42f,
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
            mixQ: 0.23f, mixK: 0.06f, mixL: 0.71f,
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
            mixQ: 0.34f, mixK: 0.33f, mixL: 0.33f,
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
            mixQ: 0.18f, mixK: 0.64f, mixL: 0.18f,
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
            mixQ: 0.06f, mixK: 0.83f, mixL: 0.11f,
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
        outQ(Config.Bind(section, "Main mix — Quest %",     mixQ, new ConfigDescription("Share of Quest mains for this archetype (auto-normalized against Kills + LootValue, so they need not total exactly 100%).", new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 100, IsAdvanced = true, ShowRangeAsPercent = true })));
        outK(Config.Bind(section, "Main mix — Kills %",     mixK, new ConfigDescription("Share of Kills mains for this archetype.", new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 99, IsAdvanced = true, ShowRangeAsPercent = true })));
        outL(Config.Bind(section, "Main mix — LootValue %", mixL, new ConfigDescription("Share of LootValue mains for this archetype.", new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 98, IsAdvanced = true, ShowRangeAsPercent = true })));
        outMainCount(Config.Bind(section, "Main count (min, max)", mainCount, new ConfigDescription("Number of mains rolled per squad, uniform in [min..max].", null, new ConfigurationManagerAttributes { Order = 97, IsAdvanced = true })));
        outExtract(Config.Bind(section, "Extract loot threshold (₽, min..max)", extract, new ConfigDescription("Rouble value that flips ExtractRequested. Rolled once per squad.", null, new ConfigurationManagerAttributes { Order = 96, IsAdvanced = true })));
        outCoverage(Config.Bind(section, "Loot coverage % (min..max)", coverage, new ConfigDescription("Per-POI pick probability (LootValue cell + scavenge sweep). 1.0 = vacuum, 0.3 = skips most.", null, new ConfigurationManagerAttributes { Order = 95, IsAdvanced = true })));
        outSprint(Config.Bind(section, "Sprint propensity (0..1)", sprint, new ConfigDescription("0 = never sprint, 1 = always.", null, new ConfigurationManagerAttributes { Order = 94, IsAdvanced = true })));
        outLockedDoor(Config.Bind(section, "Locked door unlock %", lockedDoor, new ConfigDescription("Probability of force-unlocking a locked door for an intermediate POI. Main-anchor POIs always roll 100%.", null, new ConfigurationManagerAttributes { Order = 93, IsAdvanced = true })));
        outMiniLoot(Config.Bind(section, "Mini-loot threshold (₽)", miniLoot, new ConfigDescription("Minimum item value the bot bothers picking up.", null, new ConfigurationManagerAttributes { Order = 92, IsAdvanced = true })));
        outSweep(Config.Bind(section, "Scavenge sweep radius (m)", sweep, new ConfigDescription("After a loot, chain to the nearest LooseLoot/Corpse within this radius.", null, new ConfigurationManagerAttributes { Order = 91, IsAdvanced = true })));
        outSplinter(Config.Bind(section, "Follower splinter radius (m)", splinter, new ConfigDescription("Non-leader members spread to splinter POIs within this radius of the leader.", null, new ConfigurationManagerAttributes { Order = 90, IsAdvanced = true })));
        outKillsRoam(Config.Bind(section, "Kills roam duration (s, min..max)", killsRoam, new ConfigDescription("Time the squad spends roaming the Kills anchor before completing.", null, new ConfigurationManagerAttributes { Order = 89, IsAdvanced = true })));
        outTopLoot(Config.Bind(section, "Top loot cells max", topLoot, new ConfigDescription("LootValue rolls are restricted to the top-N richest cells. Smaller = more concentration (more PvP).", null, new ConfigurationManagerAttributes { Order = 88, IsAdvanced = true })));
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
