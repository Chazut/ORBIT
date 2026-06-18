using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine.AI;

namespace Orbit.Helpers;

/// <summary>
/// Maps each Door to its NavMeshDoorLink and opens a locked door's navmesh carver on demand, so ORBIT can
/// route a squad to a loot point behind a locked door WITHOUT visibly unlocking the door early.
///
/// A Locked door keeps <c>Carver_Closed.carving = true</c>, which cuts the Unity navmesh across the doorway
/// so <c>NavMesh.CalculatePath</c> routes AROUND the building (confirmed in-raid by DoorRoutingDiag: a locked
/// door reads PathPartial with Carver_Closed.carving=True; unlocking it flips ONLY that carver to false and
/// the path becomes PathComplete). <see cref="OpenCarver"/> flips just that carver and leaves
/// <c>DoorState = Locked</c>, so the door stays visually locked until a bot physically arrives and unlocks it
/// (with the key animation) in MovementSystem.HandleDoors.
///
/// No per-frame BSG routine re-asserts the carver on a locked door (BotDoorsController.Update → ManualUpdate
/// is a no-op until the door is Open), so the flip persists for the rest of the raid.
///
/// Phasing guard: the carver is shared per door, so once opened the navmesh is passable for EVERY bot, not
/// just the squad that rolled. ORBIT bots ignore door colliders, so a bot reaching a carver-opened-but-still-
/// locked door would phase straight through it. <see cref="IsCarverOpened"/> lets the proximity door handler
/// unlock+open the door for ANY arriving PMC, not only the granting squad, so the door always actually opens
/// before anyone passes.
/// </summary>
public static class DoorNavMesh
{
    private static readonly Dictionary<string, NavMeshDoorLink> _linksByDoorId = new();
    private static readonly HashSet<int> _carverOpenedDoorIds = new();

    /// <summary>Populated at boot from BotDoorsController._navMeshDoorLinks (DoorCarverShrinkPatch enumerates
    /// them). There is no direct Door → link pointer in BSG, so we key by DoorId.</summary>
    public static void RegisterLink(NavMeshDoorLink link)
    {
        if (link == null) return;
        var id = link.DoorId;
        if (string.IsNullOrEmpty(id) && link.Door != null) id = link.Door.Id;
        if (!string.IsNullOrEmpty(id)) _linksByDoorId[id] = link;
    }

    public static NavMeshDoorLink GetLink(Door door)
    {
        if (door == null) return null;
        return _linksByDoorId.TryGetValue(door.Id, out var link) ? link : null;
    }

    /// <summary>
    /// Open the navmesh through a locked door by clearing its Carver_Closed carving, leaving DoorState
    /// untouched (door stays visually Locked). Returns true if the link was found and the carver flipped. The
    /// door is recorded so any arriving PMC (not just the granting squad) unlocks+opens it on contact — see
    /// the phasing guard note on the class.
    /// </summary>
    public static bool OpenCarver(Door door)
    {
        if (door == null) return false;
        var link = GetLink(door);
        if (link?.Carver_Closed == null) return false;
        link.Carver_Closed.carving = false;
        _carverOpenedDoorIds.Add(door.GetInstanceID());
        return true;
    }

    /// <summary>True when ORBIT opened this door's navmesh carver — the proximity handler must then unlock it
    /// for any arriving PMC so no bot phases through the still-locked leaf.</summary>
    public static bool IsCarverOpened(int doorInstanceId) => _carverOpenedDoorIds.Contains(doorInstanceId);
}
