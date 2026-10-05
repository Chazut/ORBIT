using Orbit.Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// Ranking does not require a distant destination to be reachable in one path. Each call
// checks one route; an unfinished tail is an estimate, never permission to interact.
internal sealed class RushRouteEstimate
{
    private readonly NavMeshPath _path = new();
    private int _sample;
    private float _best = float.MaxValue;
    private string _source;

    internal void Reset() { _sample = 0; _best = float.MaxValue; _source = null; }

    internal bool Step(Vector3 origin, Vector3 target, out float cost, out string source)
    {
        cost = Vector3.Distance(origin, target);
        source = "distance estimate";
        var direct = _sample == 0;
        var sample = target;
        if (!direct)
        {
            var length = _sample == 1 ? 80f : _sample == 2 ? 40f : 20f;
            sample = Vector3.MoveTowards(origin, target, Mathf.Min(length, cost * .65f));
            sample.y = origin.y;
        }
        _sample++;
        if (NavMesh.SamplePosition(sample, out var hit, 3f, NavMesh.AllAreas)
            && (!direct || Mathf.Abs(hit.position.y - target.y) <= 2.5f)
            && NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, _path)
            && _path.status != NavMeshPathStatus.PathInvalid)
        {
            var corners = _path.corners;
            if (corners != null && corners.Length >= 2)
            {
                var length = PathHelper.TotalLength(corners);
                if (direct && _path.status == NavMeshPathStatus.PathComplete)
                { cost = length; source = "complete path"; return true; }
                var end = corners[corners.Length - 1];
                var gap = Vector3.Distance(end, target);
                if (Vector3.Distance(origin, end) >= 5f && gap <= cost - 3f && length + gap < _best)
                { _best = length + gap; _source = direct ? "partial path estimate" : "staged approach estimate"; }
            }
        }
        if (_sample < 4) return false;
        if (_best < float.MaxValue) { cost = _best; source = _source; }
        return true;
    }
}
