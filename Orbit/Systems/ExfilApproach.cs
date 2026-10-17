using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

// Keep the actual exit pinned while walking connected approach legs. Reaching a
// leg never completes the objective or starts extraction away from the exit.
internal sealed class ExfilApproach(Agent agent, MovementSystem movement, WaypointSystem waypoints) : IObjectiveWork
{
    private readonly OperationRouteSearch _route = new();
    private readonly OperationProgress _progress = new();
    private readonly List<Vector3> _history = new();
    private Waypoint _exit;
    private Vector3 _leg, _searchOrigin;
    private float _nextSearch, _nextEligibility, _lastTick, _elapsed, _issuedAt;
    private int _failures;
    private bool _enabled, _walking, _sprint;

    public void ResumeObjectiveWork()
    {
        // A queued search can outlive an action switch, death or reassignment.
        if (_enabled && agent.Objective.Location == _exit
            && agent.Objective.Status is ObjectiveStatus.None or ObjectiveStatus.Moving)
            Tick(_sprint);
        else Pause();
    }

    internal void Pause()
    {
        waypoints.CancelOperationWork(this);
        _enabled = _walking = false;
        _lastTick = 0;
        _route.Reset();
    }

    internal bool Tick(bool sprint)
    {
        var objective = agent.Objective;
        var exit = objective.Location;
        if (exit?.Category != WaypointCategory.Exfil || agent.Bot.IsDead
            || objective.Status is not (ObjectiveStatus.None or ObjectiveStatus.Moving))
        { Pause(); return false; }
        if (!agent.IsActive || MovementSystem.IsTravelPaused(agent)
            || Time.time < agent.Movement.DoorInteractHoldUntil
            || agent.IsDormant && agent.Squad != null && Time.time < agent.Squad.GhostFightUntil)
        { Pause(); return true; }
        if (_exit != exit)
        {
            Pause(); _exit = exit; _history.Clear(); _progress.Reset();
            _elapsed = _nextSearch = _nextEligibility = 0; _failures = 0;
        }
        if (Time.time >= _nextEligibility)
        {
            _nextEligibility = Time.time + 1f;
            if (!waypoints.CanUseExfil(agent, exit))
            {
                Report("unavailable", "spawn, faction or exit requirements changed");
                Pause(); MovementSystem.StopExfilApproach(agent);
                ExfilArrival.Abandon(agent, exit);
                return true;
            }
        }
        var target = exit.ExfilInteriorPosition ?? exit.Position;
        // Preserve native trigger arrival, payment, backpack and local entrance recovery.
        if ((target - agent.Position).sqrMagnitude <= 625f || ExfilArrival.IsInside(agent, exit))
        {
            if (_enabled) { Pause(); objective.Status = ObjectiveStatus.None; Report("final", "local exit approach"); }
            return false;
        }
        if (!_enabled)
        {
            _enabled = true;
            MovementSystem.StopExfilApproach(agent);
            objective.Status = ObjectiveStatus.Moving;
        }
        _sprint = sprint;
        var now = Time.time;
        _elapsed += _lastTick > 0 ? Mathf.Clamp(now - _lastTick, 0, 1) : 0;
        _lastTick = now;
        _progress.Observe(agent.Position, target, _elapsed);
        if (_walking)
        {
            _progress.ObserveLeg(agent.Position, _elapsed);
            if ((agent.Position - _leg).sqrMagnitude <= 4f)
            {
                OperationRouteSearch.Remember(_history, _leg);
                _progress.ReachedLeg(); _walking = false; _route.Reset();
                _failures = _progress.Stalled(_elapsed) ? Mathf.Min(2, _failures + 1) : 0;
                Report("reached", $"point={_leg}");
            }
            else if (_elapsed - _issuedAt > 2f
                && (agent.Movement.Status is MovementStatus.Failed or MovementStatus.Stopped
                    || _elapsed - _issuedAt >= 30f && !_progress.AdvancingAlongLeg(_elapsed)))
            {
                OperationRouteSearch.Remember(_history, _leg);
                Retry("leg stopped advancing");
            }
            else return true;
        }
        if (now < _nextSearch || !waypoints.TryOperationWork(this)) return true;
        if (!_route.Pending || (agent.Position - _searchOrigin).sqrMagnitude > 9f)
        { _route.Reset(); _searchOrigin = agent.Position; }
        if (!_route.Find(agent.Position, target, out _leg, out var final,
                _failures, _history, floorAware: true, allowedPoint: AllowedPoint))
        {
            if (_route.Pending) { waypoints.ContinueOperationWork(this); return true; }
            Retry("no connected approach; " + _route.Diagnostics);
            return true;
        }
        _progress.BeginLeg(_route.RouteCorners, _elapsed);
        _walking = true; _issuedAt = _elapsed;
        objective.DispatchTime = now; objective.ArrivalPath = null;
        // Follow the verified leg directly, without travel-style detours around its endpoint.
        movement.MoveToByPath(agent, _leg, sprint: _sprint);
        Report("leg", $"point={_leg} final={final} remaining={Vector3.Distance(agent.Position, target):F1}m");
        return true;
    }

    private static bool AllowedPoint(Vector3 point) => !DangerZones.IsInside(point);

    private void Retry(string reason)
    {
        _walking = false; _route.Reset(); MovementSystem.StopExfilApproach(agent);
        _failures = Mathf.Min(2, _failures + 1);
        _nextSearch = Time.time + (_failures < 2 ? 3f : 10f);
        Report("retry", reason);
        // Keep the authorized exit and extraction intent. No wrong-side fallback or remote despawn.
    }

    private void Report(string state, string reason)
    {
        var detail = $"exit={_exit.Name} state={state} reason={reason}";
        Log.Info($"EXFIL ROUTE: {agent} {detail}");
        PerformanceJournal.Event("exfil-route", agent.Bot.Profile?.Id, detail, agent.Squad?.Id ?? -1);
    }
}
