using System.Collections.Generic;
using Orbit.Zones;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Loot can be above a catalogue boundary while the walkable floor beneath it is below that boundary.
// Use a tightly sampled target ground only for this edge case, never a tolerance on the whole floor.
internal sealed class ZoneFloorArrival
{
    private readonly Dictionary<Vector3, (Vector3? Ground, float Expires)> _ground = new();

    public bool Matches(string map, string floor, Vector3 target, Vector3 position)
    {
        if (FloorCatalog.Matches(map, floor, position.x, position.y, position.z)) return true;
        if (!FloorCatalog.Matches(map, floor, target.x, target.y, target.z)) return false;
        if (!FloorCatalog.Matches(map, floor, position.x, target.y, position.z)) return false;
        // Do not bridge storeys, even when a wide NavMesh sample could find one.
        if (Mathf.Abs(target.y - position.y) > 1.25f) return false;
        if (!_ground.TryGetValue(target, out var cached) || Time.time >= cached.Expires)
        {
            Vector3? ground = null;
            if (NavMesh.SamplePosition(target, out var hit, 1.5f, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - target.y) <= 1.25f
                && HorizontalDistanceSqr(hit.position, target) <= .75f * .75f)
                ground = hit.position;
            if (_ground.Count >= 128) _ground.Clear();
            cached = (ground, Time.time + 10f);
            _ground[target] = cached;
        }
        if (!cached.Ground.HasValue) return false;
        if (!NavMesh.SamplePosition(position, out var feet, .75f, NavMesh.AllAreas)
            || Mathf.Abs(feet.position.y - position.y) > .5f) return false;
        return Mathf.Abs(feet.position.y - cached.Ground.Value.y) <= .5f;
    }

    private static float HorizontalDistanceSqr(Vector3 a, Vector3 b)
        => (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z);
}
