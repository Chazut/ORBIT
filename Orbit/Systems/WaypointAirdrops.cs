using System.Collections.Generic;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Tasks.Actions;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private sealed class LandedAirdrop
    {
        internal LootableContainer Container;
        internal Waypoint Point;
    }

    private readonly List<LandedAirdrop> _landedAirdrops = new();
    private readonly NavMeshPath _airdropPath = new();
    private float _nextAirdropPath;

    internal void RegisterLandedAirdrop(LootableContainer container)
    {
        if (container == null) return;
        foreach (var drop in _landedAirdrops) if (drop.Container == container) return;
        _landedAirdrops.RemoveAll(drop => drop.Container == null);
        _landedAirdrops.Add(new LandedAirdrop { Container = container });
        RegisterAmbushAirdrop(container);
        Log.Info($"AIRDROP LOOT: landed crate registered at {container.transform.position}");
    }

    private Waypoint AirdropPoint(LootableContainer container)
    {
        foreach (var drop in _landedAirdrops)
        {
            if (drop.Container != container) continue;
            if (drop.Point != null) return drop.Point;
            if (!NavMesh.SamplePosition(container.transform.position, out var hit, 2f, NavMesh.AllAreas)) return null;
            // Keep the crate out of the general grid: each bot must discover it or choose its ambush.
            drop.Point = new Waypoint(NewRuntimeWaypointId(), WaypointCategory.ContainerLoot,
                "Airdrop", hit.position, 1f, new(), new(), container) { IsAirdrop = true };
            return drop.Point;
        }
        return null;
    }

    internal Waypoint TryFindOpportunisticAirdrop(Agent agent)
    {
        var squad = agent.Squad;
        if (_landedAirdrops.Count == 0 || agent.IsDormant || !agent.IsActive || squad == null
            || squad.ExtractRequested || agent.SoloExtractRequested || squad.CombatCallerMemberIdx >= 0
            || Time.time < squad.GhostFightUntil || squad.Camp.Active || squad.Camp.PendingAirdrop != null
            || squad.Operation?.Active == true || squad.CorpseEscort.Active
            || agent.Bot?.Memory?.HaveEnemy == true || agent.Bot?.Memory?.IsUnderFire == true
            || agent.Bot?.LookSensor == null || agent.Player == null
            || agent.Objective.Location?.IsAirdrop == true || agent.Objective.Location?.Category == WaypointCategory.Corpse
            || agent.Objective.Status == ObjectiveStatus.Looting || agent.LootHandler?.LootTaskRunning == true
            || agent.LootExtractSweep != null || Time.time < agent.NextAirdropScanAt) return null;
        agent.NextAirdropScanAt = Time.time + 2f;
        if (!(ServerConfig.Loot.LootingEnabled).IsBotEnabled(agent.Bot.Profile.Info.Settings.Role)) return null;
        var radius = ServerConfig.Loot.DetectDistance;
        var radiusSqr = radius * radius;
        foreach (var drop in _landedAirdrops)
        {
            var container = drop.Container;
            if (container == null || (container.transform.position - agent.Position).sqrMagnitude > radiusSqr) continue;
            var point = AirdropPoint(container);
            if (point == null || squad.CompletedPoiIds.Contains(point.Id) || agent.ValueSkippedPoiIds.Contains(point.Id)
                || IsClaimedByOther(point.Id, agent.Id) || !GotoObjectiveAction.IsLootableForAgent(agent, point)) continue;
            var target = container.transform.position + Vector3.up;
            if (!agent.Bot.LookSensor.IsPointInVisibleSector(target)) continue;
            var head = agent.Bot.LookSensor.HeadPoint;
            var ray = target - head;
            if (Physics.Raycast(head, ray.normalized, out var hit, ray.magnitude,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)
                && hit.transform != container.transform && !hit.transform.IsChildOf(container.transform)) continue;
            if (Time.time < _nextAirdropPath) return null;
            _nextAirdropPath = Time.time + .2f;
            if (!CalculateTimedPath(agent.Position, point.Position, _airdropPath, TransitionPhase.WaypointPath,
                "visible airdrop", squad.Id) || _airdropPath.status != NavMeshPathStatus.PathComplete) return null;
            return point;
        }
        return null;
    }
}
