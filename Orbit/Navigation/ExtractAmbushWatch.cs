using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

internal static class ExtractAmbushWatch
{
    // Straight ahead, then either side of the exit bearing, then behind the post.
    private const float Diagonal = .70710678f;
    private static readonly (float Forward, float Lateral)[] WatchHeadings =
    [
        (1, 0), (Diagonal, Diagonal), (Diagonal, -Diagonal),
        (0, 1), (0, -1), (-Diagonal, Diagonal), (-Diagonal, -Diagonal), (-1, 0)
    ];
    internal static int WatchProbeCount => WatchHeadings.Length * 2;

    // One navigable patch of ground and one eye-level sightline per search slice.
    // A camper can watch the path alongside a wall without seeing the exit trigger.
    internal static bool TryWatchApproach(Vector3 feet, Vector3 eye, Vector3 exit, int probe, out Vector3 target)
    {
        target = default;
        var forward = exit - feet;
        forward.y = 0f;
        forward = forward.sqrMagnitude > .01f ? forward.normalized : new Vector3(0, 0, 1);
        var lateral = new Vector3(-forward.z, 0f, forward.x);
        var heading = WatchHeadings[probe % WatchHeadings.Length];
        var range = probe < WatchHeadings.Length ? 8f : 4f;
        var sample = feet + (forward * heading.Forward + lateral * heading.Lateral) * range;
        if (!NavMesh.SamplePosition(sample, out var ground, 1.5f, NavMesh.AllAreas)) return false;
        target = ground.position + Vector3.up;
        return !Physics.Linecast(eye, target, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
    }
}
