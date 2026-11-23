using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

/// <summary>Physical loot access uses the object, never its sampled navigation point.</summary>
internal static class LootVisibility
{
    // Unity-thread scratch storage. Clear after use so scene objects never survive a raid here.
    private static readonly List<LootableContainer> propContainers = new(2);
    internal static Vector3 Target(InteractableObject target)
    {
        if (target is LootableContainer container && container.Collider != null)
            return container.Collider.bounds.center;
        // A corpse's root remains at the death position while its pelvis can move with the ragdoll.
        return BodyPosition(target) + Vector3.up * .1f;
    }

    internal static Vector3 BodyPosition(InteractableObject target)
    {
        var track = target.TrackableTransform;
        return track != null ? track.position : target.transform.position;
    }

    internal static bool Check(Vector3 head, InteractableObject target, out RaycastHit hit)
    {
        hit = default;
        if (target == null) return false;
        if (target is LootableContainer crate && AirdropVisibility.IsRegistered(crate))
            return AirdropVisibility.Check(head, crate, out hit);
        var direction = Target(target) - head;
        var distance = direction.magnitude;
        if (distance < .01f) return true;
        if (!Physics.Raycast(head, direction / distance, out hit, distance,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return true;
        var obstacle = hit.collider != null ? hit.collider.transform : null;
        if (obstacle == null) return false;
        if (obstacle == target.transform || obstacle.IsChildOf(target.transform)) return true;
        return IsContainerShell(target, obstacle);
    }

    // Some props put the interaction box and the ballistic mesh on sibling objects.
    // The sports bag uses this layout. Limit ownership to its immediate LOD prop,
    // never a map/room ancestor or a collider name shared by every bag in the scene.
    internal static bool IsContainerShell(InteractableObject target, Transform obstacle)
    {
        if (target is not LootableContainer || obstacle == null) return false;
        var prop = target.transform.parent;
        if (prop == null || prop.GetComponent<LODGroup>() == null || !obstacle.IsChildOf(prop)) return false;
        try
        {
            prop.GetComponentsInChildren(true, propContainers);
            return propContainers.Count == 1 && propContainers[0] == target;
        }
        finally { propContainers.Clear(); }
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
