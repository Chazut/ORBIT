using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

internal static class OperationSwitchReach
{
    internal readonly struct Result(bool reachable, string reason, Vector3 point, Collider blocker = null)
    {
        internal readonly bool Reachable = reachable;
        internal readonly string Reason = reason;
        internal readonly Vector3 Point = point;
        internal string Diagnostics => $"reason={Reason ?? "not-tested"} point={Point} blocker={(blocker != null ? blocker.name : "none")} layer={(blocker != null ? blocker.gameObject.layer : -1)}";
    }

    internal static Vector3 TargetPosition(WorldInteractiveObject target)
        => target.Collider != null && target.Collider.enabled && target.Collider.gameObject.activeInHierarchy
            ? target.Collider.bounds.center : target.transform.position;

    internal static bool CanReach(Vector3 feet, WorldInteractiveObject target) => Evaluate(feet, target).Reachable;

    internal static Result Evaluate(Vector3 feet, WorldInteractiveObject target)
    {
        if (target == null) return new(false, "missing-target", default);
        var point = TargetPosition(target);
        // Keep physics work local. The final limits below use the actual interaction surface.
        if ((feet - point).sqrMagnitude > 36f) return new(false, "too-far", point);
        var head = feet + Vector3.up * 1.4f;
        var collider = target.Collider;
        if (collider != null)
        {
            if (!collider.enabled || !collider.gameObject.activeInHierarchy)
                return new(false, "inactive-collider", point);
            var direction = point - head;
            // Raycast the interactive collider itself, including trigger and non-convex meshes.
            // A switch pivot can sit behind its cabinet: stop at the exposed surface instead.
            if (direction.sqrMagnitude < .000001f
                || !collider.Raycast(new Ray(head, direction.normalized), out var surface, direction.magnitude + .1f))
                return new(false, "no-collider-surface", point);
            point = surface.point;
        }
        if (Mathf.Abs(feet.y - point.y) > 2f) return new(false, "height", point);
        if ((feet - point).sqrMagnitude > 16f) return new(false, "reach-distance", point);
        var delta = point - head;
        var distance = delta.magnitude;
        if (distance < .001f) return new(true, "at-target", point);
        var mask = LayersMaskController.HighPolyWithTerrainMask | (1 << LayersMaskController.DoorLayer);
        if (!Physics.Raycast(head, delta / distance, out var hit, distance, mask, QueryTriggerInteraction.Ignore))
            return new(true, "clear", point);
        if (hit.collider != null && (hit.collider == collider || hit.collider.GetComponentInParent<WorldInteractiveObject>() == target))
            return new(true, "target-collider", point);
        return new(false, "obstructed", point, hit.collider);
    }
}
