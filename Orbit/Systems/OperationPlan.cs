using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>A persistent squad plan. All interactions occur at a reachable local point; combat pauses it.</summary>
internal sealed class OperationPlan
{
    internal readonly OperationDefinition Definition;
    internal MainObjective Main;
    internal OperationStep Current => _steps[Index];
    internal int Index { get; private set; }
    internal int Count => _steps.Count;
    internal string Status { get; private set; } = "pending";
    internal bool Active { get; private set; }
    private readonly List<OperationStep> _steps = new();
    private readonly Dictionary<Agent, Waypoint> _orders = new();
    private readonly List<CoverPoint> _covers = new();
    private readonly List<Vector3> _occupied = new();
    private readonly List<Vector3> _rejected = new();
    private readonly HashSet<int> _attemptedLoot = new();
    private Agent _actor;
    private float _lastTick, _elapsed, _nextWork, _interactionAt, _regroupAt = -1;
    private int _retries, _interactionRetries, _powerRetries;
    private bool _paused, _interactionPending;
    private Vector3 _anchor;
    private Waypoint _loot;
    private Waypoint _regroup;
    private readonly OperationRouteSearch _route = new();
    private bool _approachOnly;

    internal OperationPlan(OperationDefinition definition, bool alarm)
    {
        Definition = definition;
        foreach (var step in definition.Steps) if (!step.OptionalAlarm || alarm) _steps.Add(step);
    }

    internal void Begin(Squad squad)
    {
        Active = true;
        _lastTick = Time.time;
        Status = "approach";
        LogStep(squad);
    }

    internal void RefreshPending(WaypointSystem waypoints)
    {
        if (Active || Main?.Completed == true) return;
        var previous = Index;
        // Timed power (for example Hermetic) may expire before this plan starts.
        if (Index > 0 && Definition.Power != null && Definition.Power.DoorState == EDoorState.Shut) Index = 0;
        while (Index + 1 < Count && Current.Kind is OperationStepKind.Switch or OperationStepKind.Access && Satisfied(Current)) Index++;
        var targetChanged = false;
        if (Main != null)
        {
            targetChanged = Main.Position != Current.Position;
            Main.Position = Current.Position;
            Main.CellCoords = waypoints.WorldToCell(Main.Position);
        }
        if (Index != previous || targetChanged) Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
    }

    internal bool Owns(Agent agent) => Active && !_paused
        && (!_orders.TryGetValue(agent, out var point) || agent.Objective.Location == point);

    internal bool Tick(Squad squad, WaypointSystem waypoints, bool combat)
    {
        if (!Active) return false;
        var now = Time.time;
        var delta = Mathf.Clamp(now - _lastTick, 0, 1);
        _lastTick = now;
        if (!ServerConfig.MultiStep.Allows(squad.Leader?.BotCategory) || !Definition.AllowsCategory(squad.Leader?.BotCategory)
            || ServerConfig.MultiStep.Weight(Definition.Id) <= 0
            || (!Definition.Extraction && squad.ExtractRequested)) return End(squad, waypoints, "cancelled");
        // Paused orders may be replaced by the combat rally. Rebuild from current positions on resume.
        if (combat || now < squad.GhostFightUntil)
        {
            if (_regroupAt >= 0) _regroupAt += delta;
            if (!_paused) { ReleaseOrders(squad, waypoints); _paused = true; Status = "paused"; LogStep(squad); }
            return false;
        }
        if (_paused) { _paused = false; _actor = null; _route.Reset(); Status = "approach"; LogStep(squad); }
        _elapsed += delta;
        PauseOtherMains(squad, delta);
        if (_elapsed >= ServerConfig.MultiStep.StepTimeout
            && Status != "looting"
            && (_actor == null || !CorpseEscort.InFlight(_actor)))
            return End(squad, waypoints, "step timeout");
        if (now < _nextWork) return true;
        if (!waypoints.TryOperationWork()) return true;
        _nextWork = now + .25f;
        if (Index > 0 && Definition.Power.DoorState == EDoorState.Shut)
        {
            if (++_powerRetries > 1) return End(squad, waypoints, "power expired twice");
            ResetStep(squad, waypoints, 0);
        }
        var step = Current;
        if (step.Object == null && step.Exit == null) return End(squad, waypoints, "target removed");
        if (step.Kind is OperationStepKind.Switch or OperationStepKind.Access or OperationStepKind.Wait && Satisfied(step))
        {
            // A close-Saferoom command is irreversible. Never replay an earlier card access after it.
            return Advance(squad, waypoints);
        }
        if (_actor == null || !squad.Members.Contains(_actor) || !_actor.IsActive || _actor.SoloExtractRequested)
        {
            ReleaseOrders(squad, waypoints);
            _actor = null;
            _route.Reset();
            foreach (var member in squad.Members)
                if (member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member)) { _actor = member; break; }
            if (_actor == null) return End(squad, waypoints, "no available operator");
        }
        if (!_orders.ContainsKey(_actor))
        {
            if (!waypoints.TryOperationPoint(_actor, step, _route, out _anchor, out var final))
            {
                if (_route.Pending) return true;
                if (TryNearbySwitch(squad, waypoints, "no complete path")) return true;
                Log.Info($"MULTISTEP ROUTE: {squad} operation={Definition.Id} step={step.Label} from={_actor.Position} target={step.Position} samples={_route.Samples} partial={_route.PartialPaths} invalid={_route.InvalidPaths} attempt={_retries + 1}");
                _actor = null;
                _nextWork = now + 3f;
                if (++_retries >= 3) return End(squad, waypoints, "unreachable step");
                return true;
            }
            _approachOnly = !final;
            if (_approachOnly)
                Log.Info($"MULTISTEP ROUTE: {squad} operation={Definition.Id} step={step.Label} approach={_anchor} target={step.Position}");
            waypoints.CollectCorpseEscortCover(_anchor, _covers);
            _occupied.Clear();
            SetOrder(_actor, Point(waypoints, _anchor, step.Label, _approachOnly ? 2f : 1f), waypoints);
            if (Main != null) { Main.Position = step.Position; Main.CellCoords = waypoints.WorldToCell(step.Position); }
            squad.Objective.Location = _orders[_actor];
            squad.Objective.Status = SquadObjectiveState.Active;
        }
        // Prepare at most one follower per work tick, avoiding a burst of cover path searches.
        foreach (var member in squad.Members)
        {
            if (member == _actor || !member.IsActive || member.SoloExtractRequested || _orders.ContainsKey(member)
                || CorpseEscort.InFlight(member)) continue;
            if (step.Kind is OperationStepKind.Regroup or OperationStepKind.Extract || _regroup != null)
                SetOrder(member, Point(waypoints, _regroup?.ExfilInteriorPosition ?? _regroup?.Position ?? _anchor, "Regroup"), waypoints);
            else if (waypoints.TryPickCorpseEscortPosition(member, _anchor, _anchor, _covers, _occupied, _rejected, squad.Members.IndexOf(member), out var cover, 3))
            {
                _occupied.Add(cover.Position);
                SetOrder(member, Point(waypoints, cover.Position, "Cover " + step.Label), waypoints);
                member.Guard.CoverPoint = cover;
            }
            else SetOrder(member, Point(waypoints, _anchor, "Follow " + step.Label), waypoints);
            break;
        }
        // BSG may stop just outside a one-metre anchor. Finish an approach leg before
        // counting that as a route failure, then recalculate the remaining local path.
        if (_approachOnly && (_actor.Position - _anchor).sqrMagnitude <= 4f)
        {
            ReleaseOrders(squad, waypoints);
            _actor = null;
            _retries = 0;
            _route.Reset();
            return true;
        }
        if (_actor.Objective.Status == ObjectiveStatus.Failed)
        {
            if (TryNearbySwitch(squad, waypoints, "arrival failed")) return true;
            ReleaseOrders(squad, waypoints);
            _actor = null;
            if (++_retries >= 3) return End(squad, waypoints, "operator route failed");
            return true;
        }
        if (_approachOnly) return true;
        if (step.Kind == OperationStepKind.Loot) return TickLoot(squad, waypoints);
        if (step.Kind == OperationStepKind.Extract) return TickExtract(squad, waypoints);
        if (step.Kind == OperationStepKind.Wait)
        {
            if (Status != "waiting") { Status = "waiting"; LogStep(squad); }
            return true;
        }
        if (step.Kind == OperationStepKind.Regroup)
        {
            _regroup = step.Exit;
            if (_regroupAt < 0 && ExfilArrival.IsInside(_actor, step.Exit)) _regroupAt = now;
            foreach (var member in squad.Members)
                if (member.IsActive && !member.SoloExtractRequested && !ExfilArrival.IsInside(member, step.Exit))
                    return (_regroupAt < 0 || now - _regroupAt < ServerConfig.MultiStep.RegroupTimeout)
                        || End(squad, waypoints, "regroup timeout");
            return Advance(squad, waypoints);
        }
        if ((_actor.Position - _anchor).sqrMagnitude > 1f) return true;
        return Interact(squad, waypoints);
    }

    private bool TryNearbySwitch(Squad squad, WaypointSystem waypoints, string reason)
    {
        if (Current.Kind != OperationStepKind.Switch || Current.Object is not Switch sw
            || !OperationSwitchReach.CanReach(_actor.Position, sw)) return false;
        // Use the native interaction, including linked power, gate and extraction callbacks.
        // Assigning DoorState directly would show an open lever without powering its circuit.
        Interact(squad, waypoints, reason);
        return true;
    }

    private bool Interact(Squad squad, WaypointSystem waypoints, string recovery = null)
    {
        var step = Current;
        var now = Time.time;
        if (_interactionPending && now - _interactionAt < 5f) return true;
        if (step.Object.DoorState == EDoorState.Interacting || step.Object.InteractingPlayer != null)
            return true;
        if (!step.Object.Operatable) return true;
        if (step.Object is Door door && !MultiStepAccess.HasPower(door)) return true;
        if (step.Object is Switch sw && sw.DoorState == EDoorState.Locked) return true;
        if (++_interactionRetries > 3) return End(squad, waypoints, "interaction failed");
        try
        {
            if (recovery != null)
                Log.Info($"MULTISTEP SWITCH: {squad} operation={Definition.Id} step={step.Label} nearby recovery reason={recovery} distance={Vector3.Distance(_actor.Position, step.Position):F2}m");
            _actor.Look.Target = step.Object.transform.position;
            if (step.Object is Door locked && locked.DoorState == EDoorState.Locked)
            {
                locked.GetInteractionParameters(_actor.Position); // Selects the real remote card reader.
                locked.Unlock();
                Orbit.Api.OrbitDoorEvents.Raise(locked, Orbit.Api.OrbitDoorEvents.Operation.Unlock);
            }
            else
            {
                step.Object.LockForInteraction();
                step.Object.Interact(new InteractionResult(EInteractionType.Open));
                if (step.Object is Door opened) Orbit.Api.OrbitDoorEvents.Raise(opened, Orbit.Api.OrbitDoorEvents.Operation.Open);
                else Orbit.Api.OrbitInteractionEvents.Raise(step.Object, EInteractionType.Open);
            }
            _interactionPending = true;
            _interactionAt = now;
            Status = "interacting";
            LogStep(squad);
        }
        catch (Exception error)
        {
            _interactionAt = now; _interactionPending = true;
            Log.Warning($"MULTISTEP: {squad} operation={Definition.Id} interaction={step.Label} failed: {error.Message}");
        }
        return true;
    }

    private bool TickLoot(Squad squad, WaypointSystem waypoints)
    {
        if (_loot != null)
        {
            if (_actor.Objective.Status is not (ObjectiveStatus.Finished or ObjectiveStatus.Failed)) return true;
            _attemptedLoot.Add(_loot.Id);
            waypoints.ReleaseClaim(_loot.Id, _actor.Id);
            _loot = null;
        }
        if ((_actor.Position - Current.Position).sqrMagnitude > 225f) return true;
        if (Status != "looting") { Status = "looting"; _elapsed = 0; LogStep(squad); }
        if (_elapsed >= ServerConfig.MultiStep.LootDuration) return Advance(squad, waypoints);
        _loot = waypoints.OperationLoot(_actor, Current.Position, _attemptedLoot, out var exhausted);
        if (_loot == null) return !exhausted || Advance(squad, waypoints);
        SetOrder(_actor, _loot, waypoints);
        return true;
    }

    private bool TickExtract(Squad squad, WaypointSystem waypoints)
    {
        var exit = (ExfiltrationPoint)Current.Exit.Target;
        if (exit.Status is EExfiltrationStatus.NotPresent or EExfiltrationStatus.Hidden)
            return End(squad, waypoints, "exit unavailable");
        if (exit.Status is not (EExfiltrationStatus.RegularMode or EExfiltrationStatus.Countdown)) return true;
        foreach (var member in squad.Members)
            if (member.IsActive && !member.SoloExtractRequested && member.Objective.Location != Current.Exit)
                SetOrder(member, Current.Exit, waypoints);
        Status = "extracting";
        return true;
    }

    internal bool IsExit(Waypoint point) => Active && Current.Kind == OperationStepKind.Extract && Current.Exit == point;

    private static bool Satisfied(OperationStep step)
    {
        if (step.Object == null || step.Object.DoorState != EDoorState.Open) return false;
        if (step.Object is Door door)
            return Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, door.GetAngle(EDoorState.Open))) < 5f;
        return true;
    }

    private bool Advance(Squad squad, WaypointSystem waypoints)
    {
        if (Index + 1 >= _steps.Count) return End(squad, waypoints, "completed");
        ResetStep(squad, waypoints, Index + 1);
        return true;
    }

    private void ResetStep(Squad squad, WaypointSystem waypoints, int index)
    {
        ReleaseOrders(squad, waypoints);
        Index = index;
        _actor = null; _loot = null; _elapsed = 0; _regroupAt = -1; _retries = 0; _interactionRetries = 0; _interactionPending = false;
        _route.Reset(); _approachOnly = false;
        _attemptedLoot.Clear();
        Status = "approach";
        if (Main != null) { Main.Position = Current.Position; Main.CellCoords = waypoints.WorldToCell(Current.Position); }
        LogStep(squad);
    }

    internal bool End(Squad squad, WaypointSystem waypoints, string reason)
    {
        ReleaseOrders(squad, waypoints);
        Active = false; Status = reason;
        if (Definition.Extraction && reason != "completed") squad.FailedOperationExits.Add(Definition.Id);
        if (Main != null) Main.Completed = true;
        LogStep(squad);
        return false;
    }

    private static Waypoint Point(WaypointSystem waypoints, Vector3 position, string name, float radius = 1f)
        => new(waypoints.NewRuntimeWaypointId(), WaypointCategory.Synthetic, name, position, radius, new(), new(), null);

    private void SetOrder(Agent agent, Waypoint target, WaypointSystem waypoints)
    {
        if (agent.Objective.Location != null && agent.Objective.Location != target)
            waypoints.ReleaseClaim(agent.Objective.Location.Id, agent.Id);
        _orders[agent] = target;
        agent.Objective.Location = target; agent.Objective.SplinterParent = null; agent.Objective.ArrivalPath = null;
        agent.Objective.Status = ObjectiveStatus.None; agent.Objective.DispatchTime = Time.time;
        agent.Guard.CoverPoint = null; agent.Look.Target = null;
    }

    private void ReleaseOrders(Squad squad, WaypointSystem waypoints)
    {
        foreach (var pair in _orders)
        {
            if (pair.Key.Objective.Location == pair.Value)
            {
                pair.Key.Objective.Location = null; pair.Key.Objective.SplinterParent = null;
                pair.Key.Objective.Status = ObjectiveStatus.None; pair.Key.Guard.CoverPoint = null;
                pair.Key.Look.Target = null;
            }
            waypoints.ReleaseClaim(pair.Value.Id, pair.Key.Id);
            if (squad.Objective.Location == pair.Value) squad.Objective.Location = null;
        }
        _orders.Clear();
    }

    private void PauseOtherMains(Squad squad, float delta)
    {
        if (squad.MainObjectives == null) return;
        foreach (var main in squad.MainObjectives)
        {
            if (main.Completed || main == Main) continue;
            if (main.KillsRoamStartedAt > 0) main.KillsRoamStartedAt += delta;
            main.KillsFloorLastTick = 0; main.LootValueLastEngagedAt = 0;
        }
    }

    private void LogStep(Squad squad)
    {
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        Log.Info($"MULTISTEP: {squad} operation={Definition.Id} step={Index + 1}/{Count} label={Current.Label} state={Status}");
    }
}
