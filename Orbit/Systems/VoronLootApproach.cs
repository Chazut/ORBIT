using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

// A detour belongs to one assigned pickup. The squad anchor, splinter parent and
// claim stay intact while the claimant walks the entrance and uses the physical lock.
internal sealed class VoronLootApproach : IObjectiveWork
{
    private readonly Agent _agent;
    private readonly MovementSystem _movement;
    private readonly WaypointSystem _waypoints;
    private readonly Waypoint _loot;
    private readonly Door _door;
    private readonly VoronEntranceRoute _route;
    private OperationStep _reach;
    private float _nextTick, _requestedAt, _lastProgress, _handleDeadline;
    private Vector3 _lastFeet;
    private bool _walking, _entering, _cancelled;
    private Result _result;

    internal VoronLootApproach(Agent agent, MovementSystem movement, WaypointSystem waypoints)
    {
        _agent = agent; _movement = movement; _waypoints = waypoints;
        _loot = agent.Objective.Location; _door = _loot.VoronLootDoor;
        _route = new VoronEntranceRoute(CanReach);
    }

    internal static Door RoomDoor(Waypoint point, Door marked)
    {
        if (marked == null || point.Category is not (WaypointCategory.LooseLoot or WaypointCategory.ContainerLoot)
            || (point.Position - VoronEntranceRoute.VoronHallHandle).sqrMagnitude >= 225f
            || point.Target is not InteractableObject item || item == null) return null;
        return VoronEntranceRoute.InLootRoom(LootVisibility.Target(item)) ? marked : null;
    }

    internal static void IncludeLock(ref System.Collections.Generic.List<Door> doors, Door marked)
    {
        if (marked == null || marked.DoorState != EDoorState.Locked) return;
        doors ??= new();
        if (!doors.Contains(marked)) doors.Add(marked);
    }

    internal enum Result { Working, Inside, Failed, Unavailable }
    internal bool Owns(Waypoint point) => ReferenceEquals(point, _loot);
    internal static bool Allowed(Agent agent, Door door) => door != null && agent.Squad != null
        && (door.DoorState != EDoorState.Locked || agent.Squad.ForceUnlockDoorIds.Contains(door.GetInstanceID()));

    private bool CanReach(Door door, Vector3 feet)
    {
        if (_reach == null || _reach.Object != door) _reach = new() { Kind = OperationStepKind.Access, Object = door };
        return _reach.InteractionCheck(feet);
    }

    internal void Cancel() { _cancelled = true; _waypoints.CancelOperationWork(this); }

    private bool Current => !_cancelled && Owns(_agent.Objective.Location)
        && _agent.Objective.Status == ObjectiveStatus.Moving && _agent.IsActive
        && !MovementSystem.IsTravelPaused(_agent)
        && !(_agent.IsDormant && _agent.Squad != null && Time.time < _agent.Squad.GhostFightUntil);

    internal Result Tick()
    {
        if (Current && _result == Result.Working && Time.time >= _nextTick
            && Time.time >= _agent.Movement.DoorInteractHoldUntil && _waypoints.TryOperationWork(this))
            ResumeObjectiveWork();
        return _result;
    }

    public void ResumeObjectiveWork()
    {
        if (!Current || _result != Result.Working || Time.time < _nextTick || Time.time < _agent.Movement.DoorInteractHoldUntil) return;
        var now = Time.time;
        _nextTick = now + .25f;
        if (!Allowed(_agent, _door) || _loot.Target == null || _waypoints.IsUnavailableLooseLoot(_loot)
            || _agent.Squad.CompletedPoiIds.Contains(_loot.Id) || _waypoints.IsClaimedByOther(_loot.Id, _agent.Id))
        {
            _result = Result.Unavailable;
            Log.Debug($"VORON LOOT: {_agent} point={_loot.Id} pickup or access no longer available");
            return;
        }

        var feet = _agent.Position;
        if (VoronEntranceRoute.InLootRoom(feet)
            && (!_entering || (feet - VoronEntranceRoute.RoomStand).sqrMagnitude <= .36f))
        {
            _result = Result.Inside;
            Log.Info($"VORON LOOT: {_agent} point={_loot.Id} inside; resuming original pickup feet={feet}");
            return;
        }
        if ((feet - _lastFeet).sqrMagnitude > .36f) { _lastFeet = feet; _lastProgress = now; }
        bool stopped = _walking && now - _requestedAt > 2f
            && (_agent.Movement.Status != MovementStatus.Moving || now - _lastProgress >= 20f);
        if (_entering)
        {
            if (stopped) Fail("stopped at marked threshold");
            return;
        }
        var step = _route.Tick(_agent, _waypoints, _door, stopped);
        switch (step)
        {
            case VoronEntranceRoute.Result.Walk:
            case VoronEntranceRoute.Result.Crossing:
                if (step == VoronEntranceRoute.Result.Walk)
                    _movement.MoveToByPath(_agent, _route.Target, sprint: false);
                StartedLeg(_route.Target);
                break;
            case VoronEntranceRoute.Result.Retry:
                _walking = false;
                Log.Debug($"VORON LOOT: {_agent} point={_loot.Id} retry={_route.Detail}");
                break;
            case VoronEntranceRoute.Result.Failed:
                Fail(_route.Detail);
                break;
            case VoronEntranceRoute.Result.AtHandle:
                _walking = false;
                if (_handleDeadline == 0) _handleDeadline = now + 15f;
                if (now >= _handleDeadline) { Fail("marked threshold unavailable"); break; }
                if (_door.DoorState != EDoorState.Open)
                {
                    if (CanReach(_door, feet)) _movement.UseObjectiveGate(_agent, _door, "VORON LOOT");
                    break;
                }
                // This leaf can have the same baked mesh gap as the building entrance.
                var inside = VoronEntranceRoute.RoomStand;
                if (_route.Walkable(feet, inside)) _movement.MoveToByPath(_agent, inside, sprint: false);
                else if (_waypoints.CrossVoronEntrance?.Invoke(_agent, _door, inside) != true) break;
                _entering = true;
                StartedLeg(inside);
                break;
        }
    }

    private void StartedLeg(Vector3 target)
    {
        _walking = true;
        _requestedAt = _lastProgress = Time.time;
        _lastFeet = _agent.Position;
        Log.Debug($"VORON LOOT: {_agent} point={_loot.Id} stage={(_entering ? "room" : _route.Detail)} target={target}");
    }

    private void Fail(string reason)
    {
        _result = Result.Failed;
        Log.Info($"VORON LOOT: {_agent} point={_loot.Id} failed={reason} feet={_agent.Position}");
    }
}
