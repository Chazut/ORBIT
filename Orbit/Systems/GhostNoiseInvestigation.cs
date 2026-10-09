using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

internal static class GhostNoiseInvestigation
{
    // Hearing must not queue a detour to execute after an unrelated long objective.
    internal static bool Committed(Squad squad)
        => squad.ExtractRequested || MainObjective.HasPendingRush(squad.MainObjectives)
           || squad.Operation?.Active == true || squad.Camp.Active || squad.CorpseEscort.Active;

    internal static string Rejection(Squad squad, WaypointSystem waypoints, float now)
    {
        if (!squad.InvestigateNoisePosition.HasValue) return null;
        if (now >= squad.InvestigateNoiseExpiresAt) return "expired";
        if (Committed(squad)) return "objective committed";
        if (squad.MainObjectives == null || squad.Leader == null) return null;
        var radius = ServerConfig.MainObjectives.RoamSplinterRadius;
        foreach (var main in squad.MainObjectives)
        {
            if (!main.CanPursue(squad.MainObjectives) || main.Type != MainObjectiveType.Kills
                || main.KillAmbush || main.KillsRoamStartedAt <= 0) continue;
            if (WaypointSystem.XzDistanceSqr(main.Position, squad.Leader.Position) <= radius * radius
                && waypoints.MatchesZoneFloorAtTarget(main.ZoneFloorId, main.Position, squad.Leader.Position)
                && WaypointSystem.XzDistanceSqr(main.Position, squad.InvestigateNoisePosition.Value) > radius * radius)
                return "local Kill objective";
        }
        return null;
    }

    internal static void Revalidate(Squad squad, WaypointSystem waypoints)
    {
        var reason = Rejection(squad, waypoints, Time.time);
        if (reason == null) return;
        Log.Info($"GHOST INVESTIGATION: {squad} discarded reason={reason} source={squad.InvestigateNoisePosition.Value}");
        squad.InvestigateNoisePosition = null;
        waypoints.CancelNoiseInvestigation(squad);
    }
}
