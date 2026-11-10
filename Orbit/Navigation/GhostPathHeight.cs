using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// Long NavMesh segments describe a route, not the terrain height between corners.
// Follow only the local surface below the same horizontal point, within the wake floor band.
internal static class GhostPathHeight
{
    private static int frame = -1, probes;
    internal static Vector3 Project(Vector3 point, float now, int currentFrame, ref float nextCheck)
    {
        if (now < nextCheck) return point;
        if (currentFrame != frame) { frame = currentFrame; probes = 0; }
        if (probes >= 8) return point;
        probes++; nextCheck = now + .2f;
        if (!NavMesh.SamplePosition(point, out var hit, .65f, NavMesh.AllAreas)) return point;
        var delta = hit.position - point;
        if (float.IsNaN(delta.sqrMagnitude) || float.IsInfinity(delta.sqrMagnitude) || Mathf.Abs(delta.y) > .65f
            || delta.x * delta.x + delta.z * delta.z > .0025f) return point;
        point.y = hit.position.y;
        return point;
    }
}
