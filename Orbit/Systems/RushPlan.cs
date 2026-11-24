using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using Orbit.Tasks.Actions;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal sealed partial class RushPlan : IObjectiveWork
{
    private Squad _squad;
    private WaypointSystem _waypoints;
    void IObjectiveWork.ResumeObjectiveWork() => Tick(_squad, _waypoints, WaypointSystem.ObjectiveCombat(_squad));
    internal readonly string Kind;
    internal string BossRole => Kind == "Boss" ? _sites[0].Boss : null;
    internal MainObjective Main;
    private string _status = "pending";
    internal string Status { get => Sniper?.Status ?? _status; private set => _status = value; }
    internal readonly SniperPlan Sniper;
    internal string Step => Sniper != null ? Sniper.Step : _current < 0
        ? _lastSiteName ?? (Kind == "Spawn" ? "Choose nearby spawn" : "Choose nearest reachable sector") : _sites[_current].Name;
    internal int Index => _processed;
    internal int Count => Kind == "Spawn" ? Mathf.Min(ServerConfig.Rush.SpawnSectors, _sites.Count) : _sites.Count;
    internal string Name => Kind == "Sniper" ? "Sniper overwatch" : Kind + " rush";
    private readonly List<RushPoint> _sites;
    private readonly string[] _states;
    private readonly int[] _sequence;
    private readonly float _searchScale;
    private readonly Dictionary<Agent, Waypoint> _orders = new();
    private readonly HashSet<int> _attempted = new();
    private readonly List<Vector3> _approachHistory = new();
    private readonly OperationRouteSearch _route = new();
    private AuthoredPointSearch _pointRepair;
    private bool _repairedApproach, _repairedInterior;
    private bool _stageCustomApproach;
    private readonly RecordedRouteSearch _recorded = new();
    private bool _searchingRecorded;
    private readonly RushRouteEstimate _ranker = new();
    private const float NearbySpawnDistanceFactor = 1.5f;
    private readonly NavMeshPath _path = new();
    private Agent _actor;
    private string _lastSiteName;
    private Waypoint _loot;
    private MarkedRoomScope _room;
    private OperationStep _doorStep;
    private EDoorState? _lastDoorRequest;
    private const float DoorArrivalRadius = .25f;
    private readonly HashSet<Agent> _roomLooters = new();
    private float _roomIdle;
    private float _bestDoorGap = float.MaxValue;
    private float _roomEntryIdle, _roomEntryBestDistance = float.MaxValue;
    private const float RoomEntryStallSeconds = 45f;
    private int _current = -1, _candidate, _best = -1, _visited, _processed, _skipped, _retries, _attempts;
    private string _lastFailure;
    private float _bestLength = float.MaxValue, _nextWork, _elapsed, _held, _lastTick, _nextRoam, _interactAt;
    private bool _paused, _partial, _inside, _doorApproach, _ranking;
    private string _bestRoute;
    private int _nearbySpawnCandidates;
    private float _nearbySpawnRadius;
    private Vector3 _anchor, _sortOrigin;

    internal RushPlan(string kind, List<RushPoint> sites, float searchScale, RushStyle style = null)
    { Kind = kind; _sites = new List<RushPoint>(sites); _states = new string[sites.Count]; _sequence = new int[sites.Count]; _searchScale = searchScale;
        if (kind == "Sniper") Sniper = new SniperPlan(sites[0], style ?? new()); }

    internal bool Owns(Agent agent) => Sniper != null ? Sniper.Owns(agent) : !Main.Completed && !_paused && !agent.SoloExtractRequested
        && (!_orders.TryGetValue(agent, out var point) || agent.Objective.Location == point);
    internal bool CollectingMarkedRoom => Kind == "Marked" && !Main.Completed && _inside;
    internal bool CollectingCustomArea => Kind == "Custom" && !Main.Completed && _customArrived
        && _sites[_current].CustomMode is "Loot" or "CampThenLoot";
    internal bool OwnsMarkedLoot(Agent agent, Waypoint point) => CollectingMarkedRoom && !_paused
        && agent == _actor && point != null && point == _loot;

    internal bool Tick(Squad squad, WaypointSystem waypoints, bool combat)
    {
        _squad = squad; _waypoints = waypoints;
        if (Sniper != null) return Sniper.Tick(squad, waypoints, Main, combat);
        if (Main.Completed) return false;
        var now = Time.time;
        var delta = _lastTick > 0 ? Mathf.Clamp(now - _lastTick, 0, 1) : 0;
        _lastTick = now;
        if (!ServerConfig.Rush.Allows(squad.Leader?.BotCategory) || squad.ExtractRequested && !_recorded.Active)
            return End(squad, waypoints, "cancelled for extraction or settings");
        if (Kind == "Spawn" && WaypointSystem.RushRaidSeconds > ServerConfig.Rush.SpawnDeadline)
            return End(squad, waypoints, "opening window expired");
        // Live-boss perception is unchanged. Corpse discovery is local proximity;
        // own-squad kill knowledge is handled by TickRush before routing resumes.
        if (Kind == "Boss" && now >= _nextWork)
        {
            var found = waypoints.PerceivedRushBoss(squad, _sites[0].Boss, out var corpse);
            if (found != null)
            {
                End(squad, waypoints, found);
                squad.BossLoot.Enqueue(squad, corpse);
                return false;
            }
        }
        if (combat || now < squad.GhostFightUntil)
            return Pause(squad, waypoints, "paused");
        // Recheck here as well as in the strategy: queued route work can resume between
        // strategy ticks. A kill's approach must survive the end of the combat pause.
        if (HasOwnKillDetour(squad, waypoints))
        {
            var anchor = squad.Objective.Location;
            Pause(squad, waypoints, "looting own kill");
            // The ordinary corpse escort needs a stable mission anchor while Rush
            // releases its movement orders. It will hand control back after looting.
            squad.Objective.Location ??= anchor;
            return false;
        }
        if (_actor == null || !_actor.IsActive || _actor.Bot.IsDead || !squad.Members.Contains(_actor)
            || _actor.SoloExtractRequested || (CorpseEscort.InFlight(_actor) && (_loot == null || _actor.Objective.Location != _loot)))
        {
            Release(squad, waypoints); _actor = null; _loot = null; _route.Reset(); _pointRepair = null; _customArrived = false;
            _recorded.ResetSearch(); _searchingRecorded = false;
            if (CollectingMarkedRoom) _attempted.Clear();
            if (_current < 0) ResetRanking();
            var livingMember = false;
            foreach (var member in squad.Members)
            {
                if (member.Bot.IsDead || member.SoloExtractRequested) continue;
                livingMember = true;
                if (member.IsActive && !CorpseEscort.InFlight(member)) { _actor = member; break; }
            }
            if (_actor == null) return livingMember ? Pause(squad, waypoints, "waiting for available member")
                : End(squad, waypoints, "no remaining member");
        }
        if (_paused) { _paused = false; _route.Reset(); _recorded.ResetSearch(); _searchingRecorded = false; delta = 0; SetStatus(squad, _current < 0 ? "planning route" : "approach"); }
        // Door access does not imply an interior NavMesh connection. Preserve this
        // progress budget across combat pauses and actor changes at the same room.
        if (Kind == "Marked" && _inside && Status != "looting" && _room != null
            && !_room.Contains(_actor.Position))
        {
            var distance = Vector3.Distance(_actor.Position, _room.Center);
            if (distance < _roomEntryBestDistance - .5f)
            { _roomEntryBestDistance = distance; _roomEntryIdle = 0; }
            else _roomEntryIdle += delta;
            if (_roomEntryIdle >= RoomEntryStallSeconds)
                return Skip(squad, waypoints, "room entrance stalled");
        }
        if (Status != "looting" && !_customArrived) _elapsed += delta;
        if (_elapsed > ServerConfig.Rush.TravelTimeout && Status != "searching" && Status != "looting")
            return Skip(squad, waypoints, "travel timeout");
        if (Status is "searching" or "looting") _held += delta;
        if (Status == "looting" && _loot == null && (_roomIdle += delta) > ServerConfig.Rush.TravelTimeout)
            return Skip(squad, waypoints, "room loot stalled");
        if (_customArrived) return TickCustom(squad, waypoints, _sites[_current], delta);
        if (now < _nextWork) return true;
        if (_current < 0)
        {
            if (!waypoints.TryOperationWork(this)) return true;
            _nextWork = now + .1f;
            return SelectNext(squad, waypoints);
        }
        var site = _sites[_current];
        var door = Kind == "Marked" ? waypoints.RushDoor(site.DoorId) : null;
        if (Kind == "Marked" && door == null) return Skip(squad, waypoints, "door removed");
        if (door != null && !_inside && !_doorApproach &&
            OperationRouteSearch.NearInteraction(_actor.Position, door.transform.position))
        {
            // The authored point may sit across a wall. The native standing position
            // and the existing reach check decide which side can actually use the door.
            Release(squad, waypoints);
            _route.Reset();
            _pointRepair = null;
            _recorded.ResetSearch();
            _searchingRecorded = false;
            _approachHistory.Clear();
            _doorApproach = true;
            _retries = 0;
            _bestDoorGap = Vector3.Distance(_actor.Position, door.transform.position);
            Log.Info($"RUSH DOOR APPROACH: {squad} site={site.Name} door={door.Id} feet={_actor.Position} authored={Position(site)} gap={_bestDoorGap:F1}m");
        }
        // A native door transition can outlive the movement order (especially on Ghost wake).
        if (door != null && _doorApproach && !_inside &&
            (door.DoorState == EDoorState.Open || Status is "unlocking door" or "opening door"))
            return InteractDoor(squad, waypoints, site, door);
        if (_loot != null)
        {
            if (_actor.Objective.Location == _loot && _actor.Objective.Status is not (ObjectiveStatus.Finished or ObjectiveStatus.Failed)) return true;
            _attempted.Add(_loot.Id); waypoints.ReleaseClaim(_loot.Id, _actor.Id); _loot = null;
            _roomIdle = 0;
        }
        if (Status == "looting")
        {
            _roomEntryIdle = 0; _roomEntryBestDistance = float.MaxValue;
            if (!waypoints.TryOperationWork(this)) return true;
            _nextWork = now + .1f;
            if (_room != null && !_room.Contains(_actor.Position))
            {
                Release(squad, waypoints); _route.Reset(); SetStatus(squad, "returning to room"); return true;
            }
            HoldRoomMembers(squad, waypoints);
            _loot = waypoints.OperationLoot(_actor, RoomCenter(site), _attempted, out var exhausted, _room?.Radius ?? site.Radius, _room);
            if (_loot != null) { _roomIdle = 0; SetOrder(_actor, _loot, waypoints); }
            else if (exhausted)
            {
                _roomLooters.Add(_actor);
                var remaining = waypoints.RemainingMarkedLoot(_room);
                if (remaining > 0)
                    foreach (var member in squad.Members)
                        if (member.IsActive && !member.Bot.IsDead && !member.SoloExtractRequested && !_roomLooters.Contains(member))
                        {
                            _actor = member; _attempted.Clear(); Release(squad, waypoints); _route.Reset();
                            SetStatus(squad, "entering room"); return true;
                        }
                Log.Info($"RUSH ROOM: {squad} site={site.Name} state=collection finished members={_roomLooters.Count} remaining={remaining}");
                if (remaining > 0) return Skip(squad, waypoints, "room loot remaining: capacity or access");
                return Visit(squad, waypoints);
            }
            return true;
        }
        if (!_orders.ContainsKey(_actor))
        {
            if (!waypoints.TryOperationWork(this)) return true;
            _nextWork = now + .1f;
            var target = _inside ? RoomCenter(site) : Position(site);
            if (door != null && !_inside && _doorApproach) target = door.GetInteractionParameters(_actor.Position).InteractionPosition;
            // Marked rooms, boss areas and Other points on another floor may need distant stairs.
            var final = false;
            var staging = _stageCustomApproach && OperationRouteSearch.CanStageFrom(_actor.Position, target);
            if (_route.InvalidDestination)
            {
                if (!(_inside ? _repairedInterior : _repairedApproach) && !_recorded.Active)
                    return RepairDestination(squad, waypoints, site, door);
                ReportInvalidDestination(squad, site, target);
                return Skip(squad, waypoints, "no NavMesh near configured point");
            }
            var found = !_searchingRecorded && _route.Find(_actor.Position, target, out _anchor, out final, _retries, _approachHistory,
                floorAware: Kind == "Custom" || (Kind is "Marked" or "Boss") && Mathf.Abs(_actor.Position.y - target.y) > 2.5f,
                finalPoint: door != null && !_inside && _doorApproach ? DoorStep(door).InteractionCheck : null,
                localAccess: door != null && !_inside && _doorApproach,
                requireLocalDestination: !_recorded.Active && (!_doorApproach || _inside),
                destinationScope: Kind == "Custom" ? point => waypoints.CustomRushInZone(site, point)
                    : _inside && _room != null ? _room.Contains
                    : !string.IsNullOrEmpty(site.FloorId) && !_doorApproach ? point => waypoints.RepairFloor(site, point) : null,
                destinationRadius: Kind == "Custom" && (site.CustomMode is "Camp" or "CampThenLoot") ? site.Radius : 0,
                stageDisconnectedTarget: staging);
            if (_route.InvalidDestination)
            {
                // The last ordinary probes consumed this frame's route slot.
                _nextWork = now;
                waypoints.ContinueOperationWork(this);
                return true;
            }
            if (_searchingRecorded)
            {
                found = _recorded.Find(_actor.Position, out _anchor);
                if (_recorded.Pending) return true;
                _searchingRecorded = false;
                if (!found) return RetryRecorded(squad, waypoints);
            }
            else if (_recorded.Active && !_route.Pending && (!found || !final))
            { _searchingRecorded = true; _recorded.ResetSearch(); return true; }
            if (!found)
            {
                if (_route.Pending) { ContinueSpawnSearch(waypoints); return true; }
                Log.Info($"RUSH APPROACH: {squad} kind={Kind} site={site.Name} from={_actor.Position} target={target} samples={_route.Samples} partial={_route.PartialPaths} invalid={_route.InvalidPaths} attempt={_retries + 1}/3");
                _route.Reset(); _nextWork = now + 2;
                if (++_retries >= 3) return Skip(squad, waypoints, "unreachable");
                return true;
            }
            _partial = !final;
            if (Kind == "Custom") ReportCustomApproach(squad, site, final ? "final" : "leg",
                staging && !final ? "approaching disconnected destination" : "route selected");
            if (_partial) Log.Info($"RUSH APPROACH: {squad} kind={Kind} site={site.Name} leg={_anchor} target={target} remaining={Vector3.Distance(_anchor, target):F1}m");
            // Guide the mover to a precise spot. Door use is checked from its actual feet.
            var arrivalRadius = door != null && !_inside && _doorApproach && !_partial ? DoorArrivalRadius : 1f;
            SetOrder(_actor, Point(waypoints, _anchor, site.Name, arrivalRadius), waypoints);
            squad.Objective.Location = _orders[_actor]; squad.Objective.Status = SquadObjectiveState.Active;
        }
        foreach (var member in squad.Members)
        {
            if (member == _actor || !member.IsActive || member.SoloExtractRequested || _orders.ContainsKey(member)
                || CorpseEscort.InFlight(member)) continue;
            SetOrder(member, Point(waypoints, _anchor, "Follow rush"), waypoints);
            break;
        }
        // A partial leg can end beside its anchor even when the mover reports failure.
        // Count actual arrival before the status, then recalculate from the closer position.
        // A local door leg may itself be only two metres long. Let its mover
        // approach the assigned one-metre radius before evaluating progress.
        bool localDoorLeg = door != null && _doorApproach && !_inside && !_recorded.Active;
        bool moverEnded = _actor.Objective.Status is ObjectiveStatus.Finished or ObjectiveStatus.Failed;
        if (_partial && (_actor.Position - _anchor).sqrMagnitude <= (localDoorLeg && !moverEnded ? 1f : 9f))
        {
            RememberApproach(_actor.Position);
            Release(squad, waypoints);
            _route.Reset();
            if (localDoorLeg)
            {
                float gap = Vector3.Distance(_actor.Position, door.transform.position);
                if (OperationRouteSearch.AdvancedTowardDoor(gap, ref _bestDoorGap))
                    _retries = 0;
                else if (++_retries >= 3)
                {
                    Log.Info($"RUSH DOOR STALL: {squad} site={site.Name} door={door.Id} best={_bestDoorGap:F1}m current={gap:F1}m");
                    return Skip(squad, waypoints, "unreachable");
                }
            }
            else _retries = 0;
            return true;
        }
        // A stopped mover may still be on the correct side and within reach.
        if (door != null && !_inside && _doorApproach && !_partial)
        {
            if ((_actor.Position - _anchor).sqrMagnitude <= DoorArrivalRadius * DoorArrivalRadius
                || _actor.Objective.Status is ObjectiveStatus.Finished or ObjectiveStatus.Failed)
                return InteractDoor(squad, waypoints, site, door);
            return true;
        }
        if (_actor.Objective.Status == ObjectiveStatus.Failed || _partial && localDoorLeg && moverEnded)
        {
            RememberApproach(_anchor);
            Release(squad, waypoints); _route.Reset();
            if (++_retries >= 3) return Skip(squad, waypoints, "movement failed");
            return true;
        }
        if (Status == "searching" && _held >= site.SearchSeconds * _searchScale) return Visit(squad, waypoints);
        var arrivalDistanceSqr = door != null ? 1f : 9f;
        if (Status != "searching" && (_actor.Position - _anchor).sqrMagnitude > arrivalDistanceSqr)
            return true;
        if (_partial) return true;
        // Proximity to a destination upstairs is not arrival. A mover may stop on a
        // landing within the ordinary three-metre arrival radius.
        if (Kind == "Custom" && !waypoints.CustomRushInZone(site, _actor.Position))
        {
            if (_actor.Objective.Status != ObjectiveStatus.Finished) return true;
            ReportCustomApproach(squad, site, "rejected", "arrival outside zone or floor");
            Release(squad, waypoints); _route.Reset();
            if (++_retries >= 3) return Skip(squad, waypoints, "arrival outside zone or floor");
            return true;
        }
        if (door != null && !_inside && !_doorApproach)
        {
            _bestDoorGap = Vector3.Distance(_actor.Position, door.transform.position);
            _doorApproach = true; Release(squad, waypoints); _route.Reset(); _approachHistory.Clear(); _retries = 0; return true;
        }
        if (Kind == "Marked") { _held = 0; SetStatus(squad, "looting"); return true; }
        if (Kind == "Custom" && site.CustomMode != "Search")
        { _customArrived = true; return TickCustom(squad, waypoints, site, 0); }
        if (Status != "searching") { _held = 0; SetStatus(squad, "searching"); }
        if (_held >= site.SearchSeconds * _searchScale) return Visit(squad, waypoints);
        if (now >= _nextRoam)
        {
            if (!waypoints.TryOperationWork(this)) return true;
            _nextRoam = now + 5;
            var offset = UnityEngine.Random.insideUnitCircle * site.Radius;
            var sample = Position(site) + new Vector3(offset.x, 0, offset.y);
            if (NavMesh.SamplePosition(sample, out var hit, 2, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - site.Y) < 2.5f
                && (Kind != "Custom" || waypoints.CustomRushInZone(site, hit.position))
                && NavMesh.CalculatePath(_actor.Position, hit.position, NavMesh.AllAreas, _path)
                && _path.status == NavMeshPathStatus.PathComplete)
                SetOrder(_actor, Point(waypoints, hit.position, "Search " + site.Name), waypoints);
        }
        return true;
    }

    private OperationStep DoorStep(Door door)
        => _doorStep?.Object == door ? _doorStep : _doorStep = new() { Kind = OperationStepKind.Access, Object = door };

    private bool InteractDoor(Squad squad, WaypointSystem waypoints, RushPoint site, Door door)
    {
        if (door.DoorState == EDoorState.Open)
        {
            _room ??= waypoints.MarkedRoom(site);
            _inside = true; Release(squad, waypoints); _route.Reset(); _elapsed = 0; _retries = 0; _approachHistory.Clear();
            Main.Position = RoomCenter(site); Main.CellCoords = waypoints.WorldToCell(Main.Position);
            Log.Info($"RUSH ROOM: {squad} site={site.Name} source={_room.Source} center={Main.Position}");
            SetStatus(squad, "entering room"); return true;
        }
        try
        {
            if (!door.Operatable || door.DoorState == EDoorState.Interacting || door.InteractingPlayer != null) return true;
            var feet = _actor.Position;
            var gap = Vector3.Distance(feet, _anchor);
            // A displaced actor may have only a short window at the handle after waking.
            // Once a native unlock has settled, opening is a distinct request.
            bool displacedAfterUnlock = gap > DoorArrivalRadius && _lastDoorRequest == EDoorState.Locked && door.DoorState == EDoorState.Shut;
            var now = Time.time;
            if (now < _interactAt && !displacedAfterUnlock) return true;
            var interaction = door.GetInteractionParameters(feet).InteractionPosition;
            var tooFar = (feet - interaction).sqrMagnitude > 4f;
            var reach = tooFar ? default : OperationSwitchReach.Evaluate(feet, door);
            if (!reach.Reachable)
            {
                var detail = tooFar ? $"reason=interaction-position target={interaction}" : reach.Diagnostics;
                Log.Info($"RUSH REACH: {squad} door={door.Id} feet={feet} anchor={_anchor} gap={gap:F2}m {detail}");
                RememberApproach(feet);
                Release(squad, waypoints); _route.Reset(); _nextWork = Time.time + 2f;
                if (++_retries >= 3) return Skip(squad, waypoints, tooFar ? "door interaction point unreachable" : "door interaction blocked");
                if (Status != "approach") SetStatus(squad, "approach");
                return true;
            }
            _actor.Look.Target = reach.Point;
            _actor.Look.Type = LookType.Position;
            if (!_actor.IsDormant) LookSystem.LookToPoint(_actor, reach.Point, 360f);
            _interactAt = now + 5;
            if (++_attempts > 3) return Skip(squad, waypoints, "door interaction failed");
            _lastDoorRequest = door.DoorState;
            if (door.DoorState == EDoorState.Locked)
            {
                if (!MultiStepAccess.CanForceUnlock(door)) return Skip(squad, waypoints, "door requires power");
                door.Unlock(); Orbit.Api.OrbitDoorEvents.Raise(door, Orbit.Api.OrbitDoorEvents.Operation.Unlock);
                SetStatus(squad, "unlocking door");
            }
            else if (waypoints.OpenRushDoor?.Invoke(_actor, door) == true)
            {
                if (gap > DoorArrivalRadius)
                    Log.Info($"RUSH DOOR: {squad} door={door.Id} open-requested actor={_actor} ghost={_actor.IsDormant} feet={feet} anchor={_anchor} gap={gap:F2}m {reach.Diagnostics}");
                SetStatus(squad, "opening door");
            }
        }
        catch (Exception error) { Log.Warning($"RUSH: {squad} door={site.DoorId} error={error}"); }
        return true;
    }

    private bool HasOwnKillDetour(Squad squad, WaypointSystem waypoints)
    {
        // A room or Other-area transfer belongs to this plan, not to a corpse detour.
        // Finish it before yielding to a queued kill, keeping its claim and task intact.
        if (_actor != null && (OwnsMarkedLoot(_actor, _actor.Objective.Location)
            || CollectingCustomArea && _loot != null && _actor.Objective.Location == _loot)
            && CorpseEscort.InFlight(_actor)) return false;
        return HasPendingOwnKill(squad, waypoints);
    }

    internal static bool HasPendingOwnKill(Squad squad, WaypointSystem waypoints)
    {
        if (squad.CorpseEscort.Active) return true;
        foreach (var member in squad.Members)
        {
            if (!member.IsActive || member.Bot.IsDead || member.SoloExtractRequested) continue;
            // Transfers can outlive the completed-body marker. Do not interrupt them.
            if (CorpseEscort.InFlight(member)) return true;
            var current = member.Objective.Location;
            if (current?.Category == WaypointCategory.Corpse && current.Target != null
                && GotoObjectiveAction.IsLootableForAgent(member, current)
                && member.OwnKillCorpseIds.Contains(current.Id)
                && !squad.CompletedPoiIds.Contains(current.Id) && !member.ValueSkippedPoiIds.Contains(current.Id)
                && member.Objective.Status is not (ObjectiveStatus.Finished or ObjectiveStatus.Failed)) return true;
            var pending = waypoints.TryGetNextOwnKillCorpseForAgent(squad, member);
            if (pending != null && GotoObjectiveAction.IsLootableForAgent(member, pending)) return true;
        }
        return false;
    }

    private bool Pause(Squad squad, WaypointSystem waypoints, string reason)
    {
        waypoints.CancelOperationWork(this);
        if (!_paused) { Release(squad, waypoints); _loot = null; _actor = null; _pointRepair = null; _paused = true; _customArrived = false; }
        if (Status != reason) SetStatus(squad, reason);
        if (_current < 0) ResetRanking();
        _nextWork = Time.time + .25f;
        return false;
    }

    private void ResetRanking()
    { _ranking = false; _candidate = 0; _best = -1; _bestLength = float.MaxValue; _bestRoute = null; _ranker.Reset(); }

    // Spawn rush rolls among nearby sectors; surveying every distant route defeats an opening rush.
    // Other rush types retain one route query per work tick, including staged estimates.
    private bool SelectNext(Squad squad, WaypointSystem waypoints)
    {
        if (_visited >= Count) return End(squad, waypoints, FinishReason());
        if (!_ranking) { _ranking = true; _sortOrigin = _actor.Position; SetStatus(squad, "planning route"); }
        if (Kind == "Spawn")
        {
            var nearest = float.MaxValue;
            for (var index = 0; index < _sites.Count; index++)
            {
                if (_states[index] != null) continue;
                var distance = Vector3.Distance(_sortOrigin, Position(_sites[index]));
                if (distance < nearest) nearest = distance;
            }
            _nearbySpawnCandidates = 0;
            _nearbySpawnRadius = nearest * NearbySpawnDistanceFactor;
            for (var index = 0; index < _sites.Count; index++)
            {
                if (_states[index] != null) continue;
                var distance = Vector3.Distance(_sortOrigin, Position(_sites[index]));
                if (distance > _nearbySpawnRadius) continue;
                // Reservoir sampling gives every nearby sector the same chance without a sorted list.
                if (UnityEngine.Random.Range(0, ++_nearbySpawnCandidates) == 0)
                { _bestLength = distance; _best = index; }
            }
            _bestRoute = "nearby spawn random";
        }
        else
        {
            while (_candidate < _sites.Count && _states[_candidate] != null) _candidate++;
            if (_candidate < _sites.Count)
            {
                var index = _candidate; var site = _sites[index]; var target = Position(site);
                if (!_ranker.Step(_sortOrigin, target, out var length, out var source)) return true;
                ReportRoute(squad, site, length, source, false);
                if (Kind == "Marked" && site.DoorId == "door_Reserve_Base_Casarms_00042"
                    && RecordedRouteLibrary.Has(waypoints.RecordedMap, "reserve-marked") && length == float.MaxValue)
                    length = Vector3.Distance(_sortOrigin, target);
                if (length < _bestLength) { _bestLength = length; _best = index; _bestRoute = source; }
                _candidate++; _ranker.Reset();
                return true;
            }
        }
        if (_best < 0) return End(squad, waypoints, FinishReason());
        ReportRoute(squad, _sites[_best], _bestLength, _bestRoute, true);
        _current = _best; _lastSiteName = _sites[_current].Name; _states[_current] = "current"; _sequence[_current] = _processed + 1;
        Main.Position = Position(_sites[_current]); Main.CellCoords = waypoints.WorldToCell(Main.Position);
        Main.ZoneFloorId = _sites[_current].FloorId;
        _elapsed = _held = 0; _route.Reset(); _approachHistory.Clear(); RememberApproach(_actor.Position); SetStatus(squad, "approach");
        ContinueSpawnSearch(waypoints);
        return true;
    }

    private void ContinueSpawnSearch(WaypointSystem waypoints)
    {
        if (Kind != "Spawn") return;
        _nextWork = Time.time;
        waypoints.ContinueOperationWork(this);
    }

    private void ReportRoute(Squad squad, RushPoint site, float cost, string source, bool selected)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var detail = $"kind={Kind} site={site.Name} cost={cost:F1}m source={source} origin={_sortOrigin}";
        if (Kind == "Spawn") detail += $" candidates={_nearbySpawnCandidates} radius={_nearbySpawnRadius:F1}m";
        Log.Info($"{(selected ? "RUSH SELECT" : "RUSH ROUTE")}: {squad} {detail}");
        PerformanceJournal.Event(selected ? "rush-select" : "rush-route", squad.Leader?.Bot?.Profile?.Id, detail, squad.Id);
    }

    private bool RepairDestination(Squad squad, WaypointSystem waypoints, RushPoint site, Door door)
    {
        var authored = _inside ? LootPosition(site) : Position(site);
        var seed = authored;
        var threshold = Vector3.zero;
        if (door != null)
        {
            threshold = door.GetInteractionParameters(_actor.Position).InteractionPosition;
            seed = _inside ? RoomCenter(site) : threshold;
            seed.y = threshold.y;
        }
        bool Allowed(Vector3 point)
        {
            if (!waypoints.RepairFloor(site, point)) return false;
            if (door == null) return true;
            if (Mathf.Abs(point.y - threshold.y) > 2f) return false;
            if (!_inside) return (point - threshold).sqrMagnitude <= 4f;
            if (_room.HasNativeVolume) return _room.InNativeVolume(point);
            // Without a native room volume, keep the authored side of the doorway.
            var pivot = door.transform.position;
            var dx = authored.x - pivot.x;
            var dz = authored.z - pivot.z;
            return dx * dx + dz * dz > .01f && (point.x - pivot.x) * dx + (point.z - pivot.z) * dz > 0f
                && (point.x - authored.x) * (point.x - authored.x) + (point.z - authored.z) * (point.z - authored.z) <= 2.25f;
        }
        var part = _inside ? "interior" : "approach";
        if (!waypoints.RepairPoint(site, part, authored, seed, _actor, Allowed, ref _pointRepair, out var corrected))
        {
            _nextWork = Time.time;
            waypoints.ContinueOperationWork(this);
            return true;
        }
        var repair = _pointRepair;
        _pointRepair = null;
        if (!corrected.HasValue)
        {
            if (Kind == "Custom" && repair.HasDisconnectedSurface && OperationRouteSearch.CanStageFrom(_actor.Position, seed))
            {
                // Let the route search verify intermediate legs. Near the site it must
                // validate the authored floor again before a camp or loot order can start.
                _stageCustomApproach = true;
                _route.Reset();
                _nextWork = Time.time;
                waypoints.ContinueOperationWork(this);
                return true;
            }
            ReportInvalidDestination(squad, site, authored);
            return Skip(squad, waypoints, repair.Outcome switch
            {
                "ambiguous-floors" => "ambiguous floors near configured point",
                "no-reachable-surface" => "no reachable correction for configured point",
                _ => "no NavMesh near configured point"
            });
        }
        var copy = site.Copy();
        _stageCustomApproach = false;
        var position = corrected.Value;
        if (_inside)
        {
            _repairedInterior = true;
            copy.LootX = position.x; copy.LootY = position.y; copy.LootZ = position.z;
            _room = waypoints.MarkedRoom(copy);
        }
        else
        {
            _repairedApproach = true;
            copy.X = position.x; copy.Y = position.y; copy.Z = position.z;
        }
        _sites[_current] = copy;
        Main.Position = position; Main.CellCoords = waypoints.WorldToCell(position);
        _route.Reset(); _approachHistory.Clear();
        _nextWork = Time.time;
        waypoints.ContinueOperationWork(this);
        return true;
    }

    private void ReportInvalidDestination(Squad squad, RushPoint site, Vector3 target)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        // One record per rejected site, using the existing sliced route probes.
        // Never snap a malformed target onto a different floor or spend its travel
        // timeout searching for stairs to a destination with no local NavMesh.
        var detail = $"kind={Kind} site={site.Name} id={site.Id} mode={site.CustomMode} target={target} from={_actor.Position}"
            + $" reason=no-local-mesh {_route.Diagnostics}";
        Log.Info($"RUSH TARGET REJECTED: {squad} {detail}");
        PerformanceJournal.Event("rush-target-rejected", _actor.Bot.Profile.Id, detail, squad.Id);
    }

    private bool Visit(Squad squad, WaypointSystem waypoints)
    { _states[_current] = "visited"; _visited++; return Advance(squad, waypoints); }
    private bool Skip(Squad squad, WaypointSystem waypoints, string reason)
    {
        var navigationFailure = reason is "no NavMesh near configured point" or "ambiguous floors near configured point"
            or "no reachable correction for configured point" or "room entrance stalled" or "travel timeout" or "unreachable"
            or "movement failed" or "door interaction point unreachable" or "door interaction failed";
        if (navigationFailure && _current >= 0 && Kind == "Marked"
            && _sites[_current].DoorId == "door_Reserve_Base_Casarms_00042")
        {
            var active = _recorded.Active;
            if (_recorded.Enable(waypoints.RecordedMap, "reserve-marked", _actor.Position))
            {
                if (!active) Log.Info($"RUSH FALLBACK: {squad} site={Step} reason={reason}");
                return RetryRecorded(squad, waypoints);
            }
        }
        if (_current < 0) return End(squad, waypoints, "failed: " + reason);
        _lastFailure = reason; _skipped++;
        _states[_current] = "skipped: " + reason; return Advance(squad, waypoints);
    }
    private bool Advance(Squad squad, WaypointSystem waypoints)
    {
        SetStatus(squad, _states[_current]); Release(squad, waypoints); _processed++;
        _pointRepair = null; _repairedApproach = _repairedInterior = _stageCustomApproach = false;
        _bestDoorGap = float.MaxValue;
        _current = -1; _retries = _attempts = 0; _lastDoorRequest = null; ResetRanking();
        _customArrived = _customCampDone = false; _customCampHeld = _customLootHeld = 0;
        _recorded.Reset(); _searchingRecorded = false;
        _elapsed = _held = _interactAt = _roomIdle = 0; _inside = _doorApproach = false; _loot = null; _room = null; _roomLooters.Clear(); _attempted.Clear(); _route.Reset();
        _roomEntryIdle = 0; _roomEntryBestDistance = float.MaxValue;
        if (_visited >= Count || _processed >= _sites.Count) return End(squad, waypoints, FinishReason());
        SetStatus(squad, "planning route"); return true;
    }

    private bool RetryRecorded(Squad squad, WaypointSystem waypoints)
    {
        Release(squad, waypoints); _route.Reset(); _recorded.ResetSearch(); _approachHistory.Clear();
        _bestDoorGap = float.MaxValue;
        _retries = _attempts = 0; _lastDoorRequest = null; _elapsed = _roomEntryIdle = 0; _searchingRecorded = false;
        _nextWork = Time.time + 3f; return true;
    }

    private string FinishReason() => Kind == "Boss"
        ? _skipped > 0 ? "failed: incomplete boss search" : "boss not found"
        : _visited >= Count ? "completed" : "failed: " + (_lastFailure ?? "no reachable sector");

    private void RememberApproach(Vector3 point)
    {
        // Prevent repeated sideways detours from bouncing between the same anchors.
        if (_approachHistory.Count >= 32) _approachHistory.RemoveAt(0);
        _approachHistory.Add(point);
    }
    internal bool End(Squad squad, WaypointSystem waypoints, string reason)
    {
        waypoints.CancelOperationWork(this);
        if (Sniper != null) return Sniper.End(squad, waypoints, reason, Main);
        Release(squad, waypoints); Main.Completed = true;
        if (_current >= 0 && _states[_current] == "current") _states[_current] = reason;
        SetStatus(squad, reason); return false;
    }
    private void SetStatus(Squad squad, string state)
    {
        Status = state; Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        Log.Info($"RUSH: {squad} kind={Kind} step={Mathf.Min(_processed + 1, Count)}/{Count} site={Step} state={state}");
    }
    internal Orbit.Api.OrbitRushPoint[] Snapshot()
    {
        if (Sniper != null) return Sniper.Snapshot();
        var result = new Orbit.Api.OrbitRushPoint[_sites.Count];
        for (var i = 0; i < result.Length; i++)
        { var p = _sites[i]; result[i] = new() { Id = p.Id, Name = p.Name, X = p.X, Y = p.Y, Z = p.Z,
            State = _states[i] ?? "pending", Sequence = _sequence[i], Radius = p.Radius }; }
        return result;
    }
    private static Vector3 Position(RushPoint p) => new(p.X, p.Y, p.Z);
    private static Vector3 LootPosition(RushPoint p) => new(p.LootX, p.LootY, p.LootZ);
    private Vector3 RoomCenter(RushPoint site) => _room?.Center ?? LootPosition(site);
    private void HoldRoomMembers(Squad squad, WaypointSystem waypoints)
    {
        foreach (var member in squad.Members)
        {
            if (member == _actor || !member.IsActive || member.SoloExtractRequested || CorpseEscort.InFlight(member)) continue;
            if (!_orders.TryGetValue(member, out var order) || member.Objective.Location != order)
                SetOrder(member, Point(waypoints, _room.Center, "Hold marked room"), waypoints);
        }
    }
    private static Waypoint Point(WaypointSystem w, Vector3 position, string name, float radius = 1f)
        => new(w.NewRuntimeWaypointId(), WaypointCategory.Synthetic, name, position, radius * radius, new(), new(), null);
    private void SetOrder(Agent member, Waypoint point, WaypointSystem w)
    {
        if (member.Objective.Location != null && member.Objective.Location != point) w.ReleaseClaim(member.Objective.Location.Id, member.Id);
        _orders[member] = point; member.Objective.Location = point; member.Objective.SplinterParent = null;
        member.Objective.ArrivalPath = null; member.Objective.Status = ObjectiveStatus.None;
        member.Objective.DispatchTime = Time.time; member.Guard.CoverPoint = null; member.Look.Target = null;
    }
    private void Release(Squad squad, WaypointSystem w)
    {
        foreach (var pair in _orders)
        {
            if (pair.Key.Objective.Location == pair.Value)
            { pair.Key.Objective.Location = null; pair.Key.Objective.SplinterParent = null; pair.Key.Objective.Status = ObjectiveStatus.None; pair.Key.Guard.CoverPoint = null; pair.Key.Look.Target = null; }
            if (squad.Objective.Location == pair.Value) squad.Objective.Location = null;
            w.ReleaseClaim(pair.Value.Id, pair.Key.Id);
        }
        _orders.Clear();
        _customSlots.Clear();
        ClearCustomCamp();
    }
}
