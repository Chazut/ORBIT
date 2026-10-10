using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

// Ordinary POI travel stays opportunistic. When it stops advancing toward a main,
// commit to bounded native route legs instead of circulating through fresh nearby POIs.
internal sealed class MainObjectiveApproach : IObjectiveWork
{
    private readonly OperationRouteSearch _route = new();
    private readonly OperationProgress _progress = new();
    private readonly List<Vector3> _history = new();
    private readonly Dictionary<Agent, Waypoint> _orders = new();
    private readonly Dictionary<MainObjective, float> _deferred = new();
    private MainObjective _target;
    private Squad _squad;
    private WaypointSystem _waypoints;
    private float _lastTick, _elapsed, _lastProgress, _nextSearch;
    private int _failures;
    private bool _active, _paused;
    private Vector3 _leg;

    public void ResumeObjectiveWork() => Tick(_squad, _waypoints, WaypointSystem.ObjectiveCombat(_squad));
    internal bool Owns(Agent agent) => _active && !_paused && _orders.TryGetValue(agent, out var point)
        && agent.Objective.Location == point;

    internal MainObjective Select(Squad squad)
    {
        if (_target != null && squad.MainObjectives?.Contains(_target) == true
            && _target.CanPursue(squad.MainObjectives)) return _target;
        MainObjective nearest = null;
        var distance = float.MaxValue;
        if (squad.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
            {
                if (!main.CanPursue(squad.MainObjectives)
                    || _deferred.TryGetValue(main, out var until) && Time.time < until) continue;
                var gap = (main.Position - squad.Leader.Position).sqrMagnitude;
                if (gap < distance) { nearest = main; distance = gap; }
            }
        return nearest;
    }

    internal bool Tick(Squad squad, WaypointSystem waypoints, bool combat)
    {
        _squad = squad; _waypoints = waypoints;
        var now = Time.time;
        var dt = _lastTick > 0 ? Mathf.Clamp(now - _lastTick, 0, 1) : 0;
        _lastTick = now;
        var leader = squad.Leader;
        if (squad.ExtractRequested || leader == null || squad.MainObjectives == null)
        { End(squad, waypoints); return false; }
        var selected = Select(squad);
        if (selected != _target)
        {
            End(squad, waypoints); _target = selected;
            _progress.Reset(); _elapsed = _lastProgress = 0;
        }
        if (_target == null || _target.Type is MainObjectiveType.Rush or MainObjectiveType.ExtractCamp
            || _target.Type == MainObjectiveType.Kills && (_target.KillAmbush || _target.KillsRoamStartedAt > 0)
            || _target.Type == MainObjectiveType.LootValue && _target.LootValueEnteredAt > 0)
        { End(squad, waypoints); return false; }
        if (combat || !leader.IsActive || now < squad.GhostFightUntil || squad.Operation != null
            || squad.Camp.Active || squad.Camp.PendingAirdrop != null || squad.Camp.RetryMain != null
            || squad.InvestigateNoisePosition.HasValue || squad.PreInterruptObjectiveLocation != null
            || RushPlan.HasPendingOwnKill(squad, waypoints))
        {
            Release(squad, waypoints); _paused = true; return false;
        }
        if (_paused) { _paused = false; dt = 0; _route.Reset(); }
        _elapsed += dt;
        if (_progress.Observe(leader.Position, _target.Position, _elapsed)) _lastProgress = _elapsed;
        if (!_active)
        {
            if (_elapsed - _lastProgress < 60f) return false;
            _active = true;
            Report(squad, "started", "no progress toward main for 60s");
        }
        // A selected operation uses its own interaction-aware navigation and fallback.
        if (_target.Operation != null) return false;
        if (waypoints.WorldToCell(leader.Position) == _target.CellCoords
            && waypoints.MatchesZoneFloorAtTarget(_target.ZoneFloorId, _target.Position, leader.Position)
            && (_target.Type != MainObjectiveType.Quest || Vector3.Distance(leader.Position, _target.Position) < 3f))
        { End(squad, waypoints); return false; }
        if (_orders.TryGetValue(leader, out var order) && leader.Objective.Location == order)
        {
            _progress.ObserveLeg(leader.Position, _elapsed);
            if (leader.Objective.Status != ObjectiveStatus.Failed
                && Vector3.Distance(leader.Position, _leg) > 2f)
            {
                if (_elapsed - _lastProgress < 90f || _progress.AdvancingAlongLeg(_elapsed)) return true;
                Remember(_leg); Release(squad, waypoints); _route.Reset();
                return Retry(squad, waypoints, "route stopped advancing");
            }
            Remember(leader.Position); _progress.ReachedLeg();
            Release(squad, waypoints); _route.Reset();
            if (_progress.Stalled(_elapsed)) return Retry(squad, waypoints, "repeated legs without main progress");
        }
        if (now < _nextSearch || !waypoints.TryOperationWork(this)) return true;
        _nextSearch = now + .1f;
        if (!_route.Find(leader.Position, _target.Position, out _leg, out _, _failures, _history, floorAware: true))
        {
            if (_route.Pending) return true;
            return Retry(squad, waypoints, "no advancing native route");
        }
        _progress.BeginLeg(_route.RouteCorners, _elapsed);
        foreach (var member in squad.Members)
        {
            if (!member.IsActive || member.SoloExtractRequested || member.Bot.IsDead) continue;
            var point = new Waypoint(waypoints.NewRuntimeWaypointId(), WaypointCategory.Synthetic,
                "Approach " + _target.Type, _leg, 1f, new(), new(), null);
            if (member.Objective.Location != null) waypoints.ReleaseClaim(member.Objective.Location.Id, member.Id);
            _orders[member] = point;
            member.Objective.Location = point; member.Objective.SplinterParent = null;
            member.Objective.Status = ObjectiveStatus.None; member.Objective.ArrivalPath = null;
            member.Objective.DispatchTime = now;
            member.Guard.CoverPoint = null; member.Look.Target = null;
            if (member == leader) squad.Objective.Location = point;
        }
        squad.Objective.Status = SquadObjectiveState.Active;
        squad.Objective.RepickRequested = false;
        Report(squad, "leg", $"point={_leg} remaining={Vector3.Distance(_leg, _target.Position):F1}m");
        return true;
    }

    private bool Retry(Squad squad, WaypointSystem waypoints, string reason)
    {
        Release(squad, waypoints); _route.Reset(); _nextSearch = Time.time + 3f;
        if (++_failures < 3) return true;
        Report(squad, "deferred", reason);
        // Keep the objective pending. Another main may have a usable route from this side of the map.
        _deferred[_target] = Time.time + 60f;
        End(squad, waypoints);
        return false;
    }

    private void Remember(Vector3 position)
    { if (_history.Count >= 32) _history.RemoveAt(0); _history.Add(position); }

    internal void End(Squad squad, WaypointSystem waypoints)
    {
        Release(squad, waypoints); _target = null; _active = _paused = false;
        _failures = 0; _route.Reset(); _history.Clear(); _progress.Reset();
        _elapsed = _lastProgress = 0; _nextSearch = 0;
    }

    private void Release(Squad squad, WaypointSystem waypoints)
    {
        waypoints.CancelOperationWork(this);
        foreach (var pair in _orders)
        {
            if (pair.Key.Objective.Location == pair.Value)
            {
                pair.Key.Objective.Location = null; pair.Key.Objective.SplinterParent = null;
                pair.Key.Objective.Status = ObjectiveStatus.None;
            }
            if (squad.Objective.Location == pair.Value) squad.Objective.RepickRequested = true;
            waypoints.ReleaseClaim(pair.Value.Id, pair.Key.Id);
        }
        _orders.Clear();
    }

    private void Report(Squad squad, string state, string reason)
    {
        var detail = $"type={_target.Type} target={_target.Position} state={state} reason={reason}";
        Log.Info($"MAIN APPROACH: {squad} {detail}");
        PerformanceJournal.Event("main-approach", squad.Leader.Bot.Profile.Id, detail, squad.Id);
    }
}
