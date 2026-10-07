using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

internal static class ExfilNavigation
{
    // Loading-only fallback for triggers whose pivot/interior has no NavMesh. Keep
    // the nearest approach on the same level; runtime still checks route and entry.
    internal static bool TryApproachPoint(ExfiltrationPoint exfil, out Vector3 position)
    {
        position = default;
        var collider = exfil.GetComponent<Collider>();
        if (collider == null || collider.bounds.size.sqrMagnitude < .001f) return false;
        var bounds = collider.bounds;
        var best = float.MaxValue;
        for (var point = 0; point < 25; point++)
        {
            var probe = new Vector3(bounds.center.x + (point % 5 - 2) * bounds.extents.x * .5f,
                bounds.min.y + Mathf.Min(.5f, bounds.extents.y),
                bounds.center.z + (point / 5 - 2) * bounds.extents.z * .5f);
            if (!NavMesh.SamplePosition(probe, out var hit, 15f, NavMesh.AllAreas)) continue;
            var edge = collider.ClosestPoint(hit.position);
            if (Mathf.Abs(hit.position.y - probe.y) > 2.5f || DangerZones.IsInside(hit.position)) continue;
            var distance = (hit.position - edge).sqrMagnitude;
            if (distance > 225f || distance >= best) continue;
            best = distance; position = hit.position;
        }
        return best < float.MaxValue;
    }

    // Only called while gathering exits, not per bot or per frame. Broad samples can pick the
    // roof above a bunker; every candidate here must belong to the actual trigger volume.
    internal static bool TryInteriorPoint(ExfiltrationPoint exfil, out Vector3 position)
    {
        position = default;
        var collider = exfil.GetComponent<Collider>();
        if (collider == null) return false;
        var bounds = collider.bounds;
        if (bounds.size.sqrMagnitude < 0.001f) return false;
        for (var layer = 0; layer < 3; layer++)
        {
            var y = layer == 0 ? bounds.min.y + Mathf.Min(0.2f, bounds.extents.y)
                : layer == 1 ? bounds.min.y + Mathf.Min(0.75f, bounds.extents.y) : bounds.center.y;
            for (var point = 0; point < 9; point++)
            {
                // Centre first, then a bounded grid away from the trigger's edges.
                var grid = point == 0 ? 4 : point <= 4 ? point - 1 : point;
                var x = grid % 3 - 1;
                var z = grid / 3 - 1;
                var probe = new Vector3(bounds.center.x + x * bounds.extents.x * 0.7f, y,
                    bounds.center.z + z * bounds.extents.z * 0.7f);
                if (!NavMesh.SamplePosition(probe, out var hit, 1f, NavMesh.AllAreas)) continue;
                if (!bounds.Contains(hit.position) || (collider.ClosestPoint(hit.position) - hit.position).sqrMagnitude > 0.0001f) continue;
                position = hit.position;
                return true;
            }
        }
        return false;
    }
}
