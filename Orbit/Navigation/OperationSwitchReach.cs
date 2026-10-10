using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

internal static class OperationSwitchReach
{
    private const int SurfaceBudget = 8;
    private static readonly Dictionary<Transform, Collider[]> Surfaces = new();

    internal static void Reset() => Surfaces.Clear();

    internal readonly struct Result(bool reachable, string reason, Vector3 point, Collider blocker = null, Collider surface = null)
    {
        internal readonly bool Reachable = reachable;
        internal readonly string Reason = reason;
        internal readonly Vector3 Point = point;
        internal string Diagnostics => $"reason={Reason ?? "not-tested"} point={Point} blocker={(blocker != null ? blocker.name : "none")} layer={(blocker != null ? blocker.gameObject.layer : -1)} surface={(surface != null ? surface.name : "pivot")}";
    }

    internal static Vector3 TargetPosition(WorldInteractiveObject target)
        => target.Collider != null && target.Collider.enabled && target.Collider.gameObject.activeInHierarchy
            ? target.Collider.bounds.center : target.transform.position;

    internal static bool CanReach(Vector3 feet, WorldInteractiveObject target) => Evaluate(feet, target).Reachable;

    internal static Result Evaluate(Vector3 feet, WorldInteractiveObject target)
    {
        if (target == null) return new(false, "missing-target", default);
        var root = target.transform;
        var point = target.GetViewDirection(feet);
        var declared = target.Collider;
        var reader = target is KeycardDoor card && card.DoorState == EDoorState.Locked && card.Proxies?.Length > 0;
        if (reader)
        {
            var cardDoor = (KeycardDoor)target;
            // Use the same proxy selected by the native interaction, not the distant door leaf.
            var view = cardDoor.GetInteractionParameters(feet).ViewTarget;
            InteractiveProxy closest = null;
            var best = float.MaxValue;
            foreach (var proxy in cardDoor.Proxies)
            {
                if (proxy == null || proxy.Link != target) continue;
                var gap = (proxy.GetViewDirection(feet) - view).sqrMagnitude;
                if (gap >= best) continue;
                best = gap; closest = proxy;
            }
            if (closest == null) return new(false, "missing-reader", view);
            root = closest.transform; point = closest.GetViewDirection(feet); declared = null;
        }
        // Keep physics work local. The final limits below use the actual interaction surface.
        if ((feet - point).sqrMagnitude > 36f) return new(false, "too-far", point);
        var head = feet + Vector3.up * 1.4f;
        if (!Surfaces.TryGetValue(root, out var colliders))
        {
            var found = new List<Collider>();
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                // WorldInteractiveObject.Collider only discovers Door-layer children.
                // Levers, buttons and card readers can instead expose an Interactive-layer collider.
                if (collider == null || ((LayersMaskController.InteractiveMask | (1 << LayersMaskController.DoorLayer))
                    & (1 << collider.gameObject.layer)) == 0) continue;
                found.Add(collider);
            }
            Surfaces[root] = colliders = found.ToArray();
        }

        var remaining = SurfaceBudget;
        var result = new Result(false, "inactive-collider", point);
        if (declared != null)
        {
            result = CheckCollider(feet, head, point, target, declared, reader, ref remaining);
            if (result.Reachable) return result;
        }
        foreach (var collider in colliders)
        {
            if (collider == null || collider == declared) continue;
            var owner = collider.GetComponentInParent<WorldInteractiveObject>();
            var proxy = collider.GetComponentInParent<InteractiveProxy>();
            if (reader ? proxy?.Link != target : owner != target)
            { result = new(false, "surface-owner", point, surface: collider); continue; }
            var candidate = CheckCollider(feet, head, point, target, collider, reader, ref remaining);
            if (candidate.Reachable) return candidate;
            if (candidate.Reason != "inactive-collider") result = candidate;
            if (remaining <= 0) break;
        }
        // Only objects with no registered interaction geometry use a checked view-point fallback.
        // Never fall back through a wall after rejecting a real interaction surface.
        return declared == null && colliders.Length == 0
            ? CheckSegment(feet, head, point, target, null, reader) : result;
    }

    private static Result CheckCollider(Vector3 feet, Vector3 head, Vector3 view, WorldInteractiveObject target,
        Collider collider, bool reader, ref int remaining)
    {
        if (!collider.enabled || !collider.gameObject.activeInHierarchy)
            return new(false, "inactive-collider", view, surface: collider);
        var center = collider.bounds.center;
        var result = new Result(false, "no-collider-surface", view, surface: collider);
        // The native view point often lies beside the handle. Try the collider centre as well.
        for (var index = 0; index < 2 && remaining > 0; index++)
        {
            if (index == 1 && (view - center).sqrMagnitude < .0001f) break;
            remaining--;
            var direction = (index == 0 ? view : center) - head;
            if (direction.sqrMagnitude < .000001f
                || !collider.Raycast(new Ray(head, direction.normalized), out var hit, direction.magnitude + .1f)) continue;
            result = CheckSegment(feet, head, hit.point, target, collider, reader);
            if (result.Reachable) return result;
        }
        return result;
    }

    private static Result CheckSegment(Vector3 feet, Vector3 head, Vector3 point, WorldInteractiveObject target,
        Collider collider, bool reader)
    {
        if (Mathf.Abs(feet.y - point.y) > 2f) return new(false, "height", point);
        if ((feet - point).sqrMagnitude > 16f) return new(false, "reach-distance", point);
        var delta = point - head;
        var distance = delta.magnitude;
        if (distance < .001f) return new(true, "at-target", point, surface: collider);
        if (!reader && !target.InteractsFromAppropriateDirection(delta / distance))
            return new(false, "interaction-side", point, surface: collider);
        var mask = LayersMaskController.HighPolyWithTerrainMask | (1 << LayersMaskController.DoorLayer);
        if (!Physics.Raycast(head, delta / distance, out var hit, distance, mask, QueryTriggerInteraction.Ignore))
            return new(true, "clear", point, surface: collider);
        if (hit.collider != null && (hit.collider == collider
            || !reader && hit.collider.GetComponentInParent<WorldInteractiveObject>() == target
            || reader && hit.collider.GetComponentInParent<InteractiveProxy>()?.Link == target))
            return new(true, "target-collider", point, surface: collider);
        return new(false, "obstructed", point, hit.collider, collider);
    }
}
