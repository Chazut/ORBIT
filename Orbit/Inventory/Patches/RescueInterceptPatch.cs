using System.Reflection;
using Comfort.Common;
using EFT;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Navigation;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Inventory.Patches;

/// <summary>
/// BSG has two BotMover teleport methods. We intercept the "hard" rescue —
/// <c>BotMover.method_10(posiblePos)</c> — which snaps a stuck bot back to
/// its last good cast point. The public <c>Teleport</c> overload is a soft
/// in-bounds correction we don't care about.
///
/// When a bot was chasing a waypoint in an unreachable spot (Quest behind
/// a locked door, Corpse on a roof, etc.) the rescue fires over and over.
/// Reacting here: drop the waypoint from WaypointSystem so the squad gets a
/// fresh objective, and park the squad in a Wait state for 15s so BSG's
/// mover can re-anchor on the navmesh before the strategy hands it another
/// target. Ignore short teleports (less than 10m) — routine corrections.
/// </summary>
public class RescueInterceptPatch : ModulePatch
{
    private const float RescueDistanceThreshold = 10f;
    private const float RescueDistanceThresholdSqr = RescueDistanceThreshold * RescueDistanceThreshold;
    private const float PostRescueCooldownSeconds = 15f;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotMover).GetMethod(nameof(BotMover.method_10));
    }

    [PatchPrefix]
    public static void Prefix(BotMover __instance, Vector3 posiblePos)
    {
        var manager = Singleton<OrbitManager>.Instance;
        if (manager?.WaypointSystem == null || manager.AgentData == null) return;

        Agent matchedAgent = null;
        var agents = manager.AgentData.Entities.Values;
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a?.Bot?.Mover == __instance)
            {
                matchedAgent = a;
                break;
            }
        }
        if (matchedAgent == null) return;

        var distSqr = (posiblePos - matchedAgent.Position).sqrMagnitude;
        if (distSqr < RescueDistanceThresholdSqr) return;

        var loc = matchedAgent.Objective?.Location;
        if (loc == null) return;

        // Synthetic / Exfil waypoints aren't lootable and shouldn't be
        // pruned on a teleport — unreachable exfils get filtered upstream by
        // status / faction checks.
        switch (loc.Category)
        {
            case WaypointCategory.Quest:
            case WaypointCategory.LooseLoot:
            case WaypointCategory.ContainerLoot:
            case WaypointCategory.Corpse:
                var botPos = matchedAgent.Position;
                var objPos = loc.Position;
                Log.Info(
                    $"RescueIntercept: agent={matchedAgent} map={manager.MapId} " +
                    $"bot_pos=({botPos.x:F2},{botPos.y:F2},{botPos.z:F2}) " +
                    $"tp_dest=({posiblePos.x:F2},{posiblePos.y:F2},{posiblePos.z:F2}) " +
                    $"dist={Mathf.Sqrt(distSqr):F1}m " +
                    $"obj={loc.Category}:{loc.Id}:{loc.Target?.name ?? "?"} " +
                    $"obj_pos=({objPos.x:F2},{objPos.y:F2},{objPos.z:F2}) " +
                    "— purging waypoint"
                );
                manager.WaypointSystem.RemoveWaypoint(loc.Id);
                if (matchedAgent.Squad != null)
                {
                    var squadObj = matchedAgent.Squad.Objective;
                    squadObj.Location = null;
                    squadObj.LocationPrevious = null;
                    squadObj.Status = SquadObjectiveState.Wait;
                    squadObj.StartTime = Time.time;
                    squadObj.Duration = PostRescueCooldownSeconds;
                    squadObj.DurationAdjusted = false;
                }
                matchedAgent.Objective.Status = ObjectiveStatus.Failed;
                break;
        }
    }
}
