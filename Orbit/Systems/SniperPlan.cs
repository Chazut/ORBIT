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
// No global search for enemies, no role changes, and no body operations on sleepers.
internal sealed class SniperPlan : IObjectiveWork
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
    private readonly OperationRouteSearch _route = new();
    private Agent _actor;
    private MainObjective _main;
    private Vector3 _post, _leg;
    private float _lastTick, _travel, _held, _nextWork, _nextEquipment, _pose = 1;
    private int _candidate, _retries;
    private bool _resolved, _partial, _arrived, _paused;
    internal string Status { get; private set; } = "pending";
    internal string Step => _site.Name;

    internal SniperPlan(RushPoint site, RushStyle style)
    { _site = site; _duration = Random.Range(style.SniperHoldMin, style.SniperHoldMax); }

    internal bool Owns(Agent agent) => _main is { Completed: false } && !_paused
        && agent.IsActive && !agent.SoloExtractRequested && (!_orders.TryGetValue(agent, out var order) || agent.Objective.Location == order);

    internal bool Tick(Squad squad, WaypointSystem w, MainObjective main, bool combat)
    {
        _squad = squad; _waypoints = w;
        _main = main;
        if (main.Completed) return false;
        var now = Time.time;
        var dt = _lastTick > 0 ? Mathf.Clamp(now - _lastTick, 0, 1) : 0;
        _lastTick = now;
        if (!ServerConfig.Rush.Allows(squad.Leader?.BotCategory) || squad.ExtractRequested)
            return End(squad, w, "cancelled for extraction or settings");
        if (_actor != null && (!squad.Members.Contains(_actor) || _actor.Bot.IsDead || _actor.SoloExtractRequested))
        { Release(squad, w); _actor = null; _arrived = false; }
        if (_actor != null && now >= _nextEquipment)
        {
            _nextEquipment = now + 5;
            if (!SniperEquipment.Eligible(_actor, ServerConfig.Rush.SniperMinZoom))
                return End(squad, w, "scoped weapon unavailable");
        }
        if (_arrived)
        {
            // Count time at the post during safe long-range combat too; never extend forever.
            if (_actor != null && Near(_actor.Position, _post, 4)) _held += dt;
            main.CampElapsed = _held;
            if (_held >= _duration) return End(squad, w, "completed");
        }
        if (combat || now < squad.GhostFightUntil || _actor != null && !_actor.IsActive)
        {
            w.CancelOperationWork(this);
            if (!_paused)
            {
                Release(squad, w, keepCombatLease: _arrived);
                _paused = true;
                SetStatus(squad, "paused");
            }
            if (_arrived && _actor != null) SniperCombat.Refresh(_actor, _post, now + 2);
            return false;
        }
        if (_paused)
        { _paused = false; _route.Reset(); SetStatus(squad, "approach"); }
        if (!_arrived || _actor == null || !Near(_actor.Position, _post, 4)) _travel += dt;
        if (_travel >= ServerConfig.Rush.TravelTimeout) return End(squad, w, "travel timeout");
        if (now < _nextWork) return true;
        using var timing = PerformanceJournal.Measure(TransitionPhase.SniperPlanning, "sniper-plan", this, squad.Id);
        if (_actor == null)
        {
            foreach (var member in squad.Members)
                if (member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member)
                    && SniperEquipment.Eligible(member, ServerConfig.Rush.SniperMinZoom)) { _actor = member; break; }
            if (_actor == null) return End(squad, w, "no scoped member");
            _nextEquipment = now + 5;
        }
        if (!_resolved)
        {
            if (!w.TryOperationWork(this)) return true;
            _nextWork = now + .15f;
            SetStatus(squad, "surveying post");
            if (!Survey(_actor.Position))
            {
                if (_candidate >= 25) return End(squad, w, "no reachable sightline");
                return true;
            }
            _resolved = true;
            main.Position = _post; main.CellCoords = w.WorldToCell(_post);
            main.ZoneFloorId = _site.Elevated ? null : _site.FloorId;
            SetStatus(squad, "approach");
            return true;
        }
        if (!_orders.ContainsKey(_actor))
        {
            if (!w.TryOperationWork(this)) return true;
            _nextWork = now + .15f;
            if (!_route.Find(_actor.Position, _post, out _leg, out var final))
            {
                if (_route.Pending) return true;
                _route.Reset(); _nextWork = now + 2;
                if (++_retries >= 3) return End(squad, w, "unreachable post");
                return true;
            }
            _partial = !final;
            Assign(_actor, _leg, w, "Sniper approach");
            squad.Objective.Location = _orders[_actor]; squad.Objective.Status = SquadObjectiveState.Active;
            return true;
        }
        if (_actor.Objective.Status == ObjectiveStatus.Failed)
        {
            Release(squad, w); _route.Reset();
            if (++_retries >= 3) return End(squad, w, "movement failed");
            return true;
        }
        if (Near(_actor.Position, _leg, 1.8f) && _actor.Objective.Status == ObjectiveStatus.Finished)
        {
            if (_partial) { Release(squad, w); _route.Reset(); return true; }
            if (!_arrived)
            {
                _arrived = true; main.CampStartedAt = now; main.CampTargetDuration = _duration;
                _travel = 0;
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
            if (attempts >= 3) return End(squad, w, "squad cover unreachable");
            _coverAttempts[member] = attempts + 1;
            var offset = Direction(_site.WatchYaw + 120 + index * 65 + attempts * 90, 0) * (3 + index % 3);
            var target = _leg + offset;
            if (NavMesh.SamplePosition(target, out var hit, 1.5f, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - _leg.y) < 1.5f
                && NavMesh.CalculatePath(_leg, hit.position, NavMesh.AllAreas, _path) && _path.status == NavMeshPathStatus.PathComplete)
            {
                Assign(member, hit.position, w, "Cover sniper");
                member.Guard.CoverPoint = new CoverPoint(hit.position, offset.normalized, CoverCategory.None, CoverLevel.Stay);
            }
            break;
        }
        return true;
    }

    private bool Survey(Vector3 origin)
    {
        var n = _candidate++;
        var angle = n * 137.5f;
        var radius = n == 0 ? 0 : _site.Radius * Mathf.Sqrt(n / 24f);
        var sample = new Vector3(_site.X, _site.Y, _site.Z) + Direction(angle, 0) * radius;
        if (_site.Elevated)
        {
            // Start above the authored seed; accept only a roof in the documented elevation band.
            if (!Physics.Raycast(sample + Vector3.up * 16, Vector3.down, out var roof, 13,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return false;
            sample = roof.point;
        }
        if (!NavMesh.SamplePosition(sample, out var nav, 1.5f, NavMesh.AllAreas)
            || Mathf.Abs(nav.position.y - sample.y) > 1.25f
            || _site.Elevated && nav.position.y < _site.Y + 3) return false;
        // A usable view from the actual eye height, not a ground-level ray through a parapet.
        _pose = .5f;
        var head = nav.position + Vector3.up * 1.25f;
        SurveyDirections(head);
        if (_watchDirections.Count == 0)
        {
            _pose = 1; head = nav.position + Vector3.up * 1.65f;
            SurveyDirections(head);
            if (_watchDirections.Count == 0) return false;
        }
        if (!NavMesh.CalculatePath(origin, nav.position, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete) return false;
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
    { w.CancelOperationWork(this); _main ??= main; Release(squad, w); _main.Completed = true; SetStatus(squad, reason); return false; }
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
        var point = new Waypoint(w.NewRuntimeWaypointId(), WaypointCategory.Synthetic, name, position, 1.5f, new(), new(), null);
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
