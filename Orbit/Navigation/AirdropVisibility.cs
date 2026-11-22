using System.Runtime.CompilerServices;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

/// <summary>Use the crate, not its ground navigation point, for discovery and arrival sight.</summary>
internal static class AirdropVisibility
{
    private static readonly ConditionalWeakTable<LootableContainer, Transform> Crates = new();

    internal static void Register(LootableContainer container, Transform crateRoot)
    {
        if (container == null || crateRoot == null || !container.transform.IsChildOf(crateRoot)) return;
        Crates.Remove(container);
        Crates.Add(container, crateRoot);
    }

    internal static bool IsRegistered(LootableContainer container) => Crates.TryGetValue(container, out _);

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

        // The interaction box and the wooden shell are siblings under the landing object.
        var obstacle = hit.collider != null ? hit.collider.transform : null;
        return obstacle != null && (obstacle.IsChildOf(container.transform)
            || Crates.TryGetValue(container, out var root) && root != null && obstacle.IsChildOf(root));
    }
}
