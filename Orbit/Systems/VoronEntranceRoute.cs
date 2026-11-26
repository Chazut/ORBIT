using System;
using EFT.Interactive;
using Orbit.Entities;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Shared by the marked rush and a single looter. The caller owns the objective and movement orders.
internal sealed class VoronEntranceRoute(Func<Door, Vector3, bool> checkReach)
{
    internal const string MarkedDoorId = "door_Shoreline_Middle_00030";
    internal enum Result { Waiting, Walk, Crossing, Retry, AtHandle, Failed }
    private enum Stage { Select, Outside, Cross, Handle, Arrived }
    private Stage _stage;
    private int _next;
    private bool _triedHall;
    private float _waitUntil, _nextOpen;
    private readonly NavMeshPath _path = new();
    private Vector3 _hall, _handle;
    internal bool AtHandle => _stage == Stage.Arrived;
    internal Vector3 Target { get; private set; }
    internal Door Entrance { get; private set; }
    internal string Detail { get; private set; }

    // Standing positions on the Shoreline power station mesh. Each pair straddles
    // one exterior door. The handle is on the hall side of the locked room.
    private static readonly (string Door, Vector3 Outside, Vector3 Hall)[] VoronEntrances =
    {
        ("door_Shoreline_Middle_00037", new(-223.50f, -40.90f, 182.50f), new(-223.875f, -40.65f, 184.875f)),
        ("door_Shoreline_Middle_00026", new(-218.75f, -40.88f, 194.125f), new(-221f, -40.64f, 193.75f)),
        ("door_Shoreline_Middle_00027", new(-240.375f, -40.94f, 184.75f), new(-240.875f, -40.66f, 187.125f))
    };
    internal static readonly Vector3 VoronHallHandle = new(-241f, -40.66f, 197.625f);
    internal static readonly Vector3 RoomStand = new(-238.875f, -40.66f, 197.5f);

    // Hydro station, ground floor. Local axes follow the closed door frame. Use the
    // item itself: the navmesh stand can snap into the hall or outside the facade.
    internal static bool InLootRoom(Vector3 position)
    {
        var dx = position.x + 239.752f;
        var dz = position.z - 197.511f;
        var depth = dx * .9993f + dz * .0377f;
        var along = dz * .9993f - dx * .0377f;
        return position.y >= -42f && position.y <= -37.9f && depth > 0f && depth < 4.6f
            && along > -6.4f && along < 1.55f;
    }

    private static readonly Vector3[] VoronHandleStands =
    {
        VoronHallHandle, new(-240.8f, -40.66f, 197.4f), new(-240.8f, -40.66f, 197.8f)
    };
    private static bool VoronStand(Vector3 seed, out Vector3 stand)
    {
        var found = NavMesh.SamplePosition(seed, out var hit, .65f, NavMesh.AllAreas);
        stand = hit.position;
        return found && Mathf.Abs(stand.y - seed.y) <= .4f;
    }

    internal bool Walkable(Vector3 from, Vector3 to)
    {
        if ((from - to).sqrMagnitude <= .25f * .25f) return true;
        if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _path)
            || _path.status != NavMeshPathStatus.PathComplete) return false;
        var corners = _path.corners;
        return corners != null && corners.Length > 0 && (corners[0] - from).sqrMagnitude <= .36f
            && (corners[corners.Length - 1] - to).sqrMagnitude <= .25f;
    }

    private bool FindVoronHandle(Door marked, out Vector3 stand)
    {
        foreach (var seed in VoronHandleStands)
            if (VoronStand(seed, out stand) && (marked.DoorState == EDoorState.Open || checkReach(marked, stand)))
                return true;
        stand = default;
        return false;
    }


    private Result Order(Stage stage, Vector3 target)
    {
        _stage = stage;
        if (stage == Stage.Handle) _triedHall = true;
        Target = target;
        Detail = stage.ToString();
        return Result.Walk;
    }

    internal Result Retry(string reason)
    {
        Detail = reason;
        _stage = Stage.Select;
        Entrance = null;
        _waitUntil = _nextOpen = 0;
        return Result.Retry;
    }

    private Result Fail(string reason) { Detail = reason; return Result.Failed; }

    internal Result Tick(Agent actor, WaypointSystem w, Door marked, bool stopped)
    {
        var feet = actor.Position;
        var now = Time.time;
        if (_stage == Stage.Select)
        {
            if (!FindVoronHandle(marked, out _handle)) return Fail("marked door inaccessible from hall");
            if (!_triedHall && Walkable(feet, _handle)) return Order(Stage.Handle, _handle);
            if (_next >= VoronEntrances.Length) return Fail("no reachable building entrance");
            var entry = VoronEntrances[_next++];
            var door = w.RushDoor(entry.Door);
            if (door == null || (!door.Operatable && door.DoorState != EDoorState.Open)
                || door.DoorState is not (EDoorState.Open or EDoorState.Shut)
                || !VoronStand(entry.Outside, out var outside) || !VoronStand(entry.Hall, out _hall)
                || (door.DoorState == EDoorState.Shut && !checkReach(door, outside))
                || !Walkable(feet, outside) || !Walkable(_hall, _handle)) return Result.Waiting;
            Entrance = door;
            return Order(Stage.Outside, outside);
        }

        if ((feet - Target).sqrMagnitude > .36f)
            return stopped ? Retry("movement stopped short") : Result.Waiting;
        if (_stage is Stage.Handle or Stage.Arrived)
        {
            if (marked.DoorState != EDoorState.Open && !checkReach(marked, feet))
                return Fail("marked door out of reach from hall");
            _stage = Stage.Arrived;
            return Result.AtHandle;
        }
        if (_stage == Stage.Cross)
            return Walkable(feet, _handle) ? Order(Stage.Handle, _handle) : Retry("hall route changed");

        if (_waitUntil == 0) _waitUntil = now + 8f;
        if (Entrance == null || (!Entrance.Operatable && Entrance.DoorState != EDoorState.Open) || now >= _waitUntil)
            return Retry("entrance unavailable");
        if (Entrance.DoorState == EDoorState.Locked) return Retry("entrance locked");
        if (Entrance.DoorState == EDoorState.Shut && now >= _nextOpen && checkReach(Entrance, feet))
        {
            _nextOpen = now + 2f;
            w.OpenRushDoor?.Invoke(actor, Entrance);
            return Result.Waiting;
        }
        if (Entrance.DoorState != EDoorState.Open) return Result.Waiting;
        if (Walkable(feet, _hall)) return Order(Stage.Cross, _hall);
        if (w.CrossVoronEntrance?.Invoke(actor, Entrance, _hall) != true) return Result.Waiting;
        Order(Stage.Cross, _hall);
        return Result.Crossing;
    }
}
