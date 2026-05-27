using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Orbit.Core;
using Orbit.Navigation;
using SPT.Reflection.Patching;

namespace Orbit.Inventory.Patches;

/// <summary>
/// When any Player (human / PMC bot / scav bot) dies, BSG instantiates a
/// Corpse GameObject via Player.CreateCorpse(). We register the fresh
/// corpse as a runtime Corpse waypoint so other bots can be dispatched to
/// loot it. When the kill is attributable to an ORBIT-managed agent, we
/// also tag the corpse as "owned" by that agent's squad and force an
/// immediate re-dispatch of the killer onto the body.
/// </summary>
public class CorpseRegistrationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        // Parameterless overload — Player.CreateCorpse() — which delegates
        // to the generic CreateCorpse<T>(Vector3 velocity). Capturing the
        // result here is simpler than patching the generic.
        return typeof(Player).GetMethod(nameof(Player.CreateCorpse), Type.EmptyTypes);
    }

    [PatchPostfix]
    public static void Postfix(Player __instance, Corpse __result)
    {
        if (__result == null)
        {
            Log.Debug($"CorpseRegistration: NULL __result for {__instance?.Profile?.Info?.Nickname ?? "?"} — no waypoint created");
            return;
        }

        var manager = Singleton<OrbitManager>.Instance;
        if (manager?.WaypointSystem == null)
        {
            Log.Debug($"CorpseRegistration: OrbitManager / WaypointSystem null for {__instance?.Profile?.Info?.Nickname ?? "?"} — no waypoint created");
            return;
        }

        try
        {
            var pos = __result.transform.position;
            var loc = new Waypoint(
                manager.WaypointSystem.NewRuntimeWaypointId(),
                WaypointCategory.Corpse,
                __result.name,
                pos,
                radiusSqr: 3f * 3f, // tight enough to require same-room, loose enough to absorb BSG's 2-3m stopping inertia
                doors: new List<Door>(),
                coverPoints: new List<CoverPoint>(),
                target: __result
            );

            manager.WaypointSystem.AddRuntimeWaypoint(loc);
            var victimName = __instance?.Profile?.Info?.Nickname ?? "?";
            Log.Debug($"CorpseRegistration: registered Corpse waypoint {loc} for victim {victimName} at ({pos.x:F2},{pos.y:F2},{pos.z:F2})");

            // Attribute the kill to an ORBIT squad if possible. The victim's
            // LastAggressor is set by BSG's damage pipeline just before
            // CreateCorpse fires, so by the time this postfix runs it points
            // at whoever landed the killing blow. The field is internal —
            // Traverse keeps us from hand-rolling the reflection.
            var aggressor = Traverse.Create(__instance).Field("LastAggressor").GetValue<IPlayer>();
            if (aggressor == null)
            {
                Log.Debug($"CorpseRegistration: {loc} has no LastAggressor on victim {victimName} — registered but no killer squad credited");
                return;
            }
            var aggressorProfileId = aggressor.ProfileId;
            if (string.IsNullOrEmpty(aggressorProfileId))
            {
                Log.Debug($"CorpseRegistration: {loc} aggressor has empty ProfileId — registered but no killer squad credited");
                return;
            }
            var agents = manager.AgentData.Entities.Values;
            for (var i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a?.Player == null) continue;
                if (a.Player.ProfileId != aggressorProfileId) continue;
                if (a.Squad == null)
                {
                    Log.Debug($"CorpseRegistration: {a} matched aggressor for {loc} but has no Squad — registered but no squad credited");
                    return;
                }
                manager.WaypointSystem.TagCorpseKillerSquad(loc.Id, a.Squad.Id);
                // Force immediate re-dispatch so the own-kill priority-pick
                // fires before the killer drifts away from the body.
                a.Squad.Objective.Duration = 0;
                // Route the killer specifically onto the corpse on the next
                // dispatch tick — without this they'd be just one of N
                // candidates in the roam splinter reservoir sample, with a
                // ~1/N chance of being chosen. UpdateAgents reads these two
                // fields and clears them once the dispatch is set.
                a.Squad.PendingOwnKillKillerAgentId = a.Id;
                a.Squad.PendingOwnKillCorpseLocId = loc.Id;
                Log.Info($"CorpseRegistration: {a} ({a.Squad}) credited with {loc} — forced squad re-dispatch + direct-route on next tick");
                return;
            }
            Log.Debug($"CorpseRegistration: aggressor profileId={aggressorProfileId.Substring(0, 8)}… is not ORBIT-managed for {loc} — registered but no squad credited");
        }
        catch (Exception ex)
        {
            Log.Error($"CorpseRegistration failed for {__instance?.Profile?.Info?.Nickname}: {ex}");
        }
    }
}
