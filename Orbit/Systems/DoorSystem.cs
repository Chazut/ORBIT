using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>
/// Boot-time scan of every interactable door on the map. The waypoint system reads <see cref="Doors"/> at
/// startup to seed per-POI locked-door detection (paths that cross a Locked door are routed differently).
///
/// Also owns per-door bot collision: bot &lt;-&gt; door collision is toggled as each door opens/closes (via BSG's
/// <c>OnDoorStateChanged</c>) so a bot physically stops at a CLOSED leaf — it can't phase through a locked door
/// whose navmesh carver we opened — and passes freely through an open / mid-swing one so the door never shoves
/// it. Replaces the old blanket "ignore every door forever" which was what let carved-open locked doors be
/// walked through.
/// </summary>
public class DoorSystem
{
    public readonly Door[] Doors;

    private readonly List<(Collider bot, Collider pom)> _bots = new();

    public DoorSystem()
    {
        var interactables = Object.FindObjectsOfType<WorldInteractiveObject>();
        Doors = interactables.Where(interactable => interactable.Collider != null).OfType<Door>().ToArray();
        Log.Debug($"Found {Doors.Length} doors on the map");

        for (var i = 0; i < Doors.Length; i++)
            Doors[i].OnDoorStateChanged += HandleDoorStateChanged;
    }

    // A closed leaf (Locked / Shut) blocks the bot; an open or mid-swing (Interacting) one is passable and must
    // not shove it.
    private static bool IsPassable(EDoorState state) => state == EDoorState.Open || state == EDoorState.Interacting;

    /// <summary>Registers a bot and syncs its door collision to every door's current state.</summary>
    public void RegisterBot(Collider botCollider, Collider pomCollider)
    {
        for (var i = 0; i < Doors.Length; i++)
            SetIgnored(botCollider, pomCollider, Doors[i].Collider, IsPassable(Doors[i].DoorState));
        _bots.Add((botCollider, pomCollider));
    }

    /// <summary>Drops a dead/removed bot so we stop toggling collision for it.</summary>
    public void UnregisterBot(Collider botCollider)
    {
        for (var i = _bots.Count - 1; i >= 0; i--)
            if (_bots[i].bot == botCollider) _bots.RemoveAt(i);
    }

    private void HandleDoorStateChanged(WorldInteractiveObject obj, EDoorState prevState, EDoorState nextState)
    {
        if (obj is not Door door || door.Collider == null) return;
        if (IsPassable(prevState) == IsPassable(nextState)) return; // collision verdict unchanged
        var passable = IsPassable(nextState);
        for (var i = 0; i < _bots.Count; i++)
            SetIgnored(_bots[i].bot, _bots[i].pom, door.Collider, passable);
    }

    private static void SetIgnored(Collider botCollider, Collider pomCollider, Collider doorCollider, bool ignore)
    {
        if (doorCollider == null) return;
        if (pomCollider != null) Physics.IgnoreCollision(pomCollider, doorCollider, ignore);
        if (botCollider != null) EFTPhysicsClass.IgnoreCollision(botCollider, doorCollider, ignore);
    }
}
