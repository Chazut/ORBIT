using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    internal bool TryMainApproachDestination(Squad squad, MainObjective main, out Vector3 destination)
    {
        destination = main.Position;
        if (!main.HasAreaAnchor) return true;
        if (!IsValidCell(main.CellCoords)) return false;

        // These POIs already have ground positions from the map's NavMesh. Use one as a
        // travel anchor instead of sampling the cell's artificial Y=0. The route search
        // still verifies access in its normal budget; this scan starts no path queries.
        Waypoint nearest = null;
        var best = float.MaxValue;
        foreach (var point in _cells[main.CellCoords.x, main.CellCoords.y].Waypoints)
        {
            if (point.Category is not (WaypointCategory.ContainerLoot or WaypointCategory.LooseLoot
                or WaypointCategory.Synthetic)) continue;
            if (IsSquadKnownUnreachable(squad, point.Id) || !CanSelectMemberDoorTarget(squad, point)) continue;
            var gap = (point.Position - squad.Leader.Position).sqrMagnitude;
            if (gap >= best) continue;
            best = gap; nearest = point;
        }
        // A consumed POI still describes valid ground. This grants no claim or loot order;
        // the ordinary dispatcher takes over as soon as the squad enters the area.
        if (nearest == null) return false;
        destination = nearest.Position;
        return true;
    }
}
