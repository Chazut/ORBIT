using System.Collections.Generic;
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
    private const int DirectCandidates = LocalCandidates + 3;
    private const int ExpandedCandidates = 42;

    internal void Reset()
    {
        _candidate = Samples = PartialPaths = InvalidPaths = 0;
        _approachGap = float.MaxValue;
        Pending = false;
    }

    internal bool Find(Vector3 origin, Vector3 target, out Vector3 point, out bool final,
        int expansion = 0, IReadOnlyList<Vector3> avoided = null, bool floorAware = false, float finalHeightTolerance = 2.5f)
    {
        point = default;
        final = false;
        Pending = false;
        expansion = Mathf.Clamp(expansion, 0, 2);
        // Vertical objectives need access routes on several floors, including stairs behind us.
        // Enumeration is still resumed in slices of at most three navigation calls.
        var expandedCandidates = floorAware ? 128 : ExpandedCandidates;
        var totalCandidates = DirectCandidates + expansion * expandedCandidates;
        var work = 0;
        while (_candidate < totalCandidates && work < 3)
        {
            work++;
            var index = _candidate++;
            var local = index < LocalCandidates;
            var sampleRadius = local ? 1.25f : 3f;
            Vector3 sample;
            if (index < 4) sample = target + Vector3.down * (index * .6f);
            else if (local)
            {
                var angle = (index - 4) % 8 * Mathf.PI / 4f;
                var radius = index < 12 ? 1.25f : 2.25f;
                sample = target + new Vector3(Mathf.Cos(angle) * radius, -1f, Mathf.Sin(angle) * radius);
            }
            else if (index < DirectCandidates)
            {
                // Walk connected ground toward a distant room when its interior cannot yet be resolved.
                var distance = Vector3.Distance(origin, target);
                if (distance < 20f) continue;
                var length = index == LocalCandidates ? 80f : index == LocalCandidates + 1 ? 40f : 20f;
                sample = Vector3.MoveTowards(origin, target, Mathf.Min(length, distance * .65f));
                sample.y = origin.y;
            }
            else if (floorAware)
            {
                var probe = index - DirectCandidates;
                var pass = probe / expandedCandidates;
                var slot = probe % expandedCandidates;
                var heading = slot % 16;
                var angle = heading * Mathf.PI / 8f;
                var length = (slot / 16 % 2 == 0 ? 20f : 40f) * (pass + 1);
                var direction = target - origin; direction.y = 0;
                if (direction.sqrMagnitude < .01f) direction = new Vector3(1, 0, 0);
                direction = direction.normalized;
                sample = origin + new Vector3(direction.x * Mathf.Cos(angle) - direction.z * Mathf.Sin(angle), 0,
                    direction.x * Mathf.Sin(angle) + direction.z * Mathf.Cos(angle)) * length;
                sample.y = origin.y + (target.y - origin.y) * (slot / 32 / 3f);
                sampleRadius = pass == 0 ? 3f : 5f;
            }
            else
            {
                // Broaden failed Rush approaches across terrain and around obstacles. These are
                // intermediate destinations only; the real room/door still needs a local route.
                var probe = index - DirectCandidates;
                var pass = probe / ExpandedCandidates;
                var slot = probe % ExpandedCandidates;
                var heading = slot % 7;
                var angle = ((heading + 1) / 2) * (heading % 2 == 0 ? -1 : 1) * Mathf.PI / 6f;
                var distance = Vector3.Distance(origin, target);
                var length = Mathf.Min((slot / 7 % 2 == 0 ? 40f : 80f) * (pass + 1), Mathf.Max(10f, distance * .65f));
                var direction = target - origin; direction.y = 0;
                direction = direction.normalized;
                sample = origin + new Vector3(direction.x * Mathf.Cos(angle) - direction.z * Mathf.Sin(angle), 0,
                    direction.x * Mathf.Sin(angle) + direction.z * Mathf.Cos(angle)) * length;
                var height = slot / 14;
                sample.y = height == 0 ? origin.y : origin.y + (target.y - origin.y) * Mathf.Min(1f, length / Mathf.Max(1f, distance))
                    + (height == 1 ? -8f : 8f);
                sampleRadius = pass == 0 ? 6f : 12f;
            }
            if (!NavMesh.SamplePosition(sample, out var hit, sampleRadius, NavMesh.AllAreas)) continue;
            if (floorAware && !local && Mathf.Abs(hit.position.y - sample.y) > 2.5f) continue;
            if (local && (Mathf.Abs(hit.position.y - target.y) > finalHeightTolerance
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
            var originGap = Vector3.Distance(origin, target);
            // Require actual, useful progress; a wall endpoint beside the bot cannot create a retry loop.
            var score = gap + (floorAware ? Mathf.Abs(end.y - target.y) * 2f : 0);
            if (Vector3.Distance(origin, end) < 5f || score >= _approachGap) continue;
            var detour = floorAware && expansion > 0 ? expansion * 60f : expansion >= 2 ? 20f : -3f;
            if (gap > originGap + detour) continue;
            if (Visited(end, avoided)) continue;
            _approach = end;
            _approachGap = score;
        }
        if (_candidate < totalCandidates) { Pending = true; return false; }
        if (_approachGap == float.MaxValue) return false;
        if (work >= 3) { Pending = true; return false; }
        // Verify the leg independently, including a partial path's last corner.
        if (NavMesh.CalculatePath(origin, _approach, NavMesh.AllAreas, _path)
            && _path.status == NavMeshPathStatus.PathComplete)
        { point = _approach; return true; }
        InvalidPaths++;
        return false;
    }

    internal static void Remember(List<Vector3> history, Vector3 point)
    {
        if (Visited(point, history)) return;
        if (history.Count >= 64) history.RemoveAt(0);
        history.Add(point);
    }

    private static bool Visited(Vector3 point, IReadOnlyList<Vector3> avoided)
    {
        if (avoided == null) return false;
        for (var i = 0; i < avoided.Count; i++)
            if ((point - avoided[i]).sqrMagnitude < 25f) return true;
        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    { var delta = a - b; delta.y = 0; return delta.magnitude; }
}
