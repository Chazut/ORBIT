using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

internal static class LockedDoorPath
{
    // Test the actual door leaf, including disabled box colliders. A shared open carver can
    // make navigation complete while the physical door is still locked for this squad.
    internal static bool Crosses(Door door, Vector3[] corners)
    {
        var collider = door.Collider;
        if (collider == null || corners == null) return false;
        var box = collider as BoxCollider;
        var bounds = box != null ? new Bounds(box.center, box.size) : collider.bounds;
        for (var i = 1; i < corners.Length; i++)
        {
            var start = corners[i - 1] + Vector3.up * .8f;
            var end = corners[i] + Vector3.up * .8f;
            if (box != null)
            {
                start = box.transform.InverseTransformPoint(start);
                end = box.transform.InverseTransformPoint(end);
            }
            var delta = end - start;
            if (delta.sqrMagnitude < .0001f) continue;
            if (bounds.Contains(start) || bounds.IntersectRay(new Ray(start, delta.normalized), out var distance)
                && distance <= delta.magnitude) return true;
        }
        return false;
    }
}
