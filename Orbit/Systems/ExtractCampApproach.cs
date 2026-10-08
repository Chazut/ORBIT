using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

// The target is already rolled. Only execution asks the NavMesh for a route.
// Partial routes are approach legs; failure retires this main instead of rerolling nearby exits.
internal sealed class ExtractCampApproach(MainObjective main) : IObjectiveWork
{
    private readonly OperationRouteSearch _route = new();
    private readonly Dictionary<Agent, Waypoint> _orders = new();
    private readonly List<Vector3> _approachHistory = new();
    private Squad _squad;
    private WaypointSystem _waypoints;
    private Agent _actor;
    private Vector3 _anchor;
    private float _lastTick, _idle, _bestDistance = float.MaxValue, _retryAt;
    private int _failures;
    private bool _waitingForCover;

    void IObjectiveWork.ResumeObjectiveWork() => Tick(_squad, _waypoints);

    internal static bool Owns(Squad squad, Agent agent)
        => Category(squad, agent) != null;

    internal static string Category(Squad squad, Agent agent)
    {
        if (squad.MainObjectives == null) return null;
        foreach (var item in squad.MainObjectives)
            if (item.CampApproach is { } plan && plan._orders.TryGetValue(agent, out var point)
                && agent.Objective.Location == point) return plan._waitingForCover ? "SearchingAmbushExtract" : "ApproachingAmbushExtract";
        return null;
    }

    internal bool Tick(Squad squad, WaypointSystem waypoints)
    {
        _squad = squad; _waypoints = waypoints;
        var now = Time.time;
        var delta = _lastTick > 0 ? Mathf.Clamp(now - _lastTick, 0, 1) : 0;
        _lastTick = now;
        if (main.Completed || !main.CanPursue(squad.MainObjectives) || squad.Camp.Active || squad.Camp.PendingAirdrop != null)
        { Release(squad, waypoints); return false; }
        if (!ServerConfig.Ambush.Allows(squad.Leader?.BotCategory) || !ServerConfig.Ambush.Extracts.Enabled
            || !waypoints.IsAmbushSiteAvailable(main.CampSite, squad.Leader?.BotCategory))
            return Fail(squad, waypoints, "target unavailable");
        if (!AmbushDirector.Available(squad))
        { Release(squad, waypoints); return false; }
        _idle += delta;
        var distance = Vector3.Distance(squad.Leader.Position, main.Position);
        if (distance < _bestDistance - 5) { _bestDistance = distance; _idle = 0; }
        if (_idle >= ServerConfig.Ambush.TravelTimeout && distance > ServerConfig.Ambush.Extracts.DistanceMax + 10f)
            return Fail(squad, waypoints, "no approach progress or cover unavailable");
        if (_actor == null || !squad.Members.Contains(_actor) || !_actor.IsActive || _actor.SoloExtractRequested)
        {
            Release(squad, waypoints);
            foreach (var member in squad.Members)
                if (member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member)) { _actor = member; break; }
            if (_actor == null) return false;
        }
        // Formation is retried by AmbushDirector. Do not send a camper inside the exit trigger.
        var near = ServerConfig.Ambush.Extracts.DistanceMax + 10;
        if (distance <= near)
        {
            main.SetCampState("searching cover");
            if (!_waitingForCover)
            {
                Release(squad, waypoints);
                _actor = squad.Leader;
                foreach (var member in squad.Members) SetOrder(member, member.Position, now, waypoints);
                squad.Objective.Location = _orders[_actor];
                squad.Objective.Status = SquadObjectiveState.Active;
                _waitingForCover = true;
            }
            return true;
        }
        if (_waitingForCover) { Release(squad, waypoints); return true; }
        main.SetCampState("approach");
        if (_orders.ContainsKey(_actor))
        {
            if ((_actor.Position - _anchor).sqrMagnitude <= 9f)
            {
                OperationRouteSearch.Remember(_approachHistory, _actor.Position);
                Release(squad, waypoints); _failures = 0; return true;
            }
            if (_actor.Objective.Status == ObjectiveStatus.Failed)
            {
                OperationRouteSearch.Remember(_approachHistory, _anchor);
                Release(squad, waypoints); return Retry(squad, waypoints, "leg failed");
            }
            return true;
        }
        if (now < _retryAt || !waypoints.TryOperationWork(this)) return true;
        // Stop on the approach side, where local cover can be selected, rather than on the trigger.
        var target = Vector3.MoveTowards(main.Position, _actor.Position, ServerConfig.Ambush.Extracts.DistanceMax);
        // Retry navigation around obstacles, without enlarging the destination's cover ring.
        OperationRouteSearch.Remember(_approachHistory, _actor.Position);
        if (!_route.Find(_actor.Position, target, out _anchor, out var final, _failures, _approachHistory))
        {
            if (_route.Pending) { waypoints.ContinueOperationWork(this); return true; }
            _route.Reset();
            return Retry(squad, waypoints, "no advancing route");
        }
        foreach (var member in squad.Members) SetOrder(member, _anchor, now, waypoints);
        squad.Objective.Location = _orders[_actor]; squad.Objective.Status = SquadObjectiveState.Active;
        Log.Info($"EXTRACT CAMP ROUTE: {squad} target={main.Position} approach={_anchor} final={final}");
        return true;
    }

    private void SetOrder(Agent member, Vector3 position, float now, WaypointSystem waypoints)
    {
        if (!member.IsActive || member.SoloExtractRequested || CorpseEscort.InFlight(member)) return;
        var point = new Waypoint(waypoints.NewRuntimeWaypointId(), WaypointCategory.Synthetic,
            "Extract camp approach", position, 2f, new(), new(), null);
        if (member.Objective.Location != null) waypoints.ReleaseClaim(member.Objective.Location.Id, member.Id);
        _orders[member] = point;
        member.Objective.Location = point; member.Objective.SplinterParent = null;
        member.Objective.ArrivalPath = null; member.Objective.Status = ObjectiveStatus.None;
        member.Objective.DispatchTime = now; member.Guard.CoverPoint = null; member.Look.Target = null;
    }

    internal bool Retry(Squad squad, WaypointSystem waypoints, string reason)
    {
        if (++_failures >= 3) return Fail(squad, waypoints, reason);
        _retryAt = Time.time + 2;
        Log.Info($"EXTRACT CAMP ROUTE: {squad} retry={_failures}/3 reason={reason}");
        return true;
    }

    internal bool Fail(Squad squad, WaypointSystem waypoints, string reason)
    {
        Release(squad, waypoints);
        main.Completed = true;
        main.SetCampState("failed", reason);
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        Log.Info($"EXTRACT CAMP ROUTE: {squad} failed target={main.Position} reason={reason}");
        return false;
    }

    internal void Release(Squad squad, WaypointSystem waypoints)
    {
        waypoints.CancelOperationWork(this);
        foreach (var pair in _orders)
        {
            var member = pair.Key;
            if (member.Objective.Location == pair.Value)
            {
                member.Objective.Location = null; member.Objective.SplinterParent = null;
                member.Objective.Status = ObjectiveStatus.None; member.Guard.CoverPoint = null; member.Look.Target = null;
            }
            if (squad.Objective.Location == pair.Value) squad.Objective.Location = null;
            waypoints.ReleaseClaim(pair.Value.Id, member.Id);
        }
        _orders.Clear(); _actor = null; _waitingForCover = false; _route.Reset();
    }
}
