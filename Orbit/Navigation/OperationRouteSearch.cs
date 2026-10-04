using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// One bounded search per operation. A partial path is only an approach leg, never permission to interact.
internal sealed class OperationRouteSearch
{
    private readonly NavMeshPath _path = new();
    private int _candidate;
    private Vector3 _approach;
    private float _approachGap = float.MaxValue;
    internal bool Pending { get; private set; }
    internal int Samples { get; private set; }
    internal int PartialPaths { get; private set; }
    internal int InvalidPaths { get; private set; }
    private const int LocalCandidates = 20;
    private const int TotalCandidates = LocalCandidates + 3;

    internal void Reset()
    {
        _candidate = Samples = PartialPaths = InvalidPaths = 0;
        _approachGap = float.MaxValue;
        Pending = false;
    }

    internal bool Find(Vector3 origin, Vector3 target, out Vector3 point, out bool final)
    {
        point = default;
        final = false;
        Pending = false;
        var work = 0;
        while (_candidate < TotalCandidates && work < 3)
        {
            work++;
            var index = _candidate++;
            var local = index < LocalCandidates;
            Vector3 sample;
            if (index < 4) sample = target + Vector3.down * (index * .6f);
            else if (local)
            {
                var angle = (index - 4) % 8 * Mathf.PI / 4f;
                var radius = index < 12 ? 1.25f : 2.25f;
                sample = target + new Vector3(Mathf.Cos(angle) * radius, -1f, Mathf.Sin(angle) * radius);
            }
            else
            {
                // Walk connected ground toward a distant room when its interior cannot yet be resolved.
                var distance = Vector3.Distance(origin, target);
                if (distance < 20f) continue;
                var length = index == LocalCandidates ? 80f : index == LocalCandidates + 1 ? 40f : 20f;
                sample = Vector3.MoveTowards(origin, target, Mathf.Min(length, distance * .65f));
                sample.y = origin.y;
            }
            if (!NavMesh.SamplePosition(sample, out var hit, local ? 1.25f : 3f, NavMesh.AllAreas)) continue;
            if (local && (Mathf.Abs(hit.position.y - target.y) > 2.5f
                || HorizontalDistance(hit.position, target) > 3f)) continue;
            Samples++;
            if (!NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, _path)
                || _path.status == NavMeshPathStatus.PathInvalid) { InvalidPaths++; continue; }
            if (_path.status == NavMeshPathStatus.PathComplete && local)
            { point = hit.position; final = true; return true; }
            if (_path.status == NavMeshPathStatus.PathPartial) PartialPaths++;
            var corners = _path.corners;
            if (corners == null || corners.Length < 2) continue;
            var end = corners[corners.Length - 1];
            var gap = Vector3.Distance(end, target);
            // Require actual, useful progress; a wall endpoint beside the bot cannot create a retry loop.
            if (Vector3.Distance(origin, end) < 5f || gap > Vector3.Distance(origin, target) - 3f
                || gap >= _approachGap) continue;
            _approach = end;
            _approachGap = gap;
        }
        if (_candidate < TotalCandidates) { Pending = true; return false; }
        if (_approachGap == float.MaxValue) return false;
        if (work >= 3) { Pending = true; return false; }
        // Verify the leg independently, including a partial path's last corner.
        if (NavMesh.CalculatePath(origin, _approach, NavMesh.AllAreas, _path)
            && _path.status == NavMeshPathStatus.PathComplete)
        { point = _approach; return true; }
        InvalidPaths++;
        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    { var delta = a - b; delta.y = 0; return delta.magnitude; }
}
