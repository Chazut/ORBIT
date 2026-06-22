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
    // Primary lookup: keyed by the Door object's instance id. Immune to any DoorId<->Door.Id string mismatch
    // because GetLink resolves by the exact same Door reference. The string-id map is kept as a fallback for
    // the rare link whose Door reference is null at registration time.
    private static readonly Dictionary<int, NavMeshDoorLink> _linksByDoorInstanceId = new();
    private static readonly Dictionary<string, NavMeshDoorLink> _linksByDoorId = new();
    private static readonly HashSet<int> _carverOpenedDoorIds = new();

    /// <summary>Populated at boot from BotDoorsController._navMeshDoorLinks (DoorCarverShrinkPatch enumerates
    /// them). Keyed by the Door's instance id first (mismatch-proof), with the string DoorId kept as a
    /// fallback. Earlier this keyed only by DoorId, which silently missed any link whose DoorId disagreed with
    /// its Door.Id — those doors logged "NO NavMeshDoorLink found" and their loot stayed unreachable.</summary>
    public static void RegisterLink(NavMeshDoorLink link)
    {
        if (link == null) return;
        if (link.Door != null)
            _linksByDoorInstanceId[link.Door.GetInstanceID()] = link;

        var id = link.DoorId;
        if (string.IsNullOrEmpty(id) && link.Door != null) id = link.Door.Id;
        if (!string.IsNullOrEmpty(id)) _linksByDoorId[id] = link;
    }

    public static NavMeshDoorLink GetLink(Door door)
    {
        if (door == null) return null;
        // Match by the exact Door reference first (mismatch-proof), then fall back to the string id.
        if (_linksByDoorInstanceId.TryGetValue(door.GetInstanceID(), out var link)) return link;
        return _linksByDoorId.TryGetValue(door.Id, out link) ? link : null;
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

    /// <summary>
    /// Boot diagnostic (1.2.0-pre): census of how many scene doors actually resolve a <see cref="NavMeshDoorLink"/>
    /// via <see cref="GetLink"/>, how many do NOT (and how many of those are Locked), plus the count of registered
    /// links whose <c>DoorId</c> disagrees with their <c>Door.Id</c>. Distinguishes the "NO NavMeshDoorLink found"
    /// gap between a lookup mismatch (now fixed by the instance-id key) and genuinely linkless doors (loot behind
    /// those is navmesh-unreachable regardless of door state). Remove with the rest of the door-routing diag.
    /// </summary>
    public static void LogLinkCensus()
    {
        var mismatches = 0;
        foreach (var kv in _linksByDoorInstanceId)
        {
            var link = kv.Value;
            var did = link.DoorId;
            var oid = link.Door != null ? link.Door.Id : null;
            if (!string.IsNullOrEmpty(did) && !string.IsNullOrEmpty(oid) && did != oid) mismatches++;
        }

        var doors = UnityEngine.Object.FindObjectsOfType<Door>();
        int withLink = 0, withoutLink = 0, lockedWithout = 0, shown = 0;
        var sample = new System.Text.StringBuilder();
        foreach (var door in doors)
        {
            if (door == null) continue;
            if (GetLink(door) != null) { withLink++; continue; }
            withoutLink++;
            if (door.DoorState == EDoorState.Locked) lockedWithout++;
            if (shown < 20)
            {
                if (shown > 0) sample.Append(", ");
                sample.Append($"{door.Id}({door.DoorState})");
                shown++;
            }
        }

        Log.Info($"DoorLinkCensus: {_linksByDoorInstanceId.Count} links by instance / {_linksByDoorId.Count} by string-id, {mismatches} with DoorId!=Door.Id | scene doors: {withLink} resolve a link, {withoutLink} do NOT ({lockedWithout} Locked) | sample no-link: {sample}");
    }
}
