using Orbit.Entities;
using Orbit.Systems;

namespace Orbit.Tasks;

/// <summary>Retire consumed loose-loot references without interrupting their pickup completion.</summary>
internal static class LooseLootRecovery
{
    // True asks the dispatcher to let a pending loot action reconcile its result before changing anchor.
    internal static bool PrepareSquad(Squad squad, WaypointSystem waypoints)
    {
        var anchor = squad.Objective.Location;
        var anchorMissing = waypoints.IsUnavailableLooseLoot(anchor);
        var completingAnchor = false;
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var member = squad.Members[i];
            if (member == null) continue;
            if (member.Objective.Status == ObjectiveStatus.Looting)
            {
                if (anchorMissing && member.Objective.Location == anchor) completingAnchor = true;
                continue;
            }
            if (waypoints.IsUnavailableLooseLoot(member.Objective.Location))
                Abandon(member, waypoints);
        }

        if (!anchorMissing) return false;
        if (completingAnchor) return true;

        waypoints.RemoveWaypoint(anchor.Id);
        squad.CompletedPoiIds.Add(anchor.Id);
        squad.Objective.LocationPrevious = anchor;
        squad.Objective.Location = null;
        squad.Objective.CoverPoints.Clear();
        squad.Objective.Status = SquadObjectiveState.Active;
        squad.Objective.Duration = 0f;
        squad.Objective.DurationAdjusted = false;
        Log.Debug($"LOOT RECOVERY: {squad} retired missing anchor {anchor}, requesting a fresh objective");
        return false;
    }

    internal static void Abandon(Agent agent, WaypointSystem waypoints)
    {
        var objective = agent.Objective;
        var location = objective.Location;
        if (!waypoints.IsUnavailableLooseLoot(location)) return;

        waypoints.RemoveWaypoint(location.Id);
        waypoints.ReleaseClaim(location.Id, agent.Id);
        agent.Squad?.CompletedPoiIds.Add(location.Id);
        if (agent.Squad?.Objective.Location == location)
            agent.Squad.Objective.Duration = 0f;
        objective.Location = null;
        objective.SplinterParent = null;
        objective.ArrivalPath = null;
        objective.Status = ObjectiveStatus.None;
        agent.Guard.CoverPoint = null;
        Log.Debug($"LOOT RECOVERY: {agent} retired missing target {location}, released claim and assignment");
    }
}
