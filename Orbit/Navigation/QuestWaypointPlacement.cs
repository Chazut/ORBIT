using System;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

internal static class QuestWaypointPlacement
{
    // Long Line uses these InZone conditions to count PMC kills. Visiting their geometric
    // centres is not a quest action and can send a bot below the shops in a tall volume.
    internal static bool IsEliminationZone(string id)
        => id != null && id.StartsWith("place_merch_022_", StringComparison.Ordinal);

    internal static bool TryFind(Vector3 origin, Collider volume, out Vector3 point)
    {
        point = default;
        if (volume == null)
        {
            if (!NavMesh.SamplePosition(origin, out var near, 2f, NavMesh.AllAreas)
                || Mathf.Abs(near.position.y - origin.y) > 1.5f) return false;
            point = near.position;
            return !DangerZones.IsInside(point);
        }
        var bounds = volume.bounds;
        // Search bounded local samples inside the real volume. A wide 10m snap can cross
        // the ceiling into another floor; containment must survive the NavMesh projection.
        var levels = Mathf.Clamp(Mathf.CeilToInt(bounds.size.y / 2f), 1, 16);
        for (var level = 0; level <= levels; level++)
        {
            var sample = bounds.center;
            sample.y = level == 0 ? origin.y : bounds.min.y + (level - .5f) * bounds.size.y / levels;
            for (var offset = 0; offset < 5; offset++)
            {
                var probe = sample;
                if (offset == 1) probe.x -= bounds.extents.x * .5f;
                if (offset == 2) probe.x += bounds.extents.x * .5f;
                if (offset == 3) probe.z -= bounds.extents.z * .5f;
                if (offset == 4) probe.z += bounds.extents.z * .5f;
                if (!NavMesh.SamplePosition(probe, out var nav, 1.25f, NavMesh.AllAreas)
                    || Mathf.Abs(nav.position.y - probe.y) > 1.25f
                    || !Contains(volume, nav.position) || DangerZones.IsInside(nav.position)) continue;
                point = nav.position;
                return true;
            }
        }
        return false;
    }

    internal static bool Contains(Collider volume, Vector3 feet)
    {
        // Quest triggers register the player's collider, whose centre is above the feet.
        var body = feet + Vector3.up * .75f;
        return (volume.ClosestPoint(body) - body).sqrMagnitude < .01f;
    }
}
