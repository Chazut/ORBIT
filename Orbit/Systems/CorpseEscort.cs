using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Tasks.Actions;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>A temporary group detour. The mission anchor stays intact until the loot is finished.</summary>
internal sealed class CorpseEscort
{
    private sealed class Slot
    {
        public Waypoint Assigned;
        public Vector3 Anchor;
        public bool AtBody;
        public float NextUpdate;
        public readonly List<Vector3> Rejected = new();
    }

    private readonly Dictionary<Agent, Slot> _slots = new();
    private readonly List<Vector3> _occupied = new();
    private readonly List<CoverPoint> _covers = new();
    private Waypoint _mission;
    private Waypoint _resumeAfterCorpse;
    private float _startedAt;
    private Vector3 _coverAnchor;
    private bool _haveCovers;
    private float _nextScan;
    private int _deferredCorpseId = -1;
    private float _deferredUntil;

    internal Agent Looter { get; private set; }
    internal Waypoint Target { get; private set; }
    internal bool Active => Looter != null;
    internal bool Owns(Agent agent) => Active && Looter == agent;
    internal bool IsEscortWaypoint(Waypoint waypoint)
    {
        if (waypoint == null) return false;
        foreach (var slot in _slots.Values)
            if (slot.Assigned == waypoint) return true;
        return false;
    }

    internal static bool InFlight(Agent agent)
        => agent.Objective.Status == ObjectiveStatus.Looting || agent.LootHandler?.LootTaskRunning == true;

    private static bool Available(Agent agent)
        => agent != null && agent.IsActive && agent.Player?.HealthController?.IsAlive == true
           && !agent.SoloExtractRequested && agent.LootExtractSweep == null;

    internal bool Maintain(Squad squad, WaypointSystem waypoints, bool inCombat)
    {
        if (squad.Size < 2 || inCombat || squad.CombatCallerMemberIdx >= 0 || squad.ExtractRequested
            || squad.Objective.Location == null || squad.Objective.Location.Category == WaypointCategory.Exfil)
        {
            End(squad, waypoints, "interrupted");
            return false;
        }

        if (Active)
        {
            if (_mission != squad.Objective.Location || !squad.Members.Contains(Looter) || !Available(Looter))
            {
                End(squad, waypoints, "owner or mission changed");
                return false;
            }

            // A loot session can mark the body consumed before its transfer/animation has ended.
            // Keep that owner until the action has really released it, including adjacent loot sweeps.
            var current = Looter.Objective.Location;
            if (current != Target && current != null && InFlight(Looter))
                Target = current;
            if (!InFlight(Looter) && (current != Target
                || Looter.Objective.Status is ObjectiveStatus.Finished or ObjectiveStatus.Failed
                || squad.CompletedPoiIds.Contains(Target.Id) || Looter.ValueSkippedPoiIds.Contains(Target.Id)
                || Target.Target == null || waypoints.IsClaimedByOther(Target.Id, Looter.Id)))
            {
                if (Looter.Objective.Status == ObjectiveStatus.Failed)
                {
                    _deferredCorpseId = Target.Id;
                    _deferredUntil = Time.time + 20f;
                }
                End(squad, waypoints, "loot ended or unavailable");
                return false;
            }
            return true;
        }

        if (Time.time < _nextScan) return false;
        _nextScan = Time.time + 1f;
        Agent owner = null;
        Waypoint target = null;
        // Prefer a running session, then an already committed approach, then pending own kills.
        // One stable owner prevents squads splitting between distant corpses every strategy tick.
        for (var pass = 0; pass < 3 && owner == null; pass++)
        {
            foreach (var member in squad.Members)
            {
                if (!Available(member)) continue;
                var candidate = pass == 2 ? waypoints.TryGetNextOwnKillCorpseForAgent(squad, member)
                    : member.Objective.Location;
                if (candidate?.Category != WaypointCategory.Corpse || candidate.Target == null
                    || !GotoObjectiveAction.IsLootableForAgent(member, candidate)) continue;
                if (pass == 0 && !InFlight(member)) continue;
                if (pass != 0 && (InFlight(member) || squad.CompletedPoiIds.Contains(candidate.Id)
                    || member.ValueSkippedPoiIds.Contains(candidate.Id)
                    || waypoints.IsClaimedByOther(candidate.Id, member.Id)
                    || (candidate.Id == _deferredCorpseId && Time.time < _deferredUntil))) continue;
                if (pass == 1 && member.Objective.Status is ObjectiveStatus.Finished or ObjectiveStatus.Failed) continue;
                owner = member;
                target = candidate;
                break;
            }
        }
        if (owner == null) return false;

        Looter = owner;
        Target = target;
        _mission = squad.Objective.Location;
        _resumeAfterCorpse = _mission.Category == WaypointCategory.Corpse
            ? squad.PreInterruptObjectiveLocation : null;
        _startedAt = Time.time;
        if (owner.Objective.Location != target)
            Assign(owner, target, null, waypoints);
        Log.Info($"{squad} corpse escort started: looter={owner}, target={target}, resume={_mission}");
        return true;
    }

    // The loot action keeps its usual short adjacent sweep without replacing the saved mission.
    internal void ContinueSweep(Agent agent, Waypoint next)
    {
        if (!Owns(agent)) return;
        Target = next;
        _haveCovers = false;
    }

    internal bool UpdateMember(Squad squad, Agent agent, int memberIndex, WaypointSystem waypoints)
    {
        if (!Active) return false;
        if (agent == Looter || InFlight(agent) || !Available(agent)) return true;

        if (!_slots.TryGetValue(agent, out var slot))
            _slots[agent] = slot = new Slot();
        if (Time.time < slot.NextUpdate) return true;

        var atBody = Looter.Objective.Status == ObjectiveStatus.Looting
                     || (Looter.Position - Target.Position).sqrMagnitude <= 64f;
        var anchor = atBody ? Target.Position : Looter.Position;
        var failed = slot.Assigned != null && agent.Objective.Location == slot.Assigned
                     && agent.Objective.Status == ObjectiveStatus.Failed;
        var displaced = slot.Assigned != null && agent.Objective.Status == ObjectiveStatus.Finished
                        && (agent.Position - slot.Assigned.Position).sqrMagnitude > 36f;
        if (slot.Assigned != null && agent.Objective.Location == slot.Assigned && !failed && !displaced
            && slot.AtBody == atBody && (slot.Anchor - anchor).sqrMagnitude < 36f)
            return true;

        slot.NextUpdate = Time.time + 3f;
        if (failed && slot.Rejected.Count < 8) slot.Rejected.Add(slot.Assigned.Position);
        if ((slot.Anchor - anchor).sqrMagnitude > 225f) slot.Rejected.Clear();
        if (atBody && (!_haveCovers || (_coverAnchor - anchor).sqrMagnitude > 36f))
        {
            waypoints.CollectCorpseEscortCover(anchor, _covers);
            _coverAnchor = anchor;
            _haveCovers = true;
        }

        _occupied.Clear();
        foreach (var pair in _slots)
            if (pair.Key != agent && squad.Members.Contains(pair.Key) && pair.Value.Assigned != null)
                _occupied.Add(pair.Value.Assigned.Position);
        if (!waypoints.TryPickCorpseEscortPosition(agent, Looter.Position, anchor, atBody ? _covers : null,
                _occupied, slot.Rejected, memberIndex, out var cover))
        {
            // Do not send an escort to an unvalidated point or keep pathfinding every tick.
            slot.NextUpdate = Time.time + 8f;
            Log.Debug($"{agent} corpse escort waiting for a reachable spaced position near {Looter}");
            return true;
        }

        ForgetSlot(squad, agent, slot);
        slot.Assigned = waypoints.CreateCorpseEscortWaypoint(cover.Position);
        slot.Anchor = anchor;
        slot.AtBody = atBody;
        Assign(agent, slot.Assigned, cover, waypoints);
        Log.Debug($"{agent} corpse escort {(atBody ? "cover" : "follow")}: looter={Looter}, position={cover.Position}, cover={cover.Category}");
        return true;
    }

    private static void Assign(Agent agent, Waypoint target, CoverPoint? cover, WaypointSystem waypoints)
    {
        if (agent.Objective.Location != null) waypoints.ReleaseClaim(agent.Objective.Location.Id, agent.Id);
        agent.Objective.Location = target;
        agent.Objective.SplinterParent = null;
        agent.Objective.ArrivalPath = null;
        agent.Objective.Status = ObjectiveStatus.None;
        agent.Objective.DispatchTime = Time.time;
        agent.Guard.CoverPoint = cover;
        // Goto owns arrival; its activation makes Guard dispose any outstanding sweep job normally.
    }

    private static void ForgetSlot(Squad squad, Agent agent, Slot slot)
    {
        if (slot.Assigned == null) return;
        squad.CompletedPoiIds.Remove(slot.Assigned.Id);
        squad.RecentlyVisitedPoiCooldowns.Remove(slot.Assigned.Id);
        agent.ArrivalFailures.Forget(slot.Assigned.Id);
    }

    internal void End(Squad squad, WaypointSystem waypoints, string reason)
    {
        if (!Active) return;
        var resuming = _mission == squad.Objective.Location;
        if (_resumeAfterCorpse != null && squad.PreInterruptObjectiveLocation == _resumeAfterCorpse)
        {
            squad.PreInterruptObjectiveLocation = null;
            if (resuming)
            {
                squad.Objective.Location = _resumeAfterCorpse;
                squad.Objective.CoverPoints.Clear();
                squad.Objective.CoverPoints.AddRange(_resumeAfterCorpse.CoverPoints);
            }
        }
        if (resuming && (squad.CompletedPoiIds.Contains(squad.Objective.Location.Id)
            || (squad.Objective.Location == Target && Looter.ValueSkippedPoiIds.Contains(Target.Id))))
        {
            squad.Objective.Location = null;
            squad.Objective.Status = SquadObjectiveState.Active;
            resuming = false;
        }
        foreach (var pair in _slots)
        {
            if (squad.Members.Contains(pair.Key) && !InFlight(pair.Key)
                && pair.Key.Objective.Location == pair.Value.Assigned)
                Resume(squad, pair.Key, waypoints);
            ForgetSlot(squad, pair.Key, pair.Value);
        }
        if (squad.Members.Contains(Looter) && !InFlight(Looter) && !Looter.SoloExtractRequested
            && Looter.Objective.Location == Target)
            Resume(squad, Looter, waypoints);
        if (resuming)
        {
            // Members must walk back to the saved anchor before its normal guard timer can finish.
            squad.Objective.StartTime = Time.time;
            squad.Objective.Duration = Mathf.Max(120f, squad.Objective.Duration);
            squad.Objective.Status = SquadObjectiveState.Active;
            squad.Objective.DurationAdjusted = false;
        }
        Log.Info($"{squad} corpse escort ended: reason={reason}, duration={Time.time - _startedAt:F1}s, resume={squad.Objective.Location}");
        _slots.Clear();
        _covers.Clear();
        _haveCovers = false;
        Looter = null;
        Target = null;
        _mission = null;
        _resumeAfterCorpse = null;
    }

    private static void Resume(Squad squad, Agent agent, WaypointSystem waypoints)
    {
        var index = squad.Members.IndexOf(agent);
        CoverPoint? cover = squad.Objective.Location != null && squad.Objective.CoverPoints.Count > 0
            ? squad.Objective.CoverPoints[index % squad.Objective.CoverPoints.Count] : null;
        Assign(agent, squad.Objective.Location, cover, waypoints);
    }
}
