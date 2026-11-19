using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Tasks.Actions;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly NavMeshPath _airdropPath = new();
    private float _nextAirdropPath;

    internal Waypoint TryFindOpportunisticAirdrop(Agent agent)
    {
        var squad = agent.Squad;
        if (_landedAirdrops.Count == 0 || agent.IsDormant || !agent.IsActive || squad == null
            || squad.ExtractRequested || agent.SoloExtractRequested || squad.CombatCallerMemberIdx >= 0
            || Time.time < squad.GhostFightUntil || squad.Camp.Active || squad.Camp.PendingAirdrop != null
            || (squad.Operation?.Active == true || MainObjective.HasPendingRush(squad.MainObjectives)) || squad.CorpseEscort.Active
            || agent.Bot?.Memory?.HaveEnemy == true || agent.Bot?.Memory?.IsUnderFire == true
            || agent.Bot?.LookSensor == null || agent.Player == null
            || agent.Objective.Location?.IsAirdrop == true || agent.Objective.Location?.Category == WaypointCategory.Corpse
            || agent.Objective.Status == ObjectiveStatus.Looting || agent.LootHandler?.LootTaskRunning == true
            || agent.LootExtractSweep != null || Time.time < agent.NextAirdropScanAt) return null;
        agent.NextAirdropScanAt = Time.time + 2f;
        if (!(ServerConfig.Loot.LootingEnabled).IsBotEnabled(agent.Bot.Profile.Info.Settings.Role)) return null;
        PruneDestroyedAirdrops();
        var radius = ServerConfig.Loot.DetectDistance;
        var radiusSqr = radius * radius;
        foreach (var drop in _landedAirdrops)
        {
            var container = drop.Container;
            if (container == null || (container.transform.position - agent.Position).sqrMagnitude > radiusSqr) continue;
            var point = AirdropPoint(container);
            if (point == null || squad.CompletedPoiIds.Contains(point.Id) || agent.ValueSkippedPoiIds.Contains(point.Id)
                || IsClaimedByOther(point.Id, agent.Id) || !GotoObjectiveAction.IsLootableForAgent(agent, point)) continue;
            var target = AirdropVisibility.Target(container);
            if (!agent.Bot.LookSensor.IsPointInVisibleSector(target)) continue;
            if (!AirdropVisibility.Check(agent.Bot.LookSensor.HeadPoint, container, out _)) continue;
            if (Time.time < _nextAirdropPath) return null;
            _nextAirdropPath = Time.time + .2f;
            if (!CalculateTimedPath(agent.Position, point.Position, _airdropPath, TransitionPhase.WaypointPath,
                "visible airdrop", squad.Id) || _airdropPath.status != NavMeshPathStatus.PathComplete) return null;
            return point;
        }
        return null;
    }
}
