using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine.AI;

namespace Orbit.Helpers;

/// <summary>
/// Maps each Door to its NavMeshDoorLink and opens a locked door's navmesh carver on demand, so a squad can
/// route to loot behind a locked door without visibly unlocking it early.
///
/// A Locked door keeps <c>Carver_Closed.carving = true</c>, cutting the Unity navmesh across the doorway.
/// <see cref="OpenCarver"/> flips only that carver and leaves <c>DoorState = Locked</c>. No BSG routine
/// re-asserts the carver while the door is Locked (BotDoorsController.ManualUpdate is a no-op until Open), so
/// the flip persists for the raid.
///
/// Phasing guard: the carver is shared per door and bots ignore door colliders, so once it is open every bot
/// could pass through a still-locked leaf. <see cref="IsCarverOpened"/> lets the proximity handler unlock+open
/// the door for any arriving PMC first.
/// </summary>
public static class DoorNavMesh
{
    // Instance-id is primary (immune to DoorId/Door.Id mismatch); string-id is a fallback for links whose
    // Door is null at registration.
    private static readonly Dictionary<int, NavMeshDoorLink> _linksByDoorInstanceId = new();
    private static readonly Dictionary<string, NavMeshDoorLink> _linksByDoorId = new();
    private static readonly HashSet<int> _carverOpenedDoorIds = new();

    /// <summary>Registers a link at boot from BotDoorsController._navMeshDoorLinks.</summary>
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
        if (_linksByDoorInstanceId.TryGetValue(door.GetInstanceID(), out var link)) return link;
        return _linksByDoorId.TryGetValue(door.Id, out link) ? link : null;
    }

    /// <summary>
    /// Opens the navmesh through a locked door by clearing Carver_Closed.carving, leaving DoorState Locked.
    /// Records the door for the phasing guard (see class summary). Returns false if no link was found.
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

    /// <summary>True once this door's carver was opened, so the proximity handler unlocks it before any bot reaches it.</summary>
    public static bool IsCarverOpened(int doorInstanceId) => _carverOpenedDoorIds.Contains(doorInstanceId);

    /// <summary>
    /// Boot diagnostic: logs how many scene doors resolve a link vs. not, to tell a lookup mismatch apart from
    /// genuinely linkless doors (whose loot is navmesh-unreachable regardless of door state).
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
