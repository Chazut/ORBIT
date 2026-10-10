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
internal sealed class OperationPlan : IObjectiveWork
{
    private Squad _squad;
    private WaypointSystem _waypoints;
    void IObjectiveWork.ResumeObjectiveWork() => Tick(_squad, _waypoints, WaypointSystem.ObjectiveCombat(_squad));
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
    private readonly List<Vector3> _approachHistory = new();
    private bool _approachOnly;
    private float _approachRadiusSqr = 4f;
    private readonly OperationProgress _progress = new();
    private readonly RecordedRouteSearch _recorded = new();
    private bool _persistent, _searchingRecorded;
    private bool _saferoomAccessGranted;
    private float _stepActiveElapsed;
    private readonly HashSet<Door> _triedAccessDoors = new();
    private Door _accessDoor;
    private float _accessDoorAt;
    private float _nextReachCheck;
    private Agent _interactionLookActor;
    private Vector3 _interactionLookPoint;

    internal OperationPlan(OperationDefinition definition, bool alarm)
    {
        Definition = definition;
        // Scene objects remain shared; cached reach callbacks/diagnostics belong to this plan.
        foreach (var step in definition.Steps)
            if (!step.OptionalAlarm || alarm)
                _steps.Add(new OperationStep
                {
                    Label = step.Label, Kind = step.Kind, Object = step.Object, WaitAt = step.WaitAt,
                    Exit = step.Exit, OptionalAlarm = step.OptionalAlarm, Underground = step.Underground, Entry = step.Entry,
                    SwitchApproach = step.SwitchApproach, RetryNavigation = step.RetryNavigation, BestEffort = step.BestEffort
                });
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
        while (Index + 1 < Count && Current.Kind is OperationStepKind.Switch or OperationStepKind.Access && Satisfied(Current))
        {
            RememberSaferoomAccess();
            Index++;
        }
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
        _squad = squad; _waypoints = waypoints;
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
            waypoints.CancelOperationWork(this);
            if (_regroupAt >= 0) _regroupAt += delta;
            if (!_paused) { ReleaseOrders(squad, waypoints); _paused = true; Status = "paused"; LogStep(squad); }
            return false;
        }
        if (_paused)
        { _paused = false; _actor = null; _route.Reset(); _recorded.ResetSearch(); _searchingRecorded = false; Status = "approach"; LogStep(squad); }
        _elapsed += delta;
        _stepActiveElapsed += delta;
        if (Current.BestEffort && _stepActiveElapsed >= 12f)
        {
            Log.Info($"MULTISTEP OPTIONAL: {squad} operation={Definition.Id} step={Current.Label} skipped after local attempt budget");
            return Advance(squad, waypoints);
        }
        _persistent |= Current.RetryNavigation;
        PauseOtherMains(squad, delta);
        if (_elapsed >= ServerConfig.MultiStep.StepTimeout
            && Status != "looting"
            && (_actor == null || !CorpseEscort.InFlight(_actor)))
            {
                if (!_persistent && !EnableRescue(squad, "step timeout")) return End(squad, waypoints, "step timeout");
                RetryNavigation(squad, waypoints); return true;
            }
        if (now < _nextWork) return true;
        if (Index > 0 && Definition.Power.DoorState == EDoorState.Shut)
        {
            if (++_powerRetries > 1 && !_persistent) return End(squad, waypoints, "power expired twice");
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
            _recorded.ResetSearch(); _searchingRecorded = false;
            foreach (var member in squad.Members)
                if (member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member)) { _actor = member; break; }
            if (_actor == null) return End(squad, waypoints, "no available operator");
        }
        if (step.Kind is OperationStepKind.Switch or OperationStepKind.Access or OperationStepKind.Loot or OperationStepKind.Extract or OperationStepKind.Regroup)
        {
            // A short leg can pass the exposed lever before its destination. Recheck locally
            // under the shared work budget instead of walking past a usable interaction.
            if (step.Kind is OperationStepKind.Switch or OperationStepKind.Access && _orders.ContainsKey(_actor) && _accessDoor == null
                && now >= _nextReachCheck)
            {
                if (!waypoints.TryOperationWork(this)) return true;
                _nextReachCheck = now + .25f;
                if (TryNearbyInteraction(squad, waypoints, "passing within reach")) return true;
            }
            if (_progress.Observe(_actor.Position, step.Position, _elapsed)) _retries = 0;
            if (_orders.ContainsKey(_actor)) _progress.ObserveLeg(_actor.Position, _elapsed);
            if (!_interactionPending && _accessDoor == null && _progress.Stalled(_elapsed)
                && !_progress.AdvancingAlongLeg(_elapsed))
            {
                if (!EnableRescue(squad, "autonomous progress exhausted") && !_persistent) return End(squad, waypoints, "no advancing route");
                RetryNavigation(squad, waypoints); return true;
            }
        }
        if (!_orders.ContainsKey(_actor))
        {
            if (!waypoints.TryOperationWork(this)) return true;
            _nextWork = now + .25f;
            if (_accessDoor != null)
            {
                if (_accessDoor.DoorState == EDoorState.Open
                    && Mathf.Abs(Mathf.DeltaAngle(_accessDoor.CurrentAngle, _accessDoor.GetAngle(EDoorState.Open))) < 5f)
                {
                    _accessDoor = null; _route.Reset(); _approachHistory.Clear(); _progress.Reset(); _retries = 0;
                }
                else if (_elapsed - _accessDoorAt < 8f) return true;
                else { _accessDoor = null; _route.Reset(); _retries++; }
            }
            if (TryNearbyInteraction(squad, waypoints, "local reach")) return true;
            OperationRouteSearch.Remember(_approachHistory, _actor.Position, step.Kind == OperationStepKind.Switch ? 2f : 5f);
            var final = false;
            var found = !_searchingRecorded && waypoints.TryOperationPoint(_actor, step, _route, out _anchor, out final, _retries, _approachHistory);
            if (_searchingRecorded)
            {
                found = _recorded.Find(_actor.Position, out _anchor, step.Underground && OperationRoutePolicy.InD2Bunker(_actor.Position) ? OperationRoutePolicy.D2 : null);
                if (_recorded.Pending)
                { _nextWork = now; waypoints.ContinueOperationWork(this); return true; }
                if (!found) { RetryNavigation(squad, waypoints); return true; }
                _searchingRecorded = false;
                Log.Info($"MULTISTEP FALLBACK: {squad} operation={Definition.Id} step={step.Label} leg={_anchor}");
            }
            else if (_recorded.Active && !_route.Pending && (!found || !final))
            {
                _searchingRecorded = true; _recorded.Enable(Definition.MapKey, RescueKey(), _actor.Position); _recorded.ResetSearch();
                _nextWork = now; waypoints.ContinueOperationWork(this); return true;
            }
            if (!found)
            {
                if (_route.Pending)
                { _nextWork = now; waypoints.ContinueOperationWork(this); return true; }
                if (TryNearbyInteraction(squad, waypoints, "no complete path")) return true;
                if (TryAccessDoor(squad, waypoints)) return true;
                ReportRoute(squad, step, false);
                _actor = null;
                _route.Reset();
                _nextWork = now + 3f;
                if (++_retries >= 3)
                {
                    if (!EnableRescue(squad, "autonomous search exhausted") && !_persistent) return End(squad, waypoints, "unreachable step");
                    RetryNavigation(squad, waypoints);
                }
                return true;
            }
            _approachOnly = !final;
            var localSwitchLeg = _approachOnly && step.Kind == OperationStepKind.Switch
                && (_anchor - step.Position).sqrMagnitude <= 100f;
            _approachRadiusSqr = localSwitchLeg ? 1f : 4f;
            // Preserve ordinary closed-door recovery before leaving for another local leg.
            // Static/open scenery, such as Hermetic's entrance, is not an access door.
            if (_approachOnly && TryAccessDoor(squad, waypoints)) return true;
            ReportRoute(squad, step, true);
            _progress.BeginLeg(_recorded.Active && !final ? _recorded.RouteCorners : _route.RouteCorners, _elapsed);
            _progress.ObserveLeg(_actor.Position, _elapsed);
            waypoints.CollectCorpseEscortCover(_anchor, _covers);
            _occupied.Clear();
            SetOrder(_actor, Point(waypoints, _anchor, step.Label, _approachOnly && !localSwitchLeg ? 2f : 1f), waypoints);
            if (Main != null) { Main.Position = step.Position; Main.CellCoords = waypoints.WorldToCell(step.Position); }
            squad.Objective.Location = _orders[_actor];
            squad.Objective.Status = SquadObjectiveState.Active;
            return true; // Followers get their own work slice after the operator's route search.
        }
        // Prepare at most one follower per work tick, avoiding a burst of cover path searches.
        foreach (var member in squad.Members)
        {
            if (member == _actor || !member.IsActive || member.SoloExtractRequested || _orders.ContainsKey(member)
                || CorpseEscort.InFlight(member)) continue;
            if (!waypoints.TryOperationWork(this)) return true;
            if (step.Kind is OperationStepKind.Regroup or OperationStepKind.Extract || _regroup != null)
                SetOrder(member, Point(waypoints, _regroup?.ExfilInteriorPosition ?? _regroup?.Position ?? _anchor, "Regroup"), waypoints);
            else if (waypoints.TryPickCorpseEscortPosition(member, _anchor, _anchor, _covers, _occupied, _rejected, squad.Members.IndexOf(member), out var cover, 3)
                && (!step.Underground || !OperationRoutePolicy.InD2Bunker(_anchor) || OperationRoutePolicy.InD2Bunker(cover.Position)))
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
        if (_approachOnly && (_actor.Position - _anchor).sqrMagnitude <= _approachRadiusSqr)
        {
            OperationRouteSearch.Remember(_approachHistory, _actor.Position, step.Kind == OperationStepKind.Switch ? 2f : 5f);
            ReleaseOrders(squad, waypoints);
            _actor = null;
            _progress.ReachedLeg();
            _route.Reset();
            return true;
        }
        if (_actor.Objective.Status == ObjectiveStatus.Failed)
        {
            if (TryNearbyInteraction(squad, waypoints, "arrival failed")) return true;
            if (TryAccessDoor(squad, waypoints)) return true;
            OperationRouteSearch.Remember(_approachHistory, _anchor, step.Kind == OperationStepKind.Switch ? 2f : 5f);
            ReleaseOrders(squad, waypoints);
            _actor = null;
            _route.Reset();
            if (++_retries >= 3)
            {
                if (!EnableRescue(squad, "operator route failed") && !_persistent) return End(squad, waypoints, "operator route failed");
                RetryNavigation(squad, waypoints);
            }
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
                        || _persistent || End(squad, waypoints, "regroup timeout");
            return Advance(squad, waypoints);
        }
        if ((_actor.Position - _anchor).sqrMagnitude > 1f) return true;
        if (step.Kind is OperationStepKind.Switch or OperationStepKind.Access && !step.CanInteract(_actor.Position))
        {
            Log.Info($"MULTISTEP REACH: {squad} operation={Definition.Id} step={step.Label} {step.ReachDiagnostics}");
            // A valid NavMesh endpoint can still be on the other side of a wall.
            if (TryAccessDoor(squad, waypoints)) return true;
            ReleaseOrders(squad, waypoints); _route.Reset(); _actor = null;
            if (++_retries >= 3)
            {
                if (!EnableRescue(squad, "switch access blocked") && !_persistent) return End(squad, waypoints, "switch access blocked");
                RetryNavigation(squad, waypoints);
            }
            _nextWork = now + 3f;
            return true;
        }
        return Interact(squad, waypoints);
    }

    private string RescueKey()
    {
        var id = Current.Object?.Id;
        if (Definition.Id == "d2") return Index == 0 ? "d2-power" : Current.Kind == OperationStepKind.Extract ? "d2-exit" : "d2-gate";
        if (Definition.Id == "hermetic") return Index == 0 ? "hermetic-lever" : "hermetic-exit";
        if (Definition.Id == "zb013") return Index == 0 ? "warehouse-power" : Current.Kind == OperationStepKind.Extract ? "zb013-exit" : "zb013-door";
        if (id == MultiStepCatalog.Mall + "00058") return "mall-alarm";
        if (id == MultiStepCatalog.Mall + "00064") return "mall-urinal";
        if (id == MultiStepCatalog.Mall + "00051")
            return Current.Kind == OperationStepKind.Access && Current.Object.DoorState == EDoorState.Locked ? "mall-urinal" : "mall-saferoom";
        if (Definition.Id == "kiba") return "mall-kiba";
        if (Definition.Id == "saferoom-extract") return "mall-saferoom-inside";
        return null;
    }

    private bool EnableRescue(Squad squad, string reason)
    {
        var origin = _actor?.Position ?? squad.Leader.Position;
        if (!_recorded.Enable(Definition.MapKey, RescueKey(), origin)) return false;
        _persistent = true;
        Log.Info($"MULTISTEP FALLBACK: {squad} operation={Definition.Id} step={Current.Label} reason={reason}");
        return true;
    }

    private void RetryNavigation(Squad squad, WaypointSystem waypoints)
    {
        ReleaseOrders(squad, waypoints); _route.Reset(); _recorded.ResetSearch(); _progress.Reset();
        _approachHistory.Clear(); _triedAccessDoors.Clear(); _accessDoor = null; _retries = 0;
        _searchingRecorded = false; _elapsed = 0; _nextWork = Time.time + 3f;
    }

    private bool TryAccessDoor(Squad squad, WaypointSystem waypoints)
    {
        var door = waypoints.OperationAccessDoor(_actor, Current, _triedAccessDoors);
        if (door == null || _triedAccessDoors.Contains(door) || _triedAccessDoors.Count >= 2) return false;
        _triedAccessDoors.Add(door);
        if (waypoints.OpenRushDoor?.Invoke(_actor, door) != true) return false;
        Log.Info($"MULTISTEP ACCESS: {squad} operation={Definition.Id} step={Current.Label} opening nearby door={door.Id}");
        ReleaseOrders(squad, waypoints);
        _accessDoor = door; _accessDoorAt = _elapsed; _route.Reset();
        return true;
    }

    private bool TryNearbyInteraction(Squad squad, WaypointSystem waypoints, string reason)
    {
        if (Current.Kind is not (OperationStepKind.Switch or OperationStepKind.Access) || Current.Object == null
            || Current.Underground && !OperationRoutePolicy.InD2Bunker(_actor.Position)
            || !Current.CanInteract(_actor.Position)) return false;
        // Use the native interaction, including linked power, gate and extraction callbacks.
        // Assigning DoorState directly would show an open lever without powering its circuit.
        ReleaseOrders(squad, waypoints);
        _route.Reset();
        Interact(squad, waypoints, reason);
        return true;
    }

    private void ReportRoute(Squad squad, OperationStep step, bool found)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var detail = $"operation={Definition.Id} step={step.Label} from={_actor.Position} target={step.Position}";
        if (found) detail += $" approach={_anchor} final={!_approachOnly}";
        detail += $" samples={_route.Samples} partial={_route.PartialPaths} invalid={_route.InvalidPaths} attempt={_retries + 1} heightGap={step.Position.y - _actor.Position.y:F2}m {_route.Diagnostics} underground={step.Underground} {_progress.Diagnostics}";
        if (step.Kind is OperationStepKind.Switch or OperationStepKind.Access)
            detail += $" reach=[{step.ReachDiagnostics}] candidateReject=[{step.CandidateDiagnostics}]";
        Log.Info($"MULTISTEP ROUTE: {squad} {detail}");
        PerformanceJournal.Event("multistep-route", _actor.Player?.ProfileId, detail, squad.Id);
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
        if (++_interactionRetries > 3)
        {
            if (!_persistent) return End(squad, waypoints, "interaction failed");
            _interactionRetries = 0; _interactionPending = false; _nextWork = now + 3f; return true;
        }
        try
        {
            if (recovery != null)
                Log.Info($"MULTISTEP SWITCH: {squad} operation={Definition.Id} step={step.Label} nearby recovery reason={recovery} distance={Vector3.Distance(_actor.Position, step.InteractionPoint):F2}m");
            _interactionLookActor = _actor;
            _interactionLookPoint = step.InteractionPoint;
            _actor.Look.Target = _interactionLookPoint;
            _actor.Look.Type = LookType.Position;
            if (!_actor.IsDormant) LookSystem.LookToPoint(_actor, _interactionLookPoint, 360f);
            Log.Info($"MULTISTEP SURFACE: {squad} operation={Definition.Id} step={step.Label} target={step.Object.Id} {step.ReachDiagnostics}");
            if (step.Object is Door locked && locked.DoorState == EDoorState.Locked)
            {
                var interaction = locked.GetInteractionParameters(_actor.Position); // Selects the real remote card reader.
                if (locked is KeycardDoor)
                {
                    var gap = interaction.InteractionPosition - _actor.Position;
                    if (gap.sqrMagnitude > 4f || Mathf.Abs(gap.y) > 1.5f)
                    { RetryNavigation(squad, waypoints); return true; }
                    // KeycardDoor's successful scan opens only from an interacting/unlocked state.
                    // Invoke its native success coroutine without creating or consuming an inventory key.
                    locked.LockForInteraction();
                    Log.Info($"MULTISTEP CARD: {squad} operation={Definition.Id} reader={locked.Id} scan requested without inventory card");
                }
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
        if (!waypoints.TryOperationWork(this)) return true;
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
        if (!MultiStepAccess.ExitActive(exit, _actor)) return true;
        foreach (var member in squad.Members)
            if (member.IsActive && !member.SoloExtractRequested && member.Objective.Location != Current.Exit)
                SetOrder(member, Current.Exit, waypoints);
        Status = "extracting";
        return true;
    }

    internal bool IsExit(Waypoint point) => Active && Current.Kind == OperationStepKind.Extract && Current.Exit == point;

    internal bool CanExtractOpenSaferoom(Agent agent, ExfiltrationPoint exit)
    {
        if (!Active || Definition.Id != "saferoom-extract" || Current.Kind != OperationStepKind.Extract
            || Current.Exit?.Target != exit || _regroup != Current.Exit || agent?.Squad?.Operation != this
            || exit.Status is EExfiltrationStatus.Hidden or EExfiltrationStatus.NotPresent
            || Definition.Power == null || Definition.Power.DoorState != EDoorState.Open
            || !ExfilArrival.IsInside(agent, Current.Exit)) return false;
        var inside = Current.Exit.ExfilInteriorPosition ?? Current.Exit.Position;
        if (Mathf.Abs(agent.Position.y - inside.y) > 1f || (agent.Position - inside).sqrMagnitude > 16f) return false;
        if (!_saferoomAccessGranted) return false;
        // Only these bots may leave after completing card access and regrouping inside.
        // Keep the shared door, switch and human extraction state untouched.
        return true;
    }

    private static bool Satisfied(OperationStep step)
    {
        if (step.Object == null || step.Object.DoorState != EDoorState.Open) return false;
        if (step.Object is Door door)
            return Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, door.GetAngle(EDoorState.Open))) < 5f;
        return true;
    }

    private bool Advance(Squad squad, WaypointSystem waypoints)
    {
        RememberSaferoomAccess();
        if (Index + 1 >= _steps.Count) return End(squad, waypoints, "completed");
        ResetStep(squad, waypoints, Index + 1);
        return true;
    }

    private void RememberSaferoomAccess()
    {
        if (Current.Kind == OperationStepKind.Access && Current.Object?.Id == MultiStepCatalog.Mall + "00051" && Satisfied(Current))
            _saferoomAccessGranted = true;
    }

    private void ResetStep(Squad squad, WaypointSystem waypoints, int index)
    {
        ReleaseOrders(squad, waypoints);
        Index = index;
        _stepActiveElapsed = 0;
        _actor = null; _loot = null; _elapsed = 0; _regroupAt = -1; _retries = 0; _interactionRetries = 0; _interactionPending = false;
        _route.Reset(); _approachOnly = false; _approachHistory.Clear();
        _progress.Reset(); _accessDoor = null; _triedAccessDoors.Clear();
        _searchingRecorded = false; _recorded.Reset();
        _nextReachCheck = 0;
        _attemptedLoot.Clear();
        Status = "approach";
        if (Main != null) { Main.Position = Current.Position; Main.CellCoords = waypoints.WorldToCell(Current.Position); }
        LogStep(squad);
    }

    internal bool End(Squad squad, WaypointSystem waypoints, string reason)
    {
        if (Current.BestEffort && reason is "step timeout" or "no advancing route" or "unreachable step"
            or "operator route failed" or "switch access blocked" or "interaction failed" or "target removed")
        {
            Log.Info($"MULTISTEP OPTIONAL: {squad} operation={Definition.Id} step={Current.Label} skipped reason={reason}");
            return Advance(squad, waypoints);
        }
        waypoints.CancelOperationWork(this);
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
        if (_interactionLookActor != null && _interactionLookActor.Look.Target == _interactionLookPoint)
            _interactionLookActor.Look.Target = null;
        _interactionLookActor = null;
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
