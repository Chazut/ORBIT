using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

internal static class OperationSwitchReach
{
    // Local recovery for wall-mounted switches whose final NavMesh approach is incomplete.
    internal static bool CanReach(Vector3 feet, Switch target)
    {
        if (target == null) return false;
        var point = target.transform.position;
        if ((feet - point).sqrMagnitude > 16f || Mathf.Abs(feet.y - point.y) > 2f) return false;
        var head = feet + Vector3.up * 1.4f;
        var delta = point - head;
        var distance = delta.magnitude;
        if (distance < .1f) return true;
        if (!Physics.Raycast(head, delta / distance, out var hit, distance,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return true;
        // The lever's own collider is a valid hit. Walls and other floors are not.
        return hit.collider != null && hit.collider.GetComponentInParent<Switch>() == target;
    }
}
