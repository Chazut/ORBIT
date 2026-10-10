using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly Dictionary<Agent, MemberAccessSearch> _memberAccess = new();
    private readonly List<Agent> _expiredMemberAccess = new();

    // Keep the chosen pickup stable while its access check shares the dispatch path budget.
    // This includes sweeps handed back to the strategy after the preceding pickup finishes.
    private sealed class MemberAccessSearch : IFrameSearch
    {
        private readonly WaypointSystem _owner;
        internal readonly Agent Member;
        internal readonly Squad Squad;
        internal readonly Waypoint Pick, Parent, Anchor, Previous;
        internal readonly Vector3 Origin;
        private readonly int _mainState;
        private readonly bool _extract, _soloExtract;
        private readonly float _started;
        internal bool Done, Reachable;

        internal MemberAccessSearch(WaypointSystem owner, Agent member, Waypoint pick, Waypoint parent)
        {
            _owner = owner; Member = member; Squad = member.Squad;
            Pick = pick; Parent = parent; Anchor = Squad.Objective.Location;
            Previous = member.Objective.Location; Origin = member.Position;
            _mainState = MainSignature(Squad); _extract = Squad.ExtractRequested;
            _soloExtract = member.SoloExtractRequested; _started = Time.time;
        }

        internal bool Valid => Time.time - _started < 3f && Member.Bot != null && !Member.Bot.IsDead
            && ReferenceEquals(Member.Squad, Squad) && ReferenceEquals(Anchor, Squad.Objective.Location)
            && ReferenceEquals(Previous, Member.Objective.Location) && _mainState == MainSignature(Squad)
            && _extract == Squad.ExtractRequested && _soloExtract == Member.SoloExtractRequested
            && (Member.Position - Origin).sqrMagnitude < 25f;

        public bool Step()
        {
            if (Valid) Reachable = _owner.IsWaypointReachable(Pick, Squad, Origin);
            Done = true;
            return false;
        }
    }

    private void CancelMemberAccess(Agent member)
    {
        if (_memberAccess.Remove(member, out var search)) CancelFrameSearch(search);
    }

    private void PruneMemberAccess()
    {
        _expiredMemberAccess.Clear();
        foreach (var pair in _memberAccess)
            if (!pair.Value.Valid) _expiredMemberAccess.Add(pair.Key);
        foreach (var member in _expiredMemberAccess) CancelMemberAccess(member);
    }

    internal bool TryResumeMemberWaypoint(Agent member, out Waypoint pick, out Waypoint parent)
    {
        pick = parent = null;
        if (!_memberAccess.TryGetValue(member, out var search)) return false;
        if (!search.Valid) { CancelMemberAccess(member); return false; }
        pick = search.Pick; parent = search.Parent;
        return true;
    }

    internal bool TryPrepareMemberWaypoint(Agent member, Waypoint pick, Waypoint parent, out bool pending)
    {
        pending = false;
        var squad = member.Squad;
        if (_memberAccess.TryGetValue(member, out var search)
            && (!search.Valid || !ReferenceEquals(search.Pick, pick)))
        { CancelMemberAccess(member); search = null; }

        // Recheck live eligibility before consuming a result or opening any carver.
        if (squad == null || squad.CompletedPoiIds.Contains(pick.Id)
            || member.ValueSkippedPoiIds.Contains(pick.Id) || IsUnavailableLooseLoot(pick)
            || (_claims.TryGetValue(pick.Id, out var claimant) && claimant != member.Id))
        {
            CancelMemberAccess(member);
            // A finished/value-skipped anchor must not be offered again on every member tick.
            // Let the shared dispatcher choose a target another member can actually use.
            if (squad != null && (squad.CompletedPoiIds.Contains(pick.Id)
                || IsUnavailableLooseLoot(pick) || AllAliveMembersValueSkipped(squad, pick.Id)))
                RejectMemberAnchor(squad, pick);
            return false;
        }
        if (!CanSelectMemberDoorTarget(squad, pick))
        {
            CancelMemberAccess(member);
            RejectMemberAnchor(squad, pick);
            return false;
        }

        if (CategoryAllowsLockedDoorBypass(pick.Category))
        {
            if (search == null)
            {
                search = new MemberAccessSearch(this, member, pick, parent);
                _memberAccess.Add(member, search);
                ScheduleFrameSearch(search);
            }
            PumpFrameSearches();
            if (!search.Done) { pending = true; return false; }
            CancelMemberAccess(member);
            if (!search.Valid || !search.Reachable)
            {
                RejectMemberAnchor(squad, pick);
                return false;
            }
        }

        // The check above discovers doors even for splinters that never went through PickFromCell.
        if (TryPrepareMemberWaypoint(squad, pick)) return true;
        RejectMemberAnchor(squad, pick);
        return false;
    }

    private static void RejectMemberAnchor(Squad squad, Waypoint pick)
    {
        // Leave other members' ongoing loot alone, and do not mark the item consumed.
        // The ordinary dispatcher will apply the new door/unreachable filter on the next tick.
        if (squad != null && ReferenceEquals(squad.Objective.Location, pick))
        {
            squad.Objective.RepickRequested = true;
            Log.Debug($"{squad} member access rejected squad anchor {pick}; requesting a new waypoint");
        }
    }
}
