using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
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
    internal string Step => Sniper != null ? Sniper.Step : _current < 0 ? _lastSiteName ?? "Choose nearest reachable sector" : _sites[_current].Name;
    internal int Index => _visited;
    internal int Count => Kind == "Spawn" ? Mathf.Min(ServerConfig.Rush.SpawnSectors, _sites.Count) : _sites.Count;
    internal string Name => Kind == "Sniper" ? "Sniper overwatch" : Kind + " rush";
    private readonly List<RushPoint> _sites;
    private readonly string[] _states;
    private readonly int[] _sequence;
    private readonly float _searchScale;
    private readonly Dictionary<Agent, Waypoint> _orders = new();
    private readonly HashSet<int> _attempted = new();
    private readonly OperationRouteSearch _route = new();
    private readonly NavMeshPath _path = new();
    private Agent _actor;
    private string _lastSiteName;
    private Waypoint _loot;
    private int _current = -1, _candidate, _best = -1, _visited, _retries, _attempts;
    private float _bestLength = float.MaxValue, _nextWork, _elapsed, _held, _lastTick, _nextRoam, _interactAt;
    private bool _paused, _partial, _inside, _doorApproach;
    private Vector3 _anchor, _sortOrigin;

    internal RushPlan(string kind, List<RushPoint> sites, float searchScale, RushStyle style = null)
    { Kind = kind; _sites = sites; _states = new string[sites.Count]; _sequence = new int[sites.Count]; _searchScale = searchScale;
        if (kind == "Sniper") Sniper = new SniperPlan(sites[0], style ?? new()); }

    internal bool Owns(Agent agent) => Sniper != null ? Sniper.Owns(agent) : !Main.Completed && !_paused && !agent.SoloExtractRequested
        && (!_orders.TryGetValue(agent, out var point) || agent.Objective.Location == point);

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
        {
            waypoints.CancelOperationWork(this);
            if (!_paused) { Release(squad, waypoints); _loot = null; _paused = true; SetStatus(squad, "paused"); }
            _nextWork = now + .25f;
            return false;
        }
        if (_paused) { _paused = false; _actor = null; _route.Reset(); SetStatus(squad, "approach"); }
        _elapsed += delta;
        if (_elapsed > ServerConfig.Rush.TravelTimeout && Status != "searching" && Status != "looting")
            return Skip(squad, waypoints, "travel timeout");
        if (Status is "searching" or "looting") _held += delta;
        if (now < _nextWork) return true;
        if (_actor == null || !_actor.IsActive || !squad.Members.Contains(_actor) || _actor.SoloExtractRequested)
        {
            Release(squad, waypoints); _actor = null; _loot = null; _route.Reset();
            foreach (var member in squad.Members)
                if (member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member)) { _actor = member; break; }
            if (_actor == null) return End(squad, waypoints, "no available member");
        }
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
            if (_actor.Objective.Status is not (ObjectiveStatus.Finished or ObjectiveStatus.Failed)) return true;
            _attempted.Add(_loot.Id); waypoints.ReleaseClaim(_loot.Id, _actor.Id); _loot = null;
        }
        if (Status == "looting")
        {
            if (_held >= site.SearchSeconds * _searchScale) return Visit(squad, waypoints);
            if (!waypoints.TryOperationWork(this)) return true;
            _nextWork = now + .1f;
            _loot = waypoints.OperationLoot(_actor, LootPosition(site), _attempted, out var exhausted, site.Radius);
            if (_loot != null) SetOrder(_actor, _loot, waypoints);
            else if (exhausted) return Visit(squad, waypoints);
            return true;
        }
        if (!_orders.ContainsKey(_actor))
        {
            if (!waypoints.TryOperationWork(this)) return true;
            _nextWork = now + .1f;
            var target = _inside ? LootPosition(site) : Position(site);
            if (door != null && !_inside && _doorApproach) target = door.GetInteractionParameters(_actor.Position).InteractionPosition;
            if (!_route.Find(_actor.Position, target, out _anchor, out var final))
            {
                if (_route.Pending) return true;
                _route.Reset(); _nextWork = now + 2;
                if (++_retries >= 3) return Skip(squad, waypoints, "unreachable");
                return true;
            }
            _partial = !final;
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
        if (_actor.Objective.Status == ObjectiveStatus.Failed)
        {
            Release(squad, waypoints); _route.Reset();
            if (++_retries >= 3) return Skip(squad, waypoints, "movement failed");
            return true;
        }
        if (Status == "searching" && _held >= site.SearchSeconds * _searchScale) return Visit(squad, waypoints);
        if (Status != "searching" && (_actor.Position - _anchor).sqrMagnitude > (door != null && !_inside ? 1f : 9f)) return true;
        if (_partial) { Release(squad, waypoints); _route.Reset(); return true; }
        if (door != null && !_inside && !_doorApproach)
        {
            _doorApproach = true; Release(squad, waypoints); _route.Reset(); return true;
        }
        if (door != null && !_inside)
        {
            if (door.DoorState == EDoorState.Open)
            {
                _inside = true; Release(squad, waypoints); _route.Reset(); _elapsed = 0;
                Main.Position = LootPosition(site); Main.CellCoords = waypoints.WorldToCell(Main.Position);
                SetStatus(squad, "entering room"); return true;
            }
            var interaction = door.GetInteractionParameters(_actor.Position).InteractionPosition;
            if ((_actor.Position - interaction).sqrMagnitude > 4f)
            {
                Release(squad, waypoints); _route.Reset();
                if (++_retries >= 3) return Skip(squad, waypoints, "door interaction point unreachable");
                return true;
            }
            _actor.Look.Target = door.transform.position;
            if (now < _interactAt || !door.Operatable || door.DoorState == EDoorState.Interacting || door.InteractingPlayer != null) return true;
            _interactAt = now + 5;
            if (++_attempts > 3) return Skip(squad, waypoints, "door interaction failed");
            try
            {
                if (door.DoorState == EDoorState.Locked)
                {
                    if (!MultiStepAccess.CanForceUnlock(door)) return Skip(squad, waypoints, "door requires power");
                    door.Unlock(); Orbit.Api.OrbitDoorEvents.Raise(door, Orbit.Api.OrbitDoorEvents.Operation.Unlock);
                }
                else { door.LockForInteraction(); door.Interact(new InteractionResult(EInteractionType.Open));
                    Orbit.Api.OrbitDoorEvents.Raise(door, Orbit.Api.OrbitDoorEvents.Operation.Open); }
                SetStatus(squad, "unlocking door");
            }
            catch (Exception error) { Log.Warning($"RUSH: {squad} door={site.DoorId} error={error.Message}"); }
            return true;
        }
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

    // One candidate path per work tick; no burst of paths when a squad starts or advances.
    private bool SelectNext(Squad squad, WaypointSystem waypoints)
    {
        if (_visited >= Count) return End(squad, waypoints, "completed");
        if (_candidate == 0) { _sortOrigin = _actor.Position; SetStatus(squad, "planning route"); }
        while (_candidate < _sites.Count && _states[_candidate] != null) _candidate++;
        if (_candidate < _sites.Count)
        {
            var index = _candidate++; var site = _sites[index]; var target = Position(site);

            if (NavMesh.SamplePosition(target, out var hit, 3, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - target.y) <= 2.5f
                && NavMesh.CalculatePath(_sortOrigin, hit.position, NavMesh.AllAreas, _path)
                && _path.status == NavMeshPathStatus.PathComplete)
            {
                var length = PathHelper.TotalLength(_path.corners);
                if (length < _bestLength) { _bestLength = length; _best = index; }
            }
            // A partial distant route may become resolvable through staged approaches. Defer it until
            // complete routes have been visited, without pretending its straight distance is a path cost.
            return true;
        }
        if (_best < 0)
            for (var i = 0; i < _sites.Count; i++) if (_states[i] == null) { _best = i; break; }
        if (_best < 0) return End(squad, waypoints, Kind == "Boss" ? "boss not found" : "completed");
        _current = _best; _lastSiteName = _sites[_current].Name; _states[_current] = "current"; _sequence[_current] = _visited + 1;
        Main.Position = Position(_sites[_current]); Main.CellCoords = waypoints.WorldToCell(Main.Position);
        Main.ZoneFloorId = _sites[_current].FloorId;
        _elapsed = _held = 0; _route.Reset(); SetStatus(squad, "approach");
        return true;
    }

    private bool Visit(Squad squad, WaypointSystem waypoints)
    { _states[_current] = "visited"; return Advance(squad, waypoints); }
    private bool Skip(Squad squad, WaypointSystem waypoints, string reason)
    {
        if (_current < 0) return End(squad, waypoints, reason);
        _states[_current] = "skipped: " + reason; return Advance(squad, waypoints);
    }
    private bool Advance(Squad squad, WaypointSystem waypoints)
    {
        SetStatus(squad, _states[_current]); Release(squad, waypoints); _visited++;
        _current = _best = -1; _candidate = _retries = _attempts = 0; _bestLength = float.MaxValue;
        _elapsed = _held = 0; _inside = _doorApproach = false; _loot = null; _attempted.Clear(); _route.Reset();
        if (_visited >= Count) return End(squad, waypoints, Kind == "Boss" ? "boss not found" : "completed");
        SetStatus(squad, "planning route"); return true;
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
        Log.Info($"RUSH: {squad} kind={Kind} step={Mathf.Min(_visited + 1, Count)}/{Count} site={Step} state={state}");
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
