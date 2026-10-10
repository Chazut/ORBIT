using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

internal static class AmbushCoverProbe
{
    // Cover the disk, rather than eight small patches on a single ring.
    internal static bool Sample(Vector3 center, float radius, int index, out Vector3 point)
    {
        point = center;
        if (index == 0) return true;
        index--;
        var rings = Mathf.CeilToInt(radius / 18f);
        for (var ring = 1; ring <= rings; ring++)
        {
            var r = Mathf.Min(radius, ring * 18f);
            var count = Mathf.CeilToInt(2f * Mathf.PI * r / 18f);
            if (index >= count) { index -= count; continue; }
            var angle = (index + (ring % 2) * .5f) * 2f * Mathf.PI / count;
            point += new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * r;
            return true;
        }
        return false;
    }

    internal static bool Geometry(Vector3 sample, int heading, out CoverPoint cover)
    {
        cover = default;
        if (!NavMesh.SamplePosition(sample, out var ground, 8f, NavMesh.AllAreas)) return false;
        var angle = heading * Mathf.PI / 2f;
        var direction = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
        var mask = LayersMaskController.HighPolyWithTerrainMask | LayersMaskController.Foliage;
        if (!Physics.Raycast(ground.position + Vector3.up, direction, out var hit, 14f, mask, QueryTriggerInteraction.Collide)
            || hit.collider == null || hit.normal.sqrMagnitude < .5f || Mathf.Abs(hit.normal.y) > .6f) return false;
        var feet = hit.point + hit.normal * 1f;
        feet.y = ground.position.y;
        if (!NavMesh.SamplePosition(feet, out var stand, .75f, NavMesh.AllAreas)
            || Mathf.Abs(stand.position.y - feet.y) > .5f) return false;
        var soft = ((1 << hit.collider.gameObject.layer) & LayersMaskController.Foliage) != 0;
        cover = new CoverPoint(stand.position, hit.normal, soft ? CoverCategory.Soft : CoverCategory.Hard, CoverLevel.Sit);
        return true;
    }
}
