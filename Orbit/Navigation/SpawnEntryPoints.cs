using EFT.Game.Spawning;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Navigation;

// One marker inventory per raid, captured with the map during BotsController.Init.
// Eligibility is read when queried so markers enabled or updated after loading stay usable.
internal sealed class SpawnEntryPoints
{
    private readonly SpawnPointMarker[] _markers;

    private SpawnEntryPoints(SpawnPointMarker[] markers) => _markers = markers;

    internal static SpawnEntryPoints Capture()
    {
        using var timing = PerformanceJournal.Measure(TransitionPhase.SpawnEntryScan,
            "spawn-entry-scan", "SpawnPointMarker", always: true);
        return new SpawnEntryPoints(UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>(includeInactive: true));
    }

    internal string FindNearest(Vector3 position, out float distanceSqr)
    {
        distanceSqr = float.MaxValue;
        string entry = string.Empty;
        for (var i = 0; i < _markers.Length; i++)
        {
            var marker = _markers[i];
            if (marker == null || !marker.gameObject.activeInHierarchy || marker.SpawnPoint == null) continue;
            var infiltration = marker.SpawnPoint.Infiltration;
            if (string.IsNullOrEmpty(infiltration)) continue;
            var gap = (marker.Position - position).sqrMagnitude;
            if (gap >= distanceSqr || float.IsNaN(gap)) continue;
            distanceSqr = gap;
            entry = infiltration;
        }
        return entry;
    }
}
