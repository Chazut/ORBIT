using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

/// <summary>Physical loot access uses the object, never its sampled navigation point.</summary>
internal static class LootVisibility
{
    internal static Vector3 Target(InteractableObject target)
    {
        if (target is LootableContainer container && container.Collider != null)
            return container.Collider.bounds.center;
        // A corpse's root remains at the death position while its pelvis can move with the ragdoll.
        var track = target.TrackableTransform;
        return (track != null ? track.position : target.transform.position) + Vector3.up * .1f;
    }

    internal static bool Check(Vector3 head, InteractableObject target, out RaycastHit hit)
    {
        hit = default;
        if (target == null) return false;
        var direction = Target(target) - head;
        var distance = direction.magnitude;
        if (distance < .01f) return true;
        if (!Physics.Raycast(head, direction / distance, out hit, distance,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return true;
        var obstacle = hit.collider != null ? hit.collider.transform : null;
        return obstacle != null && (obstacle == target.transform || obstacle.IsChildOf(target.transform));
    }
}

// Shared by every loot continuation, including equipment swaps. Never retain Unity objects.
internal sealed class LootAccessGuard
{
    private int checkedFrame = -1;
    private bool consumed;
    internal void Reset() { checkedFrame = -1; consumed = false; }
    // A successful loose-item transaction removes its world object. Only completion may
    // bypass further visibility checks; cancellation and session ownership still apply.
    internal void CompleteLoosePickup(bool succeeded) { if (succeeded) consumed = true; }
    internal bool Check(int frame, bool dormant, Vector3 head, InteractableObject target, out RaycastHit hit)
    {
        hit = default;
        if (dormant || consumed) return true;
        if (target == null) return false;
        if (checkedFrame == frame) return true;
        if (!LootVisibility.Check(head, target, out hit)) return false;
        checkedFrame = frame;
        return true;
    }
}
