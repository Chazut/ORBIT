using System.Collections.Generic;
using EFT.Interactive;
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
    private enum BunkerStage { None, Gate, Room }
    private BunkerStage _stage;
    private OperationStep _gateStep;
    private Collider _volume;
    private bool _holdingGate, _finalLeg;
    private float _nextGateCheck, _lastAdvance;
    private int _bunkerRetries;

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
        _enabled = _walking = _holdingGate = false;
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
            _stage = BunkerStage.None;
        }
        if (Time.time >= _nextEligibility)
        {
            _nextEligibility = Time.time + 1f;
            if (!waypoints.CanUseExfil(agent, exit))
            {
                if (WoodsBunkerAccess.Applies(exit))
                { AbandonBunker("spawn, faction or exit requirements changed"); return true; }
                Report("unavailable", "spawn, faction or exit requirements changed");
                Pause(); MovementSystem.StopExfilApproach(agent);
                ExfilArrival.Abandon(agent, exit);
                return true;
            }
        }
        var bunker = WoodsBunkerAccess.Applies(exit);
        if (bunker && UpdateBunkerStage(exit)) return true;
        var target = WoodsBunkerAccess.Destination(exit);
        // ZB-014 owns its final approach all the way into the trigger, including inside the loose 25m radius.
        if (!bunker && ((target - agent.Position).sqrMagnitude <= 625f || ExfilArrival.IsInside(agent, exit)))
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
        if (_progress.Observe(agent.Position, target, _elapsed)) { _lastAdvance = _elapsed; _bunkerRetries = 0; }
        if (_stage == BunkerStage.Gate && OperateGate(exit.ExfilGate, now)) return true;
        if (_walking)
        {
            _progress.ObserveLeg(agent.Position, _elapsed);
            if (!(bunker && _finalLeg) && (agent.Position - _leg).sqrMagnitude <= 4f)
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
                return true;
            }
            else return true;
        }
        if (now < _nextSearch || !waypoints.TryOperationWork(this)) return true;
        if (!_route.Pending || (agent.Position - _searchOrigin).sqrMagnitude > 9f)
        { _route.Reset(); _searchOrigin = agent.Position; }
        if (!_route.Find(agent.Position, target, out _leg, out var final,
                _failures, _history, floorAware: true, allowedPoint: AllowedPoint,
                finalPoint: _stage == BunkerStage.Gate ? _gateStep.InteractionCheck
                    : _stage == BunkerStage.Room ? InsideRoom : null, localAccess: bunker))
        {
            if (_route.Pending) { waypoints.ContinueOperationWork(this); return true; }
            Retry("no connected approach; " + _route.Diagnostics);
            return true;
        }
        _progress.BeginLeg(_route.RouteCorners, _elapsed);
        _walking = true; _finalLeg = final; _issuedAt = _elapsed;
        objective.DispatchTime = now; objective.ArrivalPath = null;
        // Follow the verified leg directly, without travel-style detours around its endpoint.
        movement.MoveToByPath(agent, _leg, sprint: _sprint);
        Report("leg", $"point={_leg} final={final} remaining={Vector3.Distance(agent.Position, target):F1}m");
        return true;
    }

    private bool UpdateBunkerStage(Waypoint exit)
    {
        // The live state may change between eligibility checks or while another bot operates the gate.
        if (!WoodsBunkerAccess.Available(exit))
        { AbandonBunker("gate missing or extraction inactive"); return true; }
        if (ExfilArrival.IsInside(agent, exit))
        {
            Pause(); MovementSystem.StopExfilApproach(agent);
            agent.Objective.ExfilOutsideTriggerSince = -1f;
            agent.Objective.Status = ObjectiveStatus.Extracting;
            Report("inside", "gate open and feet inside ZB-014 trigger");
            return true;
        }
        var stage = exit.ExfilGate.DoorState == EDoorState.Open ? BunkerStage.Room : BunkerStage.Gate;
        if (stage != _stage)
        {
            Pause(); _history.Clear(); _progress.Reset();
            _stage = stage; _failures = _bunkerRetries = 0; _lastAdvance = _elapsed;
            _nextSearch = _nextGateCheck = 0;
            _gateStep = new OperationStep { Kind = OperationStepKind.Access, Object = exit.ExfilGate };
            _volume = ExfilArrival.BunkerVolume((ExfiltrationPoint)exit.Target);
            Report(stage == BunkerStage.Gate ? "gate" : "room", $"door={exit.ExfilGate.Id} state={exit.ExfilGate.DoorState}");
        }
        return false;
    }

    private bool OperateGate(Door gate, float now)
    {
        if ((gate.transform.position - agent.Position).sqrMagnitude > 16f)
        { _holdingGate = false; return false; }
        if (now < _nextGateCheck) return _holdingGate;
        _nextGateCheck = now + .25f;
        if (!_gateStep.CanInteract(agent.Position))
        { _holdingGate = false; return false; }
        if (!_holdingGate)
        {
            MovementSystem.StopExfilApproach(agent);
            _route.Reset(); _walking = false; _lastAdvance = _elapsed;
            _holdingGate = true;
        }
        if (_elapsed - _lastAdvance > 45f) AbandonBunker("native door interaction did not complete");
        else movement.UseBunkerGate(agent, gate);
        return true;
    }

    private static bool AllowedPoint(Vector3 point) => !DangerZones.IsInside(point);

    private bool InsideRoom(Vector3 point) => ExfilArrival.BunkerContains(_volume, point);

    private void AbandonBunker(string reason)
    {
        Report("unavailable", reason);
        Pause(); MovementSystem.StopExfilApproach(agent);
        if (agent.SoloExtractRequested) agent.SoloWoodsBunkerChoice = false;
        ExfilArrival.Abandon(agent, _exit, squadWide: !agent.SoloExtractRequested);
    }

    private void Retry(string reason)
    {
        if (_stage != BunkerStage.None && ++_bunkerRetries >= 3 && _elapsed - _lastAdvance >= 45f)
        { AbandonBunker(reason); return; }
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
