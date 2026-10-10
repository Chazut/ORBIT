using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// Optional rescue hints, never a movement script. Every proposed leg needs a live complete path.
internal sealed class RecordedApproach
{
    private readonly NavMeshPath _path = new();
    private int _route, _point;
    private float _best = float.MaxValue;
    private Vector3 _chosen;
    internal bool Pending { get; private set; }
    internal Vector3[] RouteCorners { get; private set; }

    internal void Reset()
    { _route = _point = 0; _best = float.MaxValue; Pending = false; RouteCorners = null; }

    internal bool Find(Vector3 origin, Vector3 target, IReadOnlyList<Vector3> visited, out Vector3 chosen)
    {
        chosen = default;
        Pending = false;
        // These observations cover only the D2 gate corridor. Relocated variants use autonomous routing.
        if (Vector3.Distance(target, new Vector3(-116.3f, -18.4f, 169.3f)) > 25f
            || !OperationRoutePolicy.InD2Bunker(origin)) return false;
        var work = 0;
        while (_route < D2TransitHints.Paths.Length && work < 3)
        {
            var route = D2TransitHints.Paths[_route];
            if (_point >= route.Length) { _route++; _point = 0; continue; }
            var i = _point++;
            var seed = route[i];
            work++;
            if (Vector3.Distance(origin, seed) < 3f || Vector3.Distance(origin, seed) > 160f) continue;
            var known = false;
            if (visited != null)
                foreach (var old in visited) if ((seed - old).sqrMagnitude < 9f) { known = true; break; }
            if (known || !NavMesh.SamplePosition(seed, out var hit, 1f, NavMesh.AllAreas)
                || Mathf.Abs(hit.position.y - seed.y) > .75f || !OperationRoutePolicy.InD2Bunker(hit.position)) continue;
            if (!NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, _path)
                || _path.status != NavMeshPathStatus.PathComplete) continue;
            var corners = _path.corners;
            if (corners == null || corners.Length < 2 || Vector3.Distance(corners[corners.Length - 1], hit.position) > 1f
                || !OperationRoutePolicy.AllowsPath(corners, OperationRoutePolicy.D2)) continue;
            var remaining = Vector3.Distance(route[route.Length - 1], target);
            for (var next = i + 1; next < route.Length; next++) remaining += Vector3.Distance(route[next - 1], route[next]);
            // Select the furthest reachable transit point toward the gate, not a detour back to the entrance.
            if (remaining >= _best) continue;
            _best = remaining; _chosen = hit.position; RouteCorners = corners;
        }
        if (_route < D2TransitHints.Paths.Length) { Pending = true; return false; }
        if (RouteCorners == null) return false;
        if (work >= 3) { Pending = true; return false; }
        if (!NavMesh.CalculatePath(origin, _chosen, NavMesh.AllAreas, _path)
            || _path.status != NavMeshPathStatus.PathComplete || !OperationRoutePolicy.AllowsPath(_path.corners, OperationRoutePolicy.D2))
            return false;
        RouteCorners = _path.corners;
        chosen = _chosen;
        return true;
    }
}
