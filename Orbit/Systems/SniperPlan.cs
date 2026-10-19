using System.Collections.Generic;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

// One survey/path per shared objective work slot. Geometry is retained for this plan/raid.
// Local mesh-gap recovery uses the movement system's guarded placement for awake and sleeping bots.
internal sealed partial class SniperPlan : IObjectiveWork
{
    private Squad _squad;
    private WaypointSystem _waypoints;
    void IObjectiveWork.ResumeObjectiveWork() => Tick(_squad, _waypoints, _main, WaypointSystem.ObjectiveCombat(_squad));
    private readonly RushPoint _site;
    private readonly float _duration;
    private readonly Dictionary<Agent, Waypoint> _orders = new();
    private readonly Dictionary<Agent, int> _coverAttempts = new();
    private readonly NavMeshPath _path = new();
    private readonly List<Vector3> _watchDirections = new(5);
    private readonly Dictionary<string, int> _surveyRejections = new();
    private readonly OperationRouteSearch _route = new();
    private readonly List<Vector3> _approachHistory = new();
    private readonly List<Vector3> _rejectedPosts = new();
    private readonly OperationProgress _progress = new();
    private readonly RecordedSniperRoute _recorded = new();
    private string _recording;
    private bool _departing;
    private Agent _actor;
    private MainObjective _main;
    private Vector3 _post, _leg;
    private float _lastTick, _travel, _held, _nextWork, _nextEquipment, _pose = 1;
    private float _activeTravel, _rescueTravel;
    private int _candidate, _retries;
    private int _relocationMember;
    private float _nextRelocation;
    private bool _resolved, _partial, _arrived, _paused, _returningToPost;
    internal string Status { get; private set; } = "pending";
    internal string Step => _site.Name;

    // The scoped actor at an occupied post, not followers or squads still travelling there.
    internal Agent ContactObserver => _arrived && !_departing && !_returningToPost && !_shifting
        && _main is { Completed: false } && _actor is { IsActive: true, SoloExtractRequested: false }
        && _squad != null && !_squad.ExtractRequested && Near(_actor.Position, _post, 4f)
            ? _actor : null;

    internal SniperPlan(RushPoint site, RushStyle style)
    { _site = site; _duration = Random.Range(style.SniperHoldMin, style.SniperHoldMax); }

    internal bool Owns(Agent agent) => _main is { Completed: false } && !_paused
        && agent.IsActive && !agent.SoloExtractRequested && (!_orders.TryGetValue(agent, out var order) || agent.Objective.Location == order);

    internal bool Tick(Squad squad, WaypointSystem w, MainObjective main, bool combat)
    {
        _squad = squad; _waypoints = w;
        _recording ??= RecordedRouteLibrary.Sniper(w.RecordedMap, _site.Id);
        _main = main;
        if (main.Completed) return false;
        var now = Time.time;
        var dt = _lastTick > 0 ? Mathf.Clamp(now - _lastTick, 0, 1) : 0;
        _lastTick = now;
        if (!ServerConfig.Rush.Allows(squad.Leader?.BotCategory) || squad.ExtractRequested && !_recorded.Active && !_departing)
            return End(squad, w, "cancelled for extraction or settings");
        if (_actor != null && (!squad.Members.Contains(_actor) || _actor.Bot.IsDead || _actor.SoloExtractRequested))
        { ResetPostShift(); Release(squad, w); _actor = null; _arrived = false; _returningToPost = false; _route.Reset(); _recorded.Reset(); _progress.Reset(); _approachHistory.Clear(); _retries = 0; }
        if (_actor != null && now >= _nextEquipment)
        {
            _nextEquipment = now + 5;
            if (!SniperEquipment.Eligible(_actor, ServerConfig.Rush.SniperMinZoom))
                return End(squad, w, "scoped weapon unavailable");
        }
        if (_arrived)
        {
            // Count time at the post during safe long-range combat too; never extend forever.
            if (_actor != null && (Near(_actor.Position, _post, 4) || _shifting && WithinRoof(_actor.Position)
                || SniperCombat.IsAlertHeld(_actor.Bot))) _held += dt;
            main.CampElapsed = _held;
            if (_held >= _duration)
            {
                if (_recording == null) return End(squad, w, "completed");
                ResetPostShift(); Release(squad, w); _departing = true; _arrived = false;
                _post = RecordedRouteLibrary.Departure(w.RecordedMap, _recording, _actor.Position);
                _route.Reset(); _recorded.Reset(); _progress.Reset(); _approachHistory.Clear(); _travel = 0;
                _activeTravel = _rescueTravel = 0;
                _recorded.Suspend();
                main.Position = _post; main.CellCoords = w.WorldToCell(_post); SetStatus(squad, "leaving post");
            }
        }
        if (combat || now < squad.GhostFightUntil || _actor != null && !_actor.IsActive)
        {
            _recorded.Suspend();
            w.CancelOperationWork(this);
            if (!_paused)
            {
                InterruptPostShift("combat");
                Release(squad, w, keepCombatLease: _arrived);
                _paused = true;
                SetStatus(squad, "paused");
            }
            if (_arrived && _actor != null) SniperCombat.Refresh(_actor, _post, now + 2);
            return false;
        }
        if (!_arrived && !_departing && !_returningToPost && RushPlan.HasPendingOwnKill(squad, w))
        {
            w.CancelOperationWork(this);
            if (!_paused)
            {
                var anchor = squad.Objective.Location;
                Release(squad, w);
                squad.Objective.Location ??= anchor;
                _paused = true;
                _recorded.Suspend();
                SetStatus(squad, "looting own kill");
            }
            // Keep the chosen post, elapsed approach and rescue cursor for the return trip.
            return false;
        }
        if (_shiftYielded && ShiftUnsafe())
        { w.CancelOperationWork(this); return false; }
        _shiftYielded = false;
        if (_paused)
        {
            _paused = false; _route.Reset(); _recorded.ResetSearch();
            if (_arrived && _actor != null)
            {
                SniperCombat.CancelAlert(_actor.Bot, "alert-ended");
                _returningToPost = !Near(_actor.Position, _post, 3f);
                if (_returningToPost)
                {
                    _arrived = false; _progress.Reset(); _approachHistory.Clear();
                    // The completed ascent belongs to the previous arrival. A later
                    // return must be able to rejoin the stairs from its new position.
                    _recorded.Reset();
                }
                Log.Info($"SNIPER ALERT: {_actor} state={(_returningToPost ? "returning" : "resumed")} reason=alert-ended post={_post} held={_held:F1}s duration={_duration:F1}s");
            }
            SetStatus(squad, _returningToPost ? "returning to post" : "approach");
        }
        if (_returningToPost && _actor != null && CorpseEscort.InFlight(_actor))
        {
            // Finishing an inventory transfer must not be interrupted by the return order.
            w.CancelOperationWork(this); _recorded.Suspend();
            SetStatus(squad, "waiting for transfer");
            return false;
        }
        if (_returningToPost) SetStatus(squad, "returning to post");
        if (_arrived && TickPostShift(squad, w, now)) return true;
        if (_actor != null && !_arrived) _recorded.Observe(_actor.Position, dt);
        if (!_arrived || _actor == null || !Near(_actor.Position, _post, 4))
        {
            _travel += dt;
            _activeTravel += dt;
            if (_recorded.Active) _rescueTravel += dt;
        }
        // A retry, a new survey or a replacement actor cannot renew the objective indefinitely.
        // Combat and own-kill looting above pause these budgets; holding a valid post is separate.
        var approachBudget = Mathf.Clamp(ServerConfig.Rush.TravelTimeout, 30f, _departing || _returningToPost ? 180f : 600f);
        if (_activeTravel >= approachBudget + (_recording == null ? 0f : 120f) || _rescueTravel >= 120f)
            return End(squad, w, _departing ? "failed: return timeout" : "failed: approach timeout");
        if (_travel >= ServerConfig.Rush.TravelTimeout)
        {
            if (!EnableRescue(squad, w, "travel timeout")) return End(squad, w, "travel timeout");
            RetryRecorded(squad, w);
        }
        if (now < _nextWork) return true;
        using var timing = PerformanceJournal.Measure(TransitionPhase.SniperPlanning, "sniper-plan", this, squad.Id);
        if (_actor == null)
        {
            foreach (var member in squad.Members)
                if (member.IsActive && !member.Bot.IsDead && !member.SoloExtractRequested && !CorpseEscort.InFlight(member)
                    && SniperEquipment.Eligible(member, ServerConfig.Rush.SniperMinZoom)) { _actor = member; break; }
            if (_actor == null) return End(squad, w, "no scoped member");
            _nextEquipment = now + 5;
        }
        if (TryRecordedTransit(w, now)) return true;
        if (!_arrived && !_recorded.Active)
        {
            var target = _resolved ? _post : _recording == null
                ? new Vector3(_site.X, _site.Y, _site.Z) : RecordedRouteLibrary.Post(_recording, 0);
            _progress.Observe(_actor.Position, target, _travel);
            if (_orders.ContainsKey(_actor)) _progress.ObserveLeg(_actor.Position, _travel);
            if (_progress.SniperStalled(_travel) && !_progress.AdvancingAlongLeg(_travel)) return RetryPost(squad, w);
        }
        if (!_resolved)
        {
            var site = _recording == null ? new Vector3(_site.X, _site.Y, _site.Z) : RecordedRouteLibrary.Post(_recording, 0);
            var delta = _actor.Position - site; delta.y = 0;
            if (_orders.ContainsKey(_actor) || delta.sqrMagnitude > 60f * 60f)
                return ApproachSite(squad, w, site, now);
            if (!w.TryOperationWork(this)) return true;
            _nextWork = now + .15f;
            SetStatus(squad, "surveying post");
            if (!Survey(_actor.Position))
            {
                if (_candidate >= 25)
                {
                    var reasons = string.Join(",", _surveyRejections);
                    Log.Info($"SNIPER SURVEY: {squad} candidates={_candidate} rejected={reasons}");
                    if (_recording == null) return End(squad, w, "no usable post: " + reasons);
                    EnableRescue(squad, w, "post survey retry"); _candidate = 0; _nextWork = now + 5f;
                }
                return true;
            }
            _resolved = true;
            _progress.Reset();
            _route.Reset(); _approachHistory.Clear(); _retries = 0;
            main.Position = _post; main.CellCoords = w.WorldToCell(_post);
            main.ZoneFloorId = _site.Elevated ? null : _site.FloorId;
            SetStatus(squad, "approach");
            return true;
        }
        if (TryLocalRelocation(squad, w, now)) return true;
        if (!_orders.ContainsKey(_actor))
        {
            if (!w.TryOperationWork(this)) return true;
            _nextWork = now + .15f;
            OperationRouteSearch.Remember(_approachHistory, _actor.Position);
            var found = FindApproach(_post, out var final);
            if (!found)
            {
                if (RoutePending) { ContinueRoute(w); return true; }
                if (_recorded.Active) { RetryRecorded(squad, w); return true; }
                ReportRoute(squad, _post, "no complete route");
                _route.Reset(); _nextWork = now + 2;
                if (++_retries >= 3) return RetryPost(squad, w);
                return true;
            }
            _partial = !final;
            ReportRoute(squad, _post, final ? "final leg" : "staged leg");
            _progress.BeginLeg(_recorded.Active && !_recorded.Complete ? _recorded.RouteCorners : _route.RouteCorners, _travel);
            _progress.ObserveLeg(_actor.Position, _travel);
            Assign(_actor, _leg, w, "Sniper approach");
            squad.Objective.Location = _orders[_actor]; squad.Objective.Status = SquadObjectiveState.Active;
            return true;
        }
        // Reaching a partial endpoint is useful even if the mover reports a stopped path.
        if (_partial && ReachedLeg())
        {
            OperationRouteSearch.Remember(_approachHistory, _actor.Position);
            _recorded.RememberPartialLeg();
            _progress.ReachedLeg();
            Release(squad, w); _route.Reset(); _retries = 0; return true;
        }
        if (_actor.Objective.Status == ObjectiveStatus.Failed)
        {
            OperationRouteSearch.Remember(_approachHistory, _leg);
            _recorded.RememberPartialLeg();
            Release(squad, w); _route.Reset();
            if (++_retries >= 3) return RetryPost(squad, w);
            return true;
        }
        if (Near(_actor.Position, _leg, 1.8f) && Near(_actor.Position, _post, 3f)
            && _actor.Objective.Status == ObjectiveStatus.Finished)
        {
            if (_departing) return End(squad, w, "completed");
            if (!_arrived)
            {
                _arrived = true; main.CampStartedAt = now; main.CampTargetDuration = _duration;
                _travel = 0;
                _activeTravel = _rescueTravel = 0;
                if (!_roofSet) { _roofOrigin = _post; _roofSet = true; }
                SchedulePostShift(now);
                if (_returningToPost)
                {
                    _returningToPost = false;
                    Log.Info($"SNIPER ALERT: {_actor} state=resumed reason=post-reached post={_post} held={_held:F1}s duration={_duration:F1}s");
                }
            }
            _actor.Guard.CoverPoint = new CoverPoint(_post, Direction(_site.WatchYaw, _site.WatchPitch), CoverCategory.None, CoverLevel.Stay);
            SetStatus(squad, "holding");
            SniperCombat.Refresh(_actor, _post, now + 2);
        }
        // Followers occupy distinct connected positions. No reservation across squads.
        var index = 0;
        foreach (var member in squad.Members)
        {
            index++;
            if (member == _actor || !member.IsActive || member.SoloExtractRequested || CorpseEscort.InFlight(member)) continue;
            if (_orders.TryGetValue(member, out var previous))
            {
                if (member.Objective.Status != ObjectiveStatus.Failed) continue;
                w.ReleaseClaim(previous.Id, member.Id);
                _orders.Remove(member);
                member.Objective.Location = null; member.Objective.Status = ObjectiveStatus.None;
                member.Guard.CoverPoint = null; member.Look.Target = null;
            }
            if (!w.TryOperationWork(this)) return true;
            _coverAttempts.TryGetValue(member, out var attempts);
            // Squad spacing is optional; a missing follower position must not cancel the sniper post.
            _coverAttempts[member] = (attempts + 1) % 12;
            var offset = Direction(_site.WatchYaw + 120 + index * 65 + attempts * 90, 0) * (3 + index % 3);
            var target = _leg + offset;
            if (NavMesh.SamplePosition(target, out var hit, 1.5f, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - _leg.y) < 1.5f
                && NavMesh.CalculatePath(_leg, hit.position, NavMesh.AllAreas, _path) && _path.status == NavMeshPathStatus.PathComplete)
            {
                Assign(member, hit.position, w, "Follow sniper");
                member.Guard.CoverPoint = new CoverPoint(hit.position, offset.normalized, CoverCategory.None, CoverLevel.Stay);
            }
            break;
        }
        return true;
    }

    private bool TryLocalRelocation(Squad squad, WaypointSystem w, float now)
    {
        if (_departing || w.TrySniperRelocation == null || now < _nextRelocation || squad.Size == 0) return false;
        // Round-robin the actor and followers, at most one local check per shared work slot.
        for (var i = 0; i < squad.Size; i++)
        {
            var member = squad.Members[(_relocationMember + i) % squad.Size];
            if (!member.IsActive || member.SoloExtractRequested || CorpseEscort.InFlight(member)) continue;
            var target = _post;
            if (member == _actor) { if (_arrived) continue; }
            else if (!_orders.TryGetValue(member, out var order) || member.Objective.Location != order
                || member.Objective.Status == ObjectiveStatus.Finished) continue;
            else target = order.Position;
            if (!SniperReturnRecovery.Near(member.Position, target) || Near(member.Position, target, 1.5f)) continue;
            if (!w.TryOperationWork(this)) return true;
            _relocationMember = (_relocationMember + i + 1) % squad.Size;
            _nextRelocation = now + 1f;
            if (w.TrySniperRelocation(member, target, _main))
            {
                if (member == _actor)
                {
                    _partial = false; _leg = _post; _retries = 0; _route.Reset();
                    Assign(member, _post, w, "Sniper approach");
                    squad.Objective.Location = _orders[member]; squad.Objective.Status = SquadObjectiveState.Active;
                }
                member.Objective.Status = ObjectiveStatus.Finished;
            }
            return true;
        }
        return false;
    }

    private bool TryRecordedTransit(WaypointSystem w, float now)
    {
        if (_arrived || !_recorded.Active || w.TrySniperTransit == null || _actor == null) return false;
        if (!_recorded.HasTarget || _recorded.Complete || _recorded.StalledFor < 12f || now < _nextRelocation
            || !SniperReturnRecovery.Near(_actor.Position, _recorded.TransitTarget)
            || !w.TryOperationWork(this)) return false;
        _nextRelocation = now + 2f;
        ReportRoute(_squad, _recorded.TransitTarget, "recorded transit attempt");
        if (!w.TrySniperTransit(_actor, _recorded.TransitTarget))
        {
            // A physically blocked mover can still report a complete local path. Reissue its
            // next recorded leg without forgetting the stall or repeating a failed partial end.
            if (_recorded.StalledFor >= 20f && _orders.ContainsKey(_actor))
            {
                _recorded.RememberPartialLeg(); Release(_squad, w); _route.Reset(); _recorded.ResetSearch();
            }
            return true;
        }
        _recorded.Suspend();
        Release(_squad, w); _route.Reset(); _recorded.ResetSearch(); _progress.Reset();
        return true;
    }

    private bool ApproachSite(Squad squad, WaypointSystem w, Vector3 site, float now)
    {
        SetStatus(squad, "approaching site");
        if (_orders.ContainsKey(_actor))
        {
            if (ReachedLeg())
            {
                _progress.ReachedLeg();
                _recorded.RememberPartialLeg();
                OperationRouteSearch.Remember(_approachHistory, _actor.Position);
                Release(squad, w); _route.Reset(); _retries = 0;
            }
            else if (_actor.Objective.Status == ObjectiveStatus.Failed)
            {
                OperationRouteSearch.Remember(_approachHistory, _leg);
                _recorded.RememberPartialLeg();
                Release(squad, w); _route.Reset(); _nextWork = now + 2;
                if (++_retries >= 3) return _recording == null ? End(squad, w, "site approach failed") : RetryPost(squad, w);
            }
            return true;
        }
        if (!w.TryOperationWork(this)) return true;
        _nextWork = now + .15f;
        OperationRouteSearch.Remember(_approachHistory, _actor.Position);
        var found = FindApproach(site, out _);
        if (!found)
        {
            if (RoutePending) { ContinueRoute(w); return true; }
            if (_recorded.Active) { RetryRecorded(squad, w); return true; }
            ReportRoute(squad, site, "site route failed");
            _route.Reset(); _nextWork = now + 2;
            if (++_retries >= 3) return _recording == null ? End(squad, w, "no advancing site approach") : RetryPost(squad, w);
            return true;
        }
        _progress.BeginLeg(_recorded.Active && !_recorded.Complete ? _recorded.RouteCorners : _route.RouteCorners, _travel);
        _progress.ObserveLeg(_actor.Position, _travel);
        foreach (var member in squad.Members)
            if (member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member))
                Assign(member, _leg, w, "Sniper site approach");
        squad.Objective.Location = _orders[_actor]; squad.Objective.Status = SquadObjectiveState.Active;
        return true;
    }

    private bool RoutePending => _recorded.Active && !_recorded.Complete ? _recorded.Pending : _route.Pending;

    private bool ReachedLeg() => _recorded.Active && !_recorded.Complete
        ? RecordedSniperRoute.Reached(_actor.Position, _leg) : Near(_actor.Position, _leg, 2.5f);

    private bool FindApproach(Vector3 target, out bool final)
    {
        final = false;
        if (_recorded.Active && !_recorded.Complete) return _recorded.Find(_actor.Position, out _leg);
        return _route.Find(_actor.Position, target, out _leg, out final, _retries, _approachHistory,
            floorAware: true, finalHeightTolerance: 1.25f);
    }

    private void ContinueRoute(WaypointSystem w)
    { _nextWork = Time.time; w.ContinueOperationWork(this); }

    private void ReportRoute(Squad squad, Vector3 target, string result)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var detail = $"site={Step} result={result} from={_actor.Position} target={target} leg={_leg} heightGap={target.y - _actor.Position.y:F2}m expansion={_retries} samples={_route.Samples} partial={_route.PartialPaths} invalid={_route.InvalidPaths} {_recorded.Diagnostics}";
        Log.Info($"SNIPER ROUTE: {squad} {detail}");
        PerformanceJournal.Event("sniper-route", _actor.Bot.ProfileId, detail, squad.Id);
    }

    private bool RetryPost(Squad squad, WaypointSystem w)
    {
        if (EnableRescue(squad, w, "autonomous approach exhausted"))
        { RetryRecorded(squad, w); return true; }
        ReportRoute(squad, _post, "post rejected; survey continues");
        _rejectedPosts.Add(_post);
        _progress.Reset();
        Release(squad, w); _route.Reset(); _approachHistory.Clear();
        _resolved = _arrived = false; _retries = 0;
        if (_candidate >= 25) return End(squad, w, "no reachable post");
        _main.Position = new Vector3(_site.X, _site.Y, _site.Z);
        _main.CellCoords = w.WorldToCell(_main.Position);
        SetStatus(squad, "surveying another post");
        return true;
    }

    private bool EnableRescue(Squad squad, WaypointSystem w, string reason)
    {
        if (_recording == null || _actor == null) return false;
        var enabled = _recorded.Active;
        if (!_recorded.Enable(w.RecordedMap, _recording + (_departing ? ":down" : ":up"), _actor.Position)) return false;
        if (!enabled) Log.Info($"SNIPER FALLBACK: {squad} site={Step} route={_recorded.Key} reason={reason}");
        return true;
    }

    private void RetryRecorded(Squad squad, WaypointSystem w)
    {
        ReportRoute(squad, _recorded.Target, "recorded route retry");
        _recorded.RememberPartialLeg();
        Release(squad, w); _route.Reset(); _recorded.ResetSearch(); _progress.Reset();
        _retries = 0; _travel = 0; _nextWork = Time.time + 3f;
    }

    private bool RejectSurvey(string reason)
    { _surveyRejections.TryGetValue(reason, out var count); _surveyRejections[reason] = count + 1; return false; }

    private bool Survey(Vector3 origin)
    {
        var n = _candidate++;
        var angle = n * 137.5f;
        var radius = n == 0 ? 0 : _site.Radius * Mathf.Sqrt(n / 24f);
        var sample = _recording == null ? new Vector3(_site.X, _site.Y, _site.Z) + Direction(angle, 0) * radius
            : RecordedRouteLibrary.Post(_recording, n);
        if (_site.Elevated && _recording == null)
        {
            // Start above the authored seed; accept only a roof in the documented elevation band.
            if (!Physics.Raycast(sample + Vector3.up * 16, Vector3.down, out var roof, 13,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return RejectSurvey("roof missing");
            sample = roof.point;
        }
        if (!NavMesh.SamplePosition(sample, out var nav, 1.5f, NavMesh.AllAreas)
            || Mathf.Abs(nav.position.y - sample.y) > 1.25f
            || _recording == null && _site.Elevated && nav.position.y < _site.Y + 3) return RejectSurvey("post mesh or height");
        foreach (var rejected in _rejectedPosts)
            if ((nav.position - rejected).sqrMagnitude < 9f) return RejectSurvey("previously unreachable post");
        // A usable view from the actual eye height, not a ground-level ray through a parapet.
        _pose = .5f;
        var head = nav.position + Vector3.up * 1.25f;
        SurveyDirections(head);
        if (_watchDirections.Count == 0)
        {
            _pose = 1; head = nav.position + Vector3.up * 1.65f;
            SurveyDirections(head);
            if (_watchDirections.Count == 0) return RejectSurvey("sightline blocked");
        }
        // Survey certifies the local post. The staged route search owns connectivity and stairs.
        _post = nav.position; return true;
    }

    private void SurveyDirections(Vector3 head)
    {
        _watchDirections.Clear();
        var count = _site.WatchArc >= 359 ? 4 : 5;
        for (var i = 0; i < count; i++)
        {
            var direction = Direction(_site.WatchYaw - _site.WatchArc / 2 + i * _site.WatchArc / 4, _site.WatchPitch);
            if (OpenView(head, direction)) _watchDirections.Add(direction);
        }
    }

    private bool OpenView(Vector3 head, Vector3 direction)
        => !Physics.Raycast(head, direction, out var hit, _site.WatchDistance,
            LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)
            || hit.distance >= Mathf.Min(_site.WatchDistance, ServerConfig.Rush.SniperDisengageDistance);

    internal bool Watch(Agent agent)
    {
        if (!Owns(agent)) return false;
        if (agent.IsDormant) return true;
        MovementSystem.ResetGait(agent, pose: agent == _actor ? _pose : 1);
        if (agent.Guard.WatchTimeout <= Time.time)
        {
            var direction = agent == _actor && _watchDirections.Count > 0
                ? _watchDirections[Random.Range(0, _watchDirections.Count)] : Direction(_site.WatchYaw + 180, 0);
            LookSystem.LookToDirection(agent, direction, 60);
            agent.Guard.WatchTimeout = Time.time + Random.Range(3f, 7f);
        }
        return true;
    }

    internal bool End(Squad squad, WaypointSystem w, string reason, MainObjective main = null)
    { w.CancelOperationWork(this); _main ??= main; ResetPostShift(); Release(squad, w); _main.Completed = true; SetStatus(squad, reason); return false; }
    private void Release(Squad squad, WaypointSystem w, bool keepCombatLease = false)
    {
        if (!keepCombatLease && _actor != null) SniperCombat.Remove(_actor.Bot);
        foreach (var pair in _orders)
        {
            var member = pair.Key;
            if (member.Objective.Location == pair.Value)
            {
                member.Objective.Location = null; member.Objective.SplinterParent = null; member.Objective.Status = ObjectiveStatus.None;
                member.Guard.CoverPoint = null; member.Look.Target = null;
            }
            if (squad.Objective.Location == pair.Value) squad.Objective.Location = null;
            w.ReleaseClaim(pair.Value.Id, member.Id);
        }
        _orders.Clear();
        _coverAttempts.Clear();
    }
    private void Assign(Agent member, Vector3 position, WaypointSystem w, string name)
    {
        if (member.Objective.Location != null) w.ReleaseClaim(member.Objective.Location.Id, member.Id);
        var radiusSqr = member == _actor && _recorded.Active && !_recorded.Complete ? .5f : 1.5f;
        var point = new Waypoint(w.NewRuntimeWaypointId(), WaypointCategory.Synthetic, name, position, radiusSqr, new(), new(), null);
        _orders[member] = point;
        member.Objective.Location = point; member.Objective.SplinterParent = null; member.Objective.ArrivalPath = null;
        member.Objective.Status = ObjectiveStatus.None; member.Objective.DispatchTime = Time.time;
        member.Guard.CoverPoint = null; member.Look.Target = null;
    }
    private void SetStatus(Squad squad, string state)
    {
        if (Status == state) return;
        Status = state; Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        Log.Info($"RUSH: {squad} kind=Sniper step=1/1 site={Step} state={state}");
    }
    internal Orbit.Api.OrbitRushPoint[] Snapshot() => new[] { new Orbit.Api.OrbitRushPoint {
        Id = _site.Id, Name = _site.Name, X = _resolved ? _post.x : _site.X, Y = _resolved ? _post.y : _site.Y,
        Z = _resolved ? _post.z : _site.Z, Radius = _site.Radius, State = Status, Sequence = 1 } };
    internal static bool Near(Vector3 a, Vector3 b, float radius) => Mathf.Abs(a.y - b.y) < 1.5f && (a - b).sqrMagnitude <= radius * radius;
    internal static Vector3 Direction(float yaw, float pitch)
    {
        var h = yaw * Mathf.Deg2Rad; var v = pitch * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(h) * Mathf.Cos(v), Mathf.Sin(v), Mathf.Cos(h) * Mathf.Cos(v));
    }
}
