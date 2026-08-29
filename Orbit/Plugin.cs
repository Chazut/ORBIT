using Orbit.Helpers;
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
/// BepInEx entry point. The F12 surface now only holds client-side logging knobs plus a button
/// to the server web UI; every behaviour tunable lives in the ORBIT server mod's config, fetched
/// via Helpers.ServerConfig. <see cref="LogSource"/> exposes the BepInEx ManualLogSource consumed by
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
    public const string OrbitVersion = "2.0.0";

    public static ManualLogSource LogSource;

    // ╔══════════════════════════════════════════════════════════════╗
    // ║ F12 ConfigEntries                                              ║
    // ╚══════════════════════════════════════════════════════════════╝

    // Client-side knobs only. Everything that drives bot behaviour lives in the ORBIT server
    // mod's config (web UI at /orbit), fetched through Helpers.ServerConfig at boot + raid start.
    public static ConfigEntry<bool> QuietLogging;
    public static ConfigEntry<OrbitLogLevel> LogLevels;
    public static ConfigEntry<bool> PerfLogging;

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

        // AI limiter: awake bots must not raycast dormant (deactivated) ones. Inert while the limiter is
        // OFF — the dormant set stays empty and the prefix falls through.
        EnableSafe(new DormantVisionPatch());

        // Inventory subsystem patches
        EnableSafe(new AirdropLandedPatch());
        EnableSafe(new InventoryChangePatch());
        EnableSafe(new CorpseRegistrationPatch());
        EnableSafe(new RescueInterceptPatch());

        // BSG layer bypasses
        EnableSafe(new AssaultEnemyFarBypassPatch());  // takes over scavs at long range
        EnableSafe(new ExfilLayerBypassPatch());       // high-priority layer (79) that strands bots near exfils
        EnableSafe(new PtrlBirdEyeBypassPatch());      // splits Bird Eye away from the Goons

        // Faction behaviour now lives server-side (ORBIT server mod, web UI at /orbit) so it applies
        // wherever the bots run — headless included. One synchronous fetch at boot, defaults on failure.
        ServerConfig.Fetch();

        // Faction-mod takeover toggles. OrbitBrainLayer always registers against the standard PMC / Scav /
        // Goon brain names, so mods like UNTAR / RUAF / BlackDiv whose bots use BaseBrain="PMC" are hijacked
        // by default. When a toggle is OFF we publish the mod's WildSpawnType-name substring to
        // OrbitBrainLayer's exclusion list, and the layer stays inert for matching bots so their own custom
        // layers (GoToCheckpoint / HuntTarget / …) win instead.
        ApplyFactionTakeoverToggle(UntarPluginGuid,    "UNTAR",         ServerConfig.Factions.TakeOverUntar,        "untar");
        // RUAF Come Home ships two factions: the base RUAF roles (ruaf*) and the RUAF Hardcore "Remnant"
        // roles (remnant*). Both belong to the same mod, so the toggle must exclude both substrings.
        ApplyFactionTakeoverToggle(RuafPluginGuid,     "RUAF",          ServerConfig.Factions.TakeOverRuaf,         "ruaf", "remnant");
        ApplyFactionTakeoverToggle(BlackDivPluginGuid, "BlackDivision", ServerConfig.Factions.TakeOverBlackDivision, "blackDiv");
        // ISB's WildSpawnType members all begin with "ISB" (ISBSpecialForces, ISBTeamLeader,
        // ISBFirefly*, …), so the single substring covers the whole faction (match is case-insensitive
        // and no vanilla role name contains "isb").
        ApplyFactionTakeoverToggle(IsbPluginGuid,      "ISB",           ServerConfig.Factions.TakeOverIsb,           "ISB");
        ApplyFactionTakeoverToggle(CombineSoldiersPluginGuid, "CombineSoldiers", ServerConfig.Factions.TakeOverCombineSoldiers, "Combine");

        OrbitBrainLayer.SetVanillaScavExclusion(ServerConfig.Factions.VanillaScavs);
        OrbitBrainLayer.SetVanillaGoonExclusion(ServerConfig.Factions.VanillaGoons);
        OrbitBrainLayer.SetVanillaCultistExclusion(ServerConfig.Factions.VanillaCultists);
        OrbitBrainLayer.SetVanillaRaiderExclusion(ServerConfig.Factions.VanillaRaiders);
        OrbitBrainLayer.SetVanillaBloodhoundExclusion(ServerConfig.Factions.VanillaBloodhounds);
        if (ServerConfig.Factions.VanillaScavs) Logger.LogInfo("Disable ORBIT on scavs ON — bot scavs running on BSG's vanilla brain (PlayerScavs unaffected).");
        if (ServerConfig.Factions.VanillaGoons) Logger.LogInfo("Disable ORBIT on goons ON — Goons (Knight / Big Pipe / Bird Eye) running on BSG's vanilla brain.");
        if (ServerConfig.Factions.VanillaCultists) Logger.LogInfo("Disable ORBIT on cultists ON — Cultists (Priest / Warriors / cursed scavs) running on BSG's vanilla brain.");
        if (ServerConfig.Factions.VanillaRaiders) Logger.LogInfo("Disable ORBIT on raiders ON — Raiders (pmcBot) and Rogues (exUsec) running on BSG's vanilla brain.");
        if (ServerConfig.Factions.VanillaBloodhounds) Logger.LogInfo("Disable ORBIT on bloodhounds ON — Bloodhounds (Smugglers / arena spawns) running on BSG's vanilla brain.");

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
    private static void ApplyFactionTakeoverToggle(string pluginGuid, string label, bool takeoverOn, params string[] roleSubstrings)
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
        if (takeoverOn)
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
        // Cfg-FILE section (kept so existing values persist); the F12 DISPLAY category is
        // blanked via Category="" — with a single section the header was pure noise.
        const string essentials = "01. Essentials";

        Config.Bind(essentials, "Server config", string.Empty, new ConfigDescription(
            "Every behaviour setting (factions, looting, extraction, AI limiter...) lives in the ORBIT server web UI.",
            null, new ConfigurationManagerAttributes { Category = "", Order = 2, HideDefaultButton = true, CustomDrawer = DrawWebConfigButton }));

        QuietLogging = Config.Bind(essentials, "Quiet logging", true, new ConfigDescription(
            "ON (default): clean log - only warnings & errors, regardless of the Log levels below. Turn it OFF to use the Log levels (e.g. tick Debug there before sending a bug report).",
            null, new ConfigurationManagerAttributes { Category = "", Order = 1 }));
        LogLevels = Config.Bind(essentials, "Log levels", OrbitLogLevel.Info | OrbitLogLevel.Warning | OrbitLogLevel.Error, new ConfigDescription(
            "Which message levels ORBIT writes (used when Quiet logging is OFF). Default: everything except Debug. Tick Debug for a detailed bug-report log - it works in the release build now, not just debug builds.",
            null, new ConfigurationManagerAttributes { Category = "", Order = 0 }));
        PerfLogging = Config.Bind(essentials, "Performance logging", false, new ConfigDescription(
            "ON: writes a one-line 'PERF' summary (fps, hitches, GC, ORBIT activity counters) to the log every 30s, regardless of the other logging settings. Turn it on before recording a raid for a performance report.",
            null, new ConfigurationManagerAttributes { Category = "", Order = -1, IsAdvanced = true }));
    }

    // F12 helper: a real button that opens the server web UI in the default browser. The drawer
    // replaces the value field entirely (the entry's string value is never used).
    private static void DrawWebConfigButton(ConfigEntryBase entry)
    {
        if (!GUILayout.Button("Open web config UI", GUILayout.ExpandWidth(true))) return;
        var host = "https://127.0.0.1:6969";
        try
        {
            if (!string.IsNullOrEmpty(SPT.Common.Http.RequestHandler.Host))
                host = SPT.Common.Http.RequestHandler.Host;
        }
        catch
        {
            // spt-common quirk — fall back to the default SPT address.
        }
        Application.OpenURL(host + "/orbit");
    }

}
