using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Tasks.Actions;
using UnityEngine;

namespace Orbit.Systems;

// Completion of the search and collection of the body are separate. Keep this
// state on the squad, not on the killer or on the current Rush movement orders.
internal sealed class BossRushLoot
{
    private readonly Dictionary<string, Waypoint> _kills = new();
    private readonly Queue<Waypoint> _pending = new();
    private readonly HashSet<int> _queued = new();
    private readonly HashSet<Agent> _attempted = new();
    private readonly Dictionary<Agent, Waypoint> _orders = new();
    private Agent _owner;
    private Waypoint _resume;
    private bool _holding;
    private float _lastTick, _elapsed;
    private const float CollectionTimeout = 120f;

    internal bool Pending => _pending.Count > 0;
    internal bool Owns(Agent agent) => _holding && !agent.SoloExtractRequested;
    internal void RememberKill(string role, Waypoint corpse)
    {
        if (!string.IsNullOrEmpty(role)) _kills[role] = corpse;
    }
    internal bool TryGetKill(string role, out Waypoint corpse) => _kills.TryGetValue(role, out corpse);

    internal void Enqueue(Squad squad, Waypoint corpse)
    {
        if (corpse == null || !_queued.Add(corpse.Id)) return;
        _pending.Enqueue(corpse);
        // A boss can die after being spotted, during the same fight that ended
        // its search. Only revoke the automatic all-mains departure, never urgency.
        if (squad.ExtractRequested && squad.ExtractRequestedReason == "all mains done")
        { squad.ExtractRequested = false; squad.ExtractRequestedReason = null; }
        Log.Info($"BOSS RUSH LOOT: {squad} body={corpse.Id} state=queued");
    }

    internal bool Tick(Squad squad, WaypointSystem waypoints, bool combat)
    {
        var delta = _lastTick > 0 ? Mathf.Clamp(Time.time - _lastTick, 0, 1) : 0;
        _lastTick = Time.time;
        if (!Pending) { _holding = false; return false; }
        var corpse = _pending.Peek();
        // A killer can already be searching the body when the strategy first
        // observes the kill. Adopt that transfer before checking consumed markers.
        if (!Present(squad, _owner))
            foreach (var member in squad.Members)
                if (Present(squad, member) && member.Objective.Location == corpse && CorpseEscort.InFlight(member))
                {
                    ReleaseOwner(squad, waypoints);
                    _owner = member; _orders[member] = corpse;
                    _resume = squad.Objective.Location == corpse ? null : squad.Objective.Location;
                    break;
                }
        // Completion markers and deadlines must not interrupt a native transfer,
        // including an adjacent loot sweep that is already in progress.
        if (Present(squad, _owner) && CorpseEscort.InFlight(_owner))
        { _holding = !combat; return true; }
        if (squad.ExtractRequested || !ServerConfig.Rush.Allows(squad.Leader?.BotCategory))
        { Cancel(squad, waypoints, "extraction or settings"); return false; }
        if (combat || Time.time < squad.GhostFightUntil)
        {
            Release(squad, waypoints);
            _holding = false;
            return false;
        }
        _holding = true;
        _elapsed += delta;
        if (corpse.Target == null || squad.CompletedPoiIds.Contains(corpse.Id))
            return Finish(squad, waypoints, "collected or unavailable");
        if (_elapsed >= CollectionTimeout)
            return Finish(squad, waypoints, "collection timeout");

        if (_owner != null)
        {
            if (!Present(squad, _owner) || _owner.SoloExtractRequested)
                ReleaseOwner(squad, waypoints);
            else if (_owner.ValueSkippedPoiIds.Contains(corpse.Id)
                || _owner.Objective.Location == corpse && _owner.Objective.Status is ObjectiveStatus.Finished or ObjectiveStatus.Failed)
            {
                _attempted.Add(_owner);
                ReleaseOwner(squad, waypoints);
            }
            else if (!_owner.IsActive || _owner.Objective.Location != corpse)
                ReleaseOwner(squad, waypoints);
        }

        if (_owner == null)
        {
            Agent candidate = null;
            var canWait = false;
            foreach (var member in squad.Members)
            {
                if (!Present(squad, member) || member.SoloExtractRequested || _attempted.Contains(member)
                    || member.ValueSkippedPoiIds.Contains(corpse.Id)
                    || !GotoObjectiveAction.IsLootableForAgent(member, corpse)) continue;
                canWait = true;
                if (!member.IsActive || CorpseEscort.InFlight(member) || member.LootExtractSweep != null
                    || waypoints.IsClaimedByOther(corpse.Id, member.Id)) continue;
                // Preserve a member already approaching this body. Otherwise give
                // the nearest eligible survivor one ordinary Goto/loot attempt.
                if (member.Objective.Location == corpse) { candidate = member; break; }
                if (candidate == null || (member.Position - corpse.Position).sqrMagnitude
                    < (candidate.Position - corpse.Position).sqrMagnitude) candidate = member;
            }
            if (candidate == null)
            {
                if (!canWait) return Finish(squad, waypoints, "no eligible survivor");
                HoldMembers(squad, waypoints);
                return true;
            }
            if (candidate.Objective.Location != corpse
                && !waypoints.TryPrepareMemberWaypoint(candidate, corpse, null, out var accessPending))
            {
                if (!accessPending)
                {
                    _attempted.Add(candidate);
                    Log.Info($"BOSS RUSH LOOT: {squad} body={corpse.Id} state=access rejected looter={candidate}");
                }
                HoldMembers(squad, waypoints, candidate);
                return true;
            }
            _owner = candidate;
            _resume = squad.Objective.Location == corpse ? null : squad.Objective.Location;
            squad.CorpseEscort.End(squad, waypoints, "boss corpse collection");
            squad.Objective.Location = corpse;
            squad.Objective.Status = SquadObjectiveState.Active;
            squad.Objective.CoverPoints.Clear();
            // Do not reset an existing loot approach or its claim.
            if (_owner.Objective.Location != corpse) Assign(_owner, corpse, waypoints);
            else _orders[_owner] = corpse;
            Log.Info($"BOSS RUSH LOOT: {squad} body={corpse.Id} state=assigned looter={_owner}");
        }
        // Existing squad waiting/cover policy handles followers. Temporary local
        // holds cover the escort scan cooldown and never replace a running transfer.
        HoldMembers(squad, waypoints);
        squad.CorpseEscort.Maintain(squad, waypoints, false);
        return true;
    }

    private void HoldMembers(Squad squad, WaypointSystem waypoints, Agent preparing = null)
    {
        foreach (var member in squad.Members)
        {
            if (member == _owner || member == preparing || !Present(squad, member) || !member.IsActive || member.SoloExtractRequested
                || CorpseEscort.InFlight(member) || squad.CorpseEscort.Active
                || _orders.TryGetValue(member, out var order) && order == member.Objective.Location) continue;
            Assign(member, waypoints.CreateCorpseEscortWaypoint(member.Position), waypoints);
        }
    }

    private static bool Present(Squad squad, Agent member) => member != null && squad.Members.Contains(member)
        && !member.Bot.IsDead && member.Player?.HealthController?.IsAlive == true;

    private void Assign(Agent member, Waypoint point, WaypointSystem waypoints)
    {
        if (member.Objective.Location != null) waypoints.ReleaseClaim(member.Objective.Location.Id, member.Id);
        _orders[member] = point;
        member.Objective.Location = point; member.Objective.SplinterParent = null;
        member.Objective.ArrivalPath = null; member.Objective.Status = ObjectiveStatus.None;
        member.Objective.DispatchTime = Time.time; member.Guard.CoverPoint = null; member.Look.Target = null;
    }

    private void ReleaseOwner(Squad squad, WaypointSystem waypoints)
    {
        // Clear the corpse anchor before ending the escort, so its normal resume
        // cannot redispatch a dead/full/failed looter back onto the same body.
        if (Pending && squad.Objective.Location == _pending.Peek()) squad.Objective.Location = _resume;
        if (squad.CorpseEscort.Looter == _owner) squad.CorpseEscort.End(squad, waypoints, "boss loot handoff");
        if (_owner != null && _orders.TryGetValue(_owner, out var point))
        {
            ClearOrder(_owner, point, waypoints);
            _orders.Remove(_owner);
        }
        _owner = null;
    }

    private static void ClearOrder(Agent member, Waypoint point, WaypointSystem waypoints)
    {
        if (!member.Bot.IsDead && member.Player?.HealthController?.IsAlive == true && CorpseEscort.InFlight(member)) return;
        if (member.Objective.Location == point)
        {
            member.Objective.Location = null; member.Objective.SplinterParent = null;
            member.Objective.Status = ObjectiveStatus.None; member.Guard.CoverPoint = null; member.Look.Target = null;
        }
        waypoints.ReleaseClaim(point.Id, member.Id);
    }

    private void Release(Squad squad, WaypointSystem waypoints)
    {
        ReleaseOwner(squad, waypoints);
        foreach (var pair in _orders) ClearOrder(pair.Key, pair.Value, waypoints);
        _orders.Clear();
    }

    private bool Finish(Squad squad, WaypointSystem waypoints, string reason)
    {
        var corpse = _pending.Peek();
        Release(squad, waypoints);
        _pending.Dequeue(); _attempted.Clear(); _elapsed = 0; _holding = false; _resume = null;
        Log.Info($"BOSS RUSH LOOT: {squad} body={corpse.Id} state={reason}");
        return Pending;
    }

    internal void Cancel(Squad squad, WaypointSystem waypoints, string reason)
    {
        while (Pending) Finish(squad, waypoints, reason);
    }
}
