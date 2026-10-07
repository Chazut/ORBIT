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

internal sealed class RushPlan : IObjectiveWork
{
    private Squad _squad;
    private WaypointSystem _waypoints;
    void IObjectiveWork.ResumeObjectiveWork() => Tick(_squad, _waypoints, WaypointSystem.ObjectiveCombat(_squad));
    internal readonly string Kind;
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
    private readonly RushRouteEstimate _ranker = new();
    private const float NearbySpawnDistanceFactor = 1.5f;
    private readonly NavMeshPath _path = new();
    private Agent _actor;
    private string _lastSiteName;
    private Waypoint _loot;
    private MarkedRoomScope _room;
    private readonly HashSet<Agent> _roomLooters = new();
    private float _roomIdle;
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
    { Kind = kind; _sites = sites; _states = new string[sites.Count]; _sequence = new int[sites.Count]; _searchScale = searchScale;
        if (kind == "Sniper") Sniper = new SniperPlan(sites[0], style ?? new()); }

    internal bool Owns(Agent agent) => Sniper != null ? Sniper.Owns(agent) : !Main.Completed && !_paused && !agent.SoloExtractRequested
        && (!_orders.TryGetValue(agent, out var point) || agent.Objective.Location == point);
    internal bool CollectingMarkedRoom => Kind == "Marked" && !Main.Completed && _inside;
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
        if (!ServerConfig.Rush.Allows(squad.Leader?.BotCategory) || squad.ExtractRequested)
            return End(squad, waypoints, "cancelled for extraction or settings");
        if (Kind == "Spawn" && WaypointSystem.RushRaidSeconds > ServerConfig.Rush.SpawnDeadline)
            return End(squad, waypoints, "opening window expired");
        // Native perception, never a lookup of the boss's actual spawn or global alive/dead state.
        if (Kind == "Boss" && now >= _nextWork)
        {
            var found = waypoints.PerceivedRushBoss(squad, _sites[0].Boss);
            if (found != null) return End(squad, waypoints, found);
        }
        if (combat || now < squad.GhostFightUntil)
            return Pause(squad, waypoints, "paused");
        // Recheck here as well as in the strategy: queued route work can resume between
        // strategy ticks. A kill's approach must survive the end of the combat pause.
        if (Kind == "Spawn" && HasOwnKillDetour(squad, waypoints))
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
            Release(squad, waypoints); _actor = null; _loot = null; _route.Reset();
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
        if (_paused) { _paused = false; _route.Reset(); delta = 0; SetStatus(squad, _current < 0 ? "planning route" : "approach"); }
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
        if (Status != "looting") _elapsed += delta;
        if (_elapsed > ServerConfig.Rush.TravelTimeout && Status != "searching" && Status != "looting")
            return Skip(squad, waypoints, "travel timeout");
        if (Status is "searching" or "looting") _held += delta;
        if (Status == "looting" && _loot == null && (_roomIdle += delta) > ServerConfig.Rush.TravelTimeout)
            return Skip(squad, waypoints, "room loot stalled");
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
            if (!_route.Find(_actor.Position, target, out _anchor, out var final, _retries, _approachHistory))
            {
                if (_route.Pending) { ContinueSpawnSearch(waypoints); return true; }
                Log.Info($"RUSH APPROACH: {squad} kind={Kind} site={site.Name} from={_actor.Position} target={target} samples={_route.Samples} partial={_route.PartialPaths} invalid={_route.InvalidPaths} attempt={_retries + 1}/3");
                _route.Reset(); _nextWork = now + 2;
                if (++_retries >= 3) return Skip(squad, waypoints, "unreachable");
                return true;
            }
            _partial = !final;
            if (_partial) Log.Info($"RUSH APPROACH: {squad} kind={Kind} site={site.Name} leg={_anchor} target={target} remaining={Vector3.Distance(_anchor, target):F1}m");
            SetOrder(_actor, Point(waypoints, _anchor, site.Name), waypoints);
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
        if (_partial && (_actor.Position - _anchor).sqrMagnitude <= 9f)
        {
            RememberApproach(_actor.Position);
            Release(squad, waypoints); _route.Reset(); _retries = 0; return true;
        }
        // Arrival at the handle takes precedence over a stopped mover's failure. Let the
        // normal door interaction settle without consuming navigation retries at the handle.
        if (door != null && !_inside && _doorApproach && !_partial
            && (_actor.Position - _anchor).sqrMagnitude <= 1f)
            return InteractDoor(squad, waypoints, site, door);
        if (_actor.Objective.Status == ObjectiveStatus.Failed)
        {
            RememberApproach(_anchor);
            Release(squad, waypoints); _route.Reset();
            if (++_retries >= 3) return Skip(squad, waypoints, "movement failed");
            return true;
        }
        if (Status == "searching" && _held >= site.SearchSeconds * _searchScale) return Visit(squad, waypoints);
        if (Status != "searching" && (_actor.Position - _anchor).sqrMagnitude > (door != null ? 1f : 9f)) return true;
        if (_partial) return true;
        if (door != null && !_inside && !_doorApproach)
        {
            _doorApproach = true; Release(squad, waypoints); _route.Reset(); _approachHistory.Clear(); _retries = 0; return true;
        }
        if (door != null && !_inside) return InteractDoor(squad, waypoints, site, door);
        if (Kind == "Marked") { _held = 0; SetStatus(squad, "looting"); return true; }
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
                && NavMesh.CalculatePath(_actor.Position, hit.position, NavMesh.AllAreas, _path)
                && _path.status == NavMeshPathStatus.PathComplete)
                SetOrder(_actor, Point(waypoints, hit.position, "Search " + site.Name), waypoints);
        }
        return true;
    }

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
            var interaction = door.GetInteractionParameters(_actor.Position).InteractionPosition;
            if ((_actor.Position - interaction).sqrMagnitude > 4f)
            {
                Release(squad, waypoints); _route.Reset();
                if (++_retries >= 3) return Skip(squad, waypoints, "door interaction point unreachable");
                return true;
            }
            _actor.Look.Target = door.transform.position;
            var now = Time.time;
            if (now < _interactAt || !door.Operatable || door.DoorState == EDoorState.Interacting || door.InteractingPlayer != null) return true;
            _interactAt = now + 5;
            if (++_attempts > 3) return Skip(squad, waypoints, "door interaction failed");
            if (door.DoorState == EDoorState.Locked)
            {
                if (!MultiStepAccess.CanForceUnlock(door)) return Skip(squad, waypoints, "door requires power");
                door.Unlock(); Orbit.Api.OrbitDoorEvents.Raise(door, Orbit.Api.OrbitDoorEvents.Operation.Unlock);
                SetStatus(squad, "unlocking door");
            }
            else if (waypoints.OpenRushDoor?.Invoke(_actor, door) == true)
                SetStatus(squad, "opening door");
        }
        catch (Exception error) { Log.Warning($"RUSH: {squad} door={site.DoorId} error={error}"); }
        return true;
    }

    private static bool HasOwnKillDetour(Squad squad, WaypointSystem waypoints)
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
        if (!_paused) { Release(squad, waypoints); _loot = null; _actor = null; _paused = true; }
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

    private bool Visit(Squad squad, WaypointSystem waypoints)
    { _states[_current] = "visited"; _visited++; return Advance(squad, waypoints); }
    private bool Skip(Squad squad, WaypointSystem waypoints, string reason)
    {
        if (_current < 0) return End(squad, waypoints, "failed: " + reason);
        _lastFailure = reason; _skipped++;
        _states[_current] = "skipped: " + reason; return Advance(squad, waypoints);
    }
    private bool Advance(Squad squad, WaypointSystem waypoints)
    {
        SetStatus(squad, _states[_current]); Release(squad, waypoints); _processed++;
        _current = -1; _retries = _attempts = 0; ResetRanking();
        _elapsed = _held = _interactAt = _roomIdle = 0; _inside = _doorApproach = false; _loot = null; _room = null; _roomLooters.Clear(); _attempted.Clear(); _route.Reset();
        _roomEntryIdle = 0; _roomEntryBestDistance = float.MaxValue;
        if (_visited >= Count || _processed >= _sites.Count) return End(squad, waypoints, FinishReason());
        SetStatus(squad, "planning route"); return true;
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
    private static Waypoint Point(WaypointSystem w, Vector3 position, string name)
        => new(w.NewRuntimeWaypointId(), WaypointCategory.Synthetic, name, position, 1, new(), new(), null);
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
    }
}
