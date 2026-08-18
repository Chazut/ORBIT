using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using Orbit.Helpers;
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
    // Doors Locked at raid start. Used purely to log when one of them physically opens: a locked door is
    // visually identical to a normal one in-game and a bot open never reaches the real "Open" state, so this is
    // the reliable "a bot actually opened a locked door" signal (grep LOCKED-DOOR OPENED).
    private readonly HashSet<int> _startedLocked = new();

    public DoorSystem()
    {
        var interactables = Object.FindObjectsOfType<WorldInteractiveObject>();
        Doors = interactables.Where(interactable => interactable.Collider != null).OfType<Door>().ToArray();
        Log.Debug($"Found {Doors.Length} doors on the map");

        for (var i = 0; i < Doors.Length; i++)
        {
            Doors[i].OnDoorStateChanged += HandleDoorStateChanged;
            if (Doors[i].DoorState == EDoorState.Locked) _startedLocked.Add(Doors[i].GetInstanceID());
        }
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

        // Verification aid: a door that STARTED the raid Locked just went visually open. This is the reliable
        // "a bot physically opened a locked door" marker (a locked door looks identical to a normal one and a
        // bot open never reaches the real Open state). Note whether ORBIT had carved its navmesh so a bot could
        // reach it.
        if (passable && _startedLocked.Contains(door.GetInstanceID()))
            Log.Info($"LOCKED-DOOR OPENED: door {door.Id} went {prevState}→{nextState} (ORBIT carver was {(DoorNavMesh.IsCarverOpened(door.GetInstanceID()) ? "OPEN" : "closed")})");

        for (var i = 0; i < _bots.Count; i++)
            SetIgnored(_bots[i].bot, _bots[i].pom, door.Collider, passable);
    }

    private static void SetIgnored(Collider botCollider, Collider pomCollider, Collider doorCollider, bool ignore)
    {
        if (doorCollider == null) return;
        if (pomCollider != null) Physics.IgnoreCollision(pomCollider, doorCollider, ignore);
        if (botCollider != null) PhysicsExtensions.IgnoreCollision(botCollider, doorCollider, ignore);
    }
}
