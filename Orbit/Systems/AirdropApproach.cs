using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

// Keep the landing site across release/landing. Every leg must itself have a complete path.
internal sealed class AirdropApproach(Squad squad, AmbushSite site, WaypointSystem waypoints) : IObjectiveWork
{
    private sealed class Member
    {
        internal readonly OperationRouteSearch Route = new();
        internal readonly OperationProgress Progress = new();
        internal readonly List<Vector3> History = new();
        internal Waypoint Order;
        internal int Failures;
        internal float RetryAt, LastProgress;
        internal bool Travelling;
    }

    private readonly Dictionary<Agent, Member> _members = new();
    private Vector3 _target = site.Position;
    private int _cursor;
    private bool _released, _toCrate;
    internal bool Ready { get; private set; }
    internal string Failure { get; private set; }
    internal bool ToCrate => _toCrate;
    internal bool Owns(Agent agent) => _members.TryGetValue(agent, out var member)
        && agent.Objective.Location == member.Order;

    void IObjectiveWork.ResumeObjectiveWork() => Tick();

    internal void ContinueToCrate()
    {
        if (_toCrate) return;
        _toCrate = true;
        Ready = false;
        foreach (var pair in _members)
        {
            pair.Value.Route.Reset();
            pair.Value.History.Clear();
            pair.Value.LastProgress = Time.time;
        }
        Log.Info($"AIRDROP ROUTE: {squad} stage=crate target={site.Position} landed={site.Landed}");
    }

    internal bool Tick()
    {
        if (_released || Failure != null) return false;
        if (squad.Camp.PendingAirdrop != site || !AmbushDirector.Available(squad)
            || !waypoints.IsAmbushSiteAvailable(site, squad.Leader?.BotCategory))
            return Fail("interrupted or site unavailable");
        if (_members.Count != 0 && (_members.Count != squad.Size
            || !AllMembersPresent())) return Fail("group or assignment changed");

        if (_members.Count == 0)
            foreach (var agent in squad.Members)
            {
                var member = new Member { LastProgress = Time.time };
                _members.Add(agent, member);
                Assign(agent, member, agent.Position, false);
            }
        // Landing may correct the estimated ground position. Never reset a valid trip just
        // because the crate is now on the ground, or restart its no-progress allowance.
        if ((site.Position - _target).sqrMagnitude > 1f)
        {
            _target = site.Position;
            foreach (var pair in _members)
            {
                pair.Value.Route.Reset();
                pair.Value.History.Clear();
                Assign(pair.Key, pair.Value, pair.Key.Position, false);
            }
            Log.Info($"AIRDROP ROUTE: {squad} stage=retarget target={_target} landed={site.Landed}");
        }

        Ready = true;
        var near = _toCrate ? 3f : ServerConfig.Ambush.Airdrops.DistanceMax + 10f;
        foreach (var pair in _members)
        {
            var agent = pair.Key;
            var member = pair.Value;
            if (!Owns(agent)) return Fail("assignment changed");
            var offset = agent.Position - site.Position;
            if (offset.x * offset.x + offset.z * offset.z <= near * near && Mathf.Abs(offset.y) <= 2.5f)
            {
                if (member.Travelling) Assign(agent, member, agent.Position, false);
                continue;
            }
            Ready = false;
            if (member.Travelling)
            {
                member.Progress.ObserveLeg(agent.Position, Time.time);
                if (member.Progress.Observe(agent.Position, site.Position, Time.time)
                    || member.Progress.AdvancingAlongLeg(Time.time)) member.LastProgress = Time.time;
                var delta = agent.Position - member.Order.Position;
                if (delta.sqrMagnitude <= 4f && Mathf.Abs(delta.y) <= 1.25f)
                {
                    OperationRouteSearch.Remember(member.History, agent.Position);
                    member.Progress.ReachedLeg();
                    Assign(agent, member, agent.Position, false);
                    member.Route.Reset();
                    member.Failures = 0;
                }
                else if (agent.Objective.Status is ObjectiveStatus.Failed or ObjectiveStatus.Finished)
                {
                    OperationRouteSearch.Remember(member.History, member.Order.Position);
                    if (!Retry(agent, member, "leg failed or stopped outside arrival")) return false;
                }
            }
            if (Time.time - member.LastProgress >= Mathf.Min(30f, ServerConfig.Ambush.TravelTimeout)
                || member.Progress.Stalled(Time.time)) return Fail($"no progress member={agent} idle={Time.time - member.LastProgress:F1}s {member.Progress.Diagnostics}");
        }
        if (Ready) { waypoints.CancelOperationWork(this); return true; }

        // One route slice, at most three NavMesh queries, through the shared FIFO budget.
        for (var i = 0; i < squad.Size; i++)
        {
            var agent = squad.Members[_cursor++ % squad.Size];
            var member = _members[agent];
            var offset = agent.Position - site.Position;
            if (member.Travelling || Time.time < member.RetryAt
                || offset.x * offset.x + offset.z * offset.z <= near * near && Mathf.Abs(offset.y) <= 2.5f) continue;
            if (!waypoints.TryOperationWork(this)) return true;
            if (!member.Route.Find(agent.Position, site.Position, out var anchor, out var final,
                    member.Failures, member.History, floorAware: true))
            {
                if (member.Route.Pending) waypoints.ContinueOperationWork(this);
                else if (!Retry(agent, member, "no advancing route")) return false;
                return true;
            }
            var corners = member.Route.RouteCorners;
            Assign(agent, member, anchor, true);
            member.Progress.BeginLeg(corners, Time.time);
            Log.Info($"AIRDROP ROUTE: {squad} member={agent} stage=leg target={site.Position} approach={anchor} final={final} landed={site.Landed}");
            return true;
        }
        return true;
    }

    private bool AllMembersPresent()
    {
        foreach (var agent in _members.Keys) if (!squad.Members.Contains(agent) || !Owns(agent)) return false;
        return true;
    }

    private bool Retry(Agent agent, Member member, string reason)
    {
        Log.Info($"AIRDROP ROUTE: {squad} member={agent} stage=retry attempt={member.Failures + 1}/3 reason={reason} {member.Route.Diagnostics}");
        if (++member.Failures >= 3) return Fail(reason);
        Assign(agent, member, agent.Position, false);
        member.Route.Reset();
        member.RetryAt = Time.time + 2f;
        return true;
    }

    private bool Fail(string reason)
    {
        Failure = reason;
        Log.Info($"AIRDROP ROUTE: {squad} stage=failed reason={reason} target={site.Position} landed={site.Landed}");
        Release();
        return false;
    }

    private void Assign(Agent agent, Member member, Vector3 position, bool travelling)
    {
        Forget(agent, member);
        member.Order = new Waypoint(waypoints.NewRuntimeWaypointId(), WaypointCategory.Synthetic,
            "Airdrop approach", position, 1.5f, new(), new(), null);
        agent.Objective.Location = member.Order;
        agent.Objective.SplinterParent = null;
        agent.Objective.ArrivalPath = null;
        agent.Objective.Status = ObjectiveStatus.None;
        agent.Objective.DispatchTime = Time.time;
        agent.Guard.CoverPoint = null;
        agent.Look.Target = null;
        member.Travelling = travelling;
    }

    private void Forget(Agent agent, Member member)
    {
        if (agent.Objective.Location != null) waypoints.ReleaseClaim(agent.Objective.Location.Id, agent.Id);
        if (member.Order == null) return;
        squad.CompletedPoiIds.Remove(member.Order.Id);
        squad.RecentlyVisitedPoiCooldowns.Remove(member.Order.Id);
        agent.ArrivalFailures.Forget(member.Order.Id);
    }

    internal void Release()
    {
        waypoints.CancelOperationWork(this);
        foreach (var pair in _members)
        {
            var agent = pair.Key;
            if (!Owns(agent)) continue;
            Forget(agent, pair.Value);
            agent.Objective.Location = null;
            agent.Objective.SplinterParent = null;
            agent.Objective.Status = ObjectiveStatus.None;
            agent.Guard.CoverPoint = null;
            agent.Look.Target = null;
        }
        _released = true;
    }
}
