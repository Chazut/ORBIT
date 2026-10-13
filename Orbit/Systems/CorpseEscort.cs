using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Sain;
using Orbit.Settings;
using Orbit.Tasks.Actions;
using UnityEngine;
using Random = UnityEngine.Random;

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
        public int Layout;
        public int Failures;
        public int ArrivalFailures;
        public bool WaitingFallback;
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
    private Vector3 _holdAnchor;
    private Waypoint _returnWaypoint;
    private float _returnDeadline;
    internal SquadLootWaitMode Mode { get; private set; }
    internal bool Returning => _returnWaypoint != null;

    internal Agent Looter { get; private set; }
    internal Waypoint Target { get; private set; }
    internal bool Active => Looter != null;
    internal bool Owns(Agent agent) => Active && Looter == agent;
    internal bool IsWaitingInCover(Agent agent)
    {
        if (!Active || agent == Looter || !Available(agent) || InFlight(agent)
            || agent.Objective.Status != ObjectiveStatus.Finished
            || !_slots.TryGetValue(agent, out var slot) || slot.Assigned != agent.Objective.Location
            || agent.Guard.CoverPoint is not { } cover || cover.Category == CoverCategory.None) return false;
        var delta = agent.Position - cover.Position;
        return delta.sqrMagnitude <= 4f && Mathf.Abs(delta.y) <= 1f;
    }
    internal bool IsEscortWaypoint(Waypoint waypoint)
    {
        if (waypoint == null) return false;
        if (waypoint == _returnWaypoint) return true;
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

            if (Returning)
            {
                if (Looter.Objective.Location != _returnWaypoint || Looter.Objective.Status == ObjectiveStatus.Failed
                    || Time.time >= _returnDeadline || !HasWaitingMember(squad))
                {
                    End(squad, waypoints, "regroup interrupted or blocked");
                    return false;
                }
                var delta = Looter.Position - _returnWaypoint.Position;
                if (delta.sqrMagnitude <= 25f && Mathf.Abs(delta.y) <= 2f)
                {
                    End(squad, waypoints, "regrouped after loot");
                    return false;
                }
                return true;
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
                // Only release the loot owner after all native transfers/animations have finished.
                if (current == Target && TryReturnToGroup(squad, waypoints)) return true;
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
        for (var pass = 0; pass < 4 && owner == null; pass++)
        {
            foreach (var member in squad.Members)
            {
                if (!Available(member)) continue;
                var candidate = pass == 2 ? waypoints.TryGetNextOwnKillCorpseForAgent(squad, member)
                    : member.Objective.Location;
                var waitingForOtherLoot = pass == 3 && InFlight(member)
                    && candidate?.Category is WaypointCategory.ContainerLoot or WaypointCategory.LooseLoot
                    && HasSettledSquadmate(squad, member);
                if (pass == 3 && !waitingForOtherLoot) continue;
                if ((!waitingForOtherLoot && candidate?.Category != WaypointCategory.Corpse) || candidate.Target == null
                    || !GotoObjectiveAction.IsLootableForAgent(member, candidate)) continue;
                if (pass == 0 && !InFlight(member)) continue;
                if (pass is 1 or 2 && (InFlight(member) || squad.CompletedPoiIds.Contains(candidate.Id)
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
        var archetype = squad.Personality?.Archetype ?? PersonalityArchetype.Average;
        var style = PersonalityProfile.LootWaitStyleFor(archetype) ?? SquadLootWaitStyle.For(archetype.ToString());
        Mode = style.Choose(Random.value, Random.value);
        if (Mode == SquadLootWaitMode.HoldCover && !TrySetHoldAnchor(squad, waypoints))
            Mode = SquadLootWaitMode.EscortCover;
        if (owner.Objective.Location != target)
            Assign(owner, target, null, waypoints);
        Log.Info($"{squad} corpse escort started: looter={owner}, target={target}, resume={_mission}, mode={Mode}, personality={archetype}");
        return true;
    }

    // The loot action keeps its usual short adjacent sweep without replacing the saved mission.
    internal void ContinueSweep(Agent agent, Waypoint next)
    {
        if (!Owns(agent) || Returning) return;
        Target = next;
        _haveCovers = false;
    }

    internal bool UpdateMember(Squad squad, Agent agent, int memberIndex, WaypointSystem waypoints)
    {
        if (!Active) return false;
        if (agent == Looter || InFlight(agent) || !Available(agent)) return true;

        if (!_slots.TryGetValue(agent, out var slot))
            _slots[agent] = slot = new Slot { Layout = memberIndex + Random.Range(0, 16) };
        if (Time.time < slot.NextUpdate) return true;

        var holding = Mode == SquadLootWaitMode.HoldCover;
        var atBody = holding || Looter.Objective.Status == ObjectiveStatus.Looting
                     || (Looter.Position - Target.Position).sqrMagnitude <= 64f;
        var anchor = holding ? _holdAnchor : atBody ? Target.Position : Looter.Position;
        var failed = slot.Assigned != null && agent.Objective.Location == slot.Assigned
                     && agent.Objective.Status == ObjectiveStatus.Failed;
        var displaced = slot.Assigned != null && agent.Objective.Status == ObjectiveStatus.Finished
                        && (agent.Position - slot.Assigned.Position).sqrMagnitude > 36f;
        if (slot.Assigned != null && agent.Objective.Location == slot.Assigned && !failed && !displaced && slot.Failures == 0
            && slot.AtBody == atBody && (slot.Anchor - anchor).sqrMagnitude < 36f)
            return true;

        if (slot.WaitingFallback && (slot.Anchor - anchor).sqrMagnitude < 225f) return true;
        slot.WaitingFallback = false;
        slot.NextUpdate = Time.time + Random.Range(2.5f, 4f);
        if ((slot.Anchor - anchor).sqrMagnitude > 225f)
        {
            slot.Rejected.Clear();
            slot.Failures = slot.ArrivalFailures = 0;
        }
        if (failed)
        {
            if (slot.Rejected.Count < 8) slot.Rejected.Add(slot.Assigned.Position);
            if (++slot.ArrivalFailures >= 3)
            {
                WaitLocally(squad, agent, slot, anchor, atBody, waypoints);
                return true;
            }
        }
        var useCover = Mode != SquadLootWaitMode.Follow;
        if (useCover && (!_haveCovers || (_coverAnchor - anchor).sqrMagnitude > 36f))
        {
            waypoints.CollectCorpseEscortCover(anchor, _covers);
            _coverAnchor = anchor;
            _haveCovers = true;
        }

        _occupied.Clear();
        foreach (var pair in _slots)
            if (pair.Key != agent && squad.Members.Contains(pair.Key) && pair.Value.Assigned != null)
                _occupied.Add(pair.Value.Assigned.Position);
        if (!waypoints.TryPickCorpseEscortPosition(agent, holding ? _holdAnchor : Looter.Position,
                anchor, useCover ? _covers : null,
                _occupied, slot.Rejected, slot.Layout, out var cover, maxPaths: 6))
        {
            // Do not send an escort to an unvalidated point or keep pathfinding every tick.
            slot.NextUpdate = Time.time + 8f;
            slot.Anchor = anchor;
            // Stop the previous mission's move order even during the delay before our one retry.
            slot.Failures++;
            WaitLocally(squad, agent, slot, anchor, atBody, waypoints, final: slot.Failures >= 2);
            Log.Debug($"{agent} corpse escort waiting for a reachable spaced position near {Looter}");
            return true;
        }

        ForgetSlot(squad, agent, slot);
        slot.Assigned = waypoints.CreateCorpseEscortWaypoint(cover.Position);
        slot.Anchor = anchor;
        slot.AtBody = atBody;
        slot.Failures = 0;
        Assign(agent, slot.Assigned, cover, waypoints);
        Log.Debug($"{agent} corpse escort {(useCover ? "cover" : "follow")}: looter={Looter}, position={cover.Position}, cover={cover.Category}, mode={Mode}");
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

    private static void WaitLocally(Squad squad, Agent agent, Slot slot, Vector3 anchor, bool atBody,
        WaypointSystem waypoints, bool final = true)
    {
        ForgetSlot(squad, agent, slot);
        slot.Assigned = waypoints.CreateCorpseEscortWaypoint(agent.Position);
        slot.Anchor = anchor;
        slot.AtBody = atBody;
        slot.WaitingFallback = final;
        Assign(agent, slot.Assigned, new CoverPoint(agent.Position, Vector3.zero,
            CoverCategory.None, CoverLevel.Stay), waypoints);
        Log.Debug($"{agent} corpse escort local wait: cover route unavailable, anchor={anchor}, retry={!final}");
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
            && (Looter.Objective.Location == Target || Looter.Objective.Location == _returnWaypoint))
            Resume(squad, Looter, waypoints);
        if (_returnWaypoint != null)
        {
            squad.CompletedPoiIds.Remove(_returnWaypoint.Id);
            squad.RecentlyVisitedPoiCooldowns.Remove(_returnWaypoint.Id);
            Looter.ArrivalFailures.Forget(_returnWaypoint.Id);
        }
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
        _returnWaypoint = null;
    }

    private static bool HasSettledSquadmate(Squad squad, Agent looter)
    {
        foreach (var member in squad.Members)
            if (member != looter && Available(member) && !InFlight(member)
                && member.Objective.Status == ObjectiveStatus.Finished
                && (member.Position - looter.Position).sqrMagnitude <= 1600f) return true;
        return false;
    }

    private bool HasWaitingMember(Squad squad)
    {
        foreach (var member in squad.Members)
            if (member != Looter && Available(member) && !InFlight(member)
                && _slots.TryGetValue(member, out var slot) && member.Objective.Location == slot.Assigned)
                return true;
        return false;
    }

    private bool TrySetHoldAnchor(Squad squad, WaypointSystem waypoints)
    {
        // Use a member's real position, never an average that could lie inside a wall or on another floor.
        Agent waiting = null;
        foreach (var member in squad.Members)
            if (member != Looter && Available(member) && !InFlight(member)) { waiting = member; break; }
        if (waiting == null || (waiting.Position - Target.Position).sqrMagnitude > 3600f) return false;
        foreach (var member in squad.Members)
            if (member != Looter && Available(member) && !InFlight(member)
                && (member.Position - waiting.Position).sqrMagnitude > 625f) return false;
        return waypoints.TryGetLootRegroupPosition(Target.Position, waiting.Position, out _holdAnchor);
    }

    private bool TryReturnToGroup(Squad squad, WaypointSystem waypoints)
    {
        if (Mode != SquadLootWaitMode.HoldCover || !HasWaitingMember(squad)) return false;
        if ((Looter.Position - _holdAnchor).sqrMagnitude <= 25f) return false;
        if (!waypoints.TryGetLootRegroupPosition(Looter.Position, _holdAnchor, out var position)) return false;
        _returnWaypoint = waypoints.CreateCorpseEscortWaypoint(position);
        _returnDeadline = Time.time + 60f;
        Assign(Looter, _returnWaypoint, null, waypoints);
        Log.Debug($"{Looter} corpse escort regroup: position={position}, mode={Mode}");
        return true;
    }

    private static void Resume(Squad squad, Agent agent, WaypointSystem waypoints)
    {
        var index = squad.Members.IndexOf(agent);
        CoverPoint? cover = squad.Objective.Location != null && squad.Objective.CoverPoints.Count > 0
            ? squad.Objective.CoverPoints[index % squad.Objective.CoverPoints.Count] : null;
        Assign(agent, squad.Objective.Location, cover, waypoints);
    }
}
