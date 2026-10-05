using System.Collections.Generic;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal sealed class TravelRouteWorld(WaypointSystem waypoints) : ITravelWorld
{
    private readonly Dictionary<(int X, int Y, int Z), (float Value, float Until)> _exposure = new();
    private readonly Queue<(int X, int Y, int Z)> _order = new();
    private readonly List<CoverPoint> _covers = new(32);
    private readonly NavMeshPath _path = new();
    internal int CacheHits, CacheMisses;
    private static readonly Vector3[] Directions =
    {
        Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
        new Vector3(1, 0, 1).normalized, new Vector3(-1, 0, 1).normalized,
        new Vector3(1, 0, -1).normalized, new Vector3(-1, 0, -1).normalized
    };

    public float Exposure(Vector3 position)
    {
        using var timing = PerformanceJournal.Measure(TransitionPhase.TravelExposure, "travel-exposure");
        var key = (Mathf.FloorToInt(position.x / 2f), Mathf.FloorToInt(position.y), Mathf.FloorToInt(position.z / 2f));
        if (_exposure.TryGetValue(key, out var value) && Time.time < value.Until)
        { CacheHits++; return value.Value; }
        CacheMisses++;
        // Chest-height, short-range hard geometry. Foliage cannot pretend to be a solid wall.
        var shelter = 0f;
        foreach (var direction in Directions)
            if (Physics.Raycast(position + Vector3.up * 1.1f, direction, out var hit, 6f,
                    LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                shelter += Mathf.Lerp(.15f, .8f, 1f - hit.distance / 6f);
        var exposure = Mathf.Clamp01(1f - shelter);
        if (!_exposure.ContainsKey(key))
        {
            if (_exposure.Count >= 4096) _exposure.Remove(_order.Dequeue());
            _order.Enqueue(key);
        }
        _exposure[key] = (exposure, Time.time + 30f);
        return exposure;
    }

    public void Covers(Vector3 position, List<Vector3> result)
    {
        using var timing = PerformanceJournal.Measure(TransitionPhase.TravelCoverScan, "travel-covers");
        result.Clear();
        waypoints.CollectTravelCover(position, _covers);
        // Only one candidate will be path-tested in this window. Rank walls ahead of foliage,
        // then proximity, without running CalculatePath for every native cover point.
        var best = -1;
        var score = float.MaxValue;
        for (var i = 0; i < _covers.Count; i++)
        {
            var delta = _covers[i].Position - position;
            if (delta.sqrMagnitude < 9f) continue;
            var candidateScore = delta.sqrMagnitude + (_covers[i].Category == CoverCategory.Soft ? 1000f : 0f);
            if (candidateScore < score) { best = i; score = candidateScore; }
        }
        if (best >= 0 && NavMesh.SamplePosition(_covers[best].Position, out var hit, 1.5f, NavMesh.AllAreas)
            && Mathf.Abs(hit.position.y - _covers[best].Position.y) < 1f) result.Add(hit.position);
    }

    public Vector3[] Path(Vector3 origin, Vector3 target)
    {
        using var timing = PerformanceJournal.Measure(TransitionPhase.TravelPath, "travel-path");
        if (!NavMesh.CalculatePath(origin, target, NavMesh.AllAreas, _path)
            || _path.status != NavMeshPathStatus.PathComplete) return null;
        var corners = _path.corners;
        if (corners.Length < 2 || (corners[0] - origin).sqrMagnitude > .09f
            || (corners[corners.Length - 1] - target).sqrMagnitude > .09f) return null;
        return corners;
    }

    public bool Connects(Vector3 origin, Vector3 target)
        => !NavMesh.Raycast(origin, target, out _, NavMesh.AllAreas);
}

public partial class WaypointSystem
{
    internal void CollectTravelCover(Vector3 center, List<CoverPoint> result)
    {
        result.Clear();
        var data = _botsController?.CoversData;
        if (data == null) return;
        var index = data.GetIndexes(center);
        var voxels = data.GetVoxelesExtended(index.x, index.y, index.z, 3, true);
        var examined = 0;
        for (var i = 0; i < voxels.Count; i++)
        {
            var points = voxels[i].Points;
            for (var p = 0; p < points.Count; p++)
            {
                if (++examined > 256) return;
                var point = points[p];
                var delta = point.Position - center;
                if (delta.sqrMagnitude > 625f || Mathf.Abs(delta.y) > 2f) continue;
                var cover = new CoverPoint(point.Position, point.WallDirection, point.CoverType, point.CoverLevel);
                if (cover.Category != CoverCategory.None) result.Add(cover);
                if (result.Count >= 32) return;
            }
        }
    }
}
