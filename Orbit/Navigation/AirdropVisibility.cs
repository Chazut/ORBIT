using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

/// <summary>Use the crate, not its ground navigation point, for discovery and arrival sight.</summary>
internal static class AirdropVisibility
{
    internal static Vector3 Target(LootableContainer container) => container.transform.position + Vector3.up;

    internal static bool Check(Vector3 head, LootableContainer container, out RaycastHit hit)
    {
        hit = default;
        if (container == null) return false;
        var direction = Target(container) - head;
        var distance = direction.magnitude;
        if (distance < .01f) return true;
        if (!Physics.Raycast(head, direction / distance, out hit, distance,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return true;

        // The nearest hit must belong to this crate. Never accept its parent or siblings,
        // which can include unrelated level geometry or another container.
        var obstacle = hit.collider != null ? hit.collider.transform : null;
        return obstacle != null && (obstacle == container.transform || obstacle.IsChildOf(container.transform));
    }
}
