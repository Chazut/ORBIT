using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly NavMeshPath _escortPath = new();

    internal bool IsClaimedByOther(int waypointId, int agentId)
        => _claims.TryGetValue(waypointId, out var holder) && holder != agentId;

    internal Waypoint CreateCorpseEscortWaypoint(Vector3 position)
        => new(NewRuntimeWaypointId(), WaypointCategory.Synthetic, "CorpseEscort", position,
            1f, new List<Door>(), new List<CoverPoint>(), null);

    internal void CollectCorpseEscortCover(Vector3 center, List<CoverPoint> result)
    {
        result.Clear();
        var data = _botsController?.CoversData;
        if (data == null) return;
        var index = data.GetIndexes(center);
        var voxels = data.GetVoxelesExtended(index.x, index.y, index.z, 3, true);
        for (var i = 0; i < voxels.Count; i++)
        {
            var points = voxels[i].Points;
            for (var p = 0; p < points.Count; p++)
            {
                var point = points[p];
                var delta = point.Position - center;
                if (delta.sqrMagnitude < 16f || delta.sqrMagnitude > 225f || Mathf.Abs(delta.y) > 2f) continue;
                var cover = new CoverPoint(point.Position, point.WallDirection, point.CoverType, point.CoverLevel);
                if (cover.Category != CoverCategory.None && !result.Contains(cover)) result.Add(cover);
                if (result.Count >= 64) return;
            }
        }
    }

    internal bool TryPickCorpseEscortPosition(Agent agent, Vector3 looterPosition, Vector3 anchor,
        List<CoverPoint> covers, List<Vector3> occupied, List<Vector3> rejected, int slot, out CoverPoint chosen)
    {
        // Local path validation prevents "nearby" guards on another floor or behind a long detour.
        // Native cover is preferred, with a strict budget per member instead of pathing every voxel.
        var pathBudget = 12;
        if (covers != null)
        {
            for (var category = CoverCategory.Hard; category <= CoverCategory.Soft; category++)
            {
                for (var c = 0; c < covers.Count && pathBudget > 0; c++)
                {
                    var candidate = covers[(c + slot * 7) % covers.Count];
                    if (candidate.Category != category) continue;
                    if (ValidateEscortPosition(agent, looterPosition, anchor, candidate.Position, occupied,
                            rejected, ref pathBudget, out var position))
                    {
                        chosen = new CoverPoint(position, candidate.Direction, candidate.Category, candidate.Level);
                        return true;
                    }
                }
            }
        }

        // No usable cover: dispersed walkable positions, still close to the looter. This is an ordinary
        // move order, never a teleport. Rotate the samples per member to avoid a shared destination.
        pathBudget = 8;
        for (var sample = 0; sample < 16 && pathBudget > 0; sample++)
        {
            var angle = (slot * 2.399963f) + sample * Mathf.PI / 4f;
            var radius = sample < 8 ? 6f : 10f;
            var candidate = anchor + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            if (!ValidateEscortPosition(agent, looterPosition, anchor, candidate, occupied,
                    rejected, ref pathBudget, out var position)) continue;
            chosen = new CoverPoint(position, (position - anchor).normalized, CoverCategory.None, CoverLevel.Stay);
            return true;
        }
        chosen = default;
        return false;
    }

    private bool ValidateEscortPosition(Agent agent, Vector3 looter, Vector3 anchor, Vector3 candidate,
        List<Vector3> occupied, List<Vector3> rejected, ref int pathBudget, out Vector3 position)
    {
        position = default;
        if (!NavMesh.SamplePosition(candidate, out var hit, .75f, NavMesh.AllAreas)) return false;
        position = hit.position;
        var delta = position - anchor;
        if (Mathf.Abs(delta.y) > 2f || delta.sqrMagnitude < 9f || delta.sqrMagnitude > 225f
            || (position - looter).sqrMagnitude < 9f) return false;
        foreach (var other in occupied)
            if ((position - other).sqrMagnitude < 9f) return false;
        foreach (var bad in rejected)
            if ((position - bad).sqrMagnitude < 4f) return false;
        pathBudget--;
        if (!NavMesh.SamplePosition(looter, out var origin, 1.5f, NavMesh.AllAreas)
            || !NavMesh.CalculatePath(origin.position, position, NavMesh.AllAreas, _escortPath)
            || _escortPath.status != NavMeshPathStatus.PathComplete
            || PathHelper.TotalLength(_escortPath.corners) > 24f) return false;
        return IsReachableFromPosition(agent.Position, position);
    }
}
