using UnityEngine;
using UnityEngine.AI;
using EFT.Interactive;

namespace Orbit.Navigation;

// Recovery only, after travel to the live pelvis has actually stopped or stalled.
// Check at most one candidate path per update. Walking still uses the normal movement queue.
internal static class CorpseApproach
{
    internal const float Preferred = .85f;
    internal const float PreferredSqr = Preferred * Preferred;
    internal const float Limit = 3f;
    internal const float LimitSqr = Limit * Limit;
    internal const float SearchRadiusSqr = 5f * 5f;
    private static readonly Vector3[] directions =
    {
        new(1,0,0), new(-1,0,0), new(0,0,1), new(0,0,-1),
        new(.707f,0,.707f), new(-.707f,0,.707f), new(.707f,0,-.707f), new(-.707f,0,-.707f)
    };

    internal static bool PreferProne(bool pmc, bool playerScav, bool alreadyProne, bool clearance)
        => (pmc || playerScav) && (alreadyProne || clearance);

    internal static int Samples => 1 + 2 * directions.Length;
    private static NavMeshPath reachPath;

    internal static void ClearRaid() => reachPath = null;

    internal static bool HasCompletePath(Vector3 feet, Vector3 stand)
    {
        reachPath ??= new NavMeshPath();
        return NavMesh.CalculatePath(feet, stand, NavMesh.AllAreas, reachPath)
            && reachPath.status == NavMeshPathStatus.PathComplete;
    }

    internal static bool WithinLootReach(float distanceSqr, float approvedReachSqr)
        => distanceSqr <= Mathf.Min(LimitSqr, Mathf.Max(PreferredSqr, approvedReachSqr));

    internal static bool TryStand(int index, Vector3 feet, Vector3 body, Corpse corpse, Vector3? lastStand, out Vector3 stand)
    {
        var offset = index == 0 ? default : directions[(index - 1) % directions.Length]
            * (index <= directions.Length ? .65f : 1.7f);
        stand = default;
        if (!NavMesh.SamplePosition(body + offset, out var hit, 1f, NavMesh.AllAreas)) return false;
        // Only issue a real, closer step. A navmesh edge often snaps several seeds to the same spot.
        var improvement = (body - feet).magnitude - (body - hit.position).magnitude;
        if (improvement < .2f || (hit.position - feet).sqrMagnitude < .2f * .2f
            || lastStand.HasValue && (hit.position - lastStand.Value).sqrMagnitude < .2f * .2f
            || !LootVisibility.Check(hit.position + Vector3.up, corpse, out _)) return false;
        if (!HasCompletePath(feet, hit.position)) return false;
        stand = hit.position;
        return true;
    }
}
