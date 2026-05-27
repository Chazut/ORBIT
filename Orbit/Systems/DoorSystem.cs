using System.Linq;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>
/// Boot-time scan of every interactable door on the map. The waypoint
/// system reads <see cref="Doors"/> at startup to seed per-POI locked-door
/// detection (paths that cross a Locked door are routed differently).
/// </summary>
public class DoorSystem
{
    public readonly Door[] Doors;

    public DoorSystem()
    {
        var interactables = Object.FindObjectsOfType<WorldInteractiveObject>();
        Doors = interactables.Where(interactable => interactable.Collider != null).OfType<Door>().ToArray();
        Log.Debug($"Found {Doors.Length} doors on the map");
    }
}
