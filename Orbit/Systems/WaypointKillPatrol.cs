using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly ConditionalWeakTable<Agent, KillPatrolState> _killPatrols = new();

    private sealed class KillPatrolState
    {
        internal KillPatrolSearch Search;
        internal float RetryAt;
        internal readonly List<(Vector3 Point, float Until)> Recent = new();
    }

    internal bool IsLocalKillRoam(Agent agent, MainObjective main)
    {
        var squad = agent?.Squad;
        var radius = ServerConfig.MainObjectives.RoamSplinterRadius;
        return squad != null && agent.IsActive && agent.Bot != null && !agent.Bot.IsDead
            && main != null && main.Type == MainObjectiveType.Kills && !main.KillAmbush
            && main.KillsRoamStartedAt > 0 && main.CanPursue(squad.MainObjectives)
            && !agent.SoloExtractRequested && !GhostNoiseInvestigation.Committed(squad)
            && squad.CombatCallerMemberIdx < 0 && !ObjectiveCombat(squad) && Time.time >= squad.GhostFightUntil
            && agent.LootHandler?.LootTaskRunning != true
            && WorldToCell(agent.Position) == main.CellCoords
            && XzDistanceSqr(agent.Position, main.Position) <= radius * radius
            && MatchesZoneFloorAtTarget(main.ZoneFloorId, main.Position, agent.Position);
    }

    internal Waypoint FindKillPatrolForMember(Agent agent, MainObjective main, out bool pending)
    {
        pending = false;
        var state = _killPatrols.GetValue(agent, _ => new KillPatrolState());
        var search = state.Search;
        if (search != null && (!search.Valid || search.Main != main))
        {
            CancelFrameSearch(search);
            state.Search = search = null;
        }
        if (!IsLocalKillRoam(agent, main)) return null;
        if (search == null)
        {
            if (Time.time < state.RetryAt) return null;
            state.Recent.RemoveAll(entry => Time.time >= entry.Until);
            state.Search = search = new KillPatrolSearch(this, agent, main, state.Recent);
            ScheduleFrameSearch(search);
        }
        PumpFrameSearches();
        if (!search.Done) { pending = true; return null; }
        CancelFrameSearch(search);
        state.Search = null;
        state.RetryAt = Time.time + 3f;
        if (!search.Valid) return null;
        var point = search.Result;
        if (point != null)
        {
            state.Recent.Add((search.Origin, Time.time + 20f));
            state.Recent.Add((point.Position, Time.time + 20f));
        }
        Log.Info($"KILL PATROL: {agent} result={(point == null ? "no route" : "assigned")} probes={search.Probes} target={(point == null ? "none" : point.Position.ToString())}");
        return point;
    }

    private sealed class KillPatrolSearch : IFrameSearch
    {
        private readonly WaypointSystem _owner;
        private readonly Agent _agent;
        private readonly Squad _squad;
        private readonly Waypoint _previous, _anchor;
        private readonly List<(Vector3 Point, float Until)> _recent;
        private readonly NavMeshPath _path = new();
        private readonly float _started;
        internal readonly Vector3 Origin;
        internal readonly MainObjective Main;
        internal int Probes;
        internal bool Done;
        internal Waypoint Result;

        internal KillPatrolSearch(WaypointSystem owner, Agent agent, MainObjective main,
            List<(Vector3 Point, float Until)> recent)
        {
            _owner = owner; _agent = agent; _squad = agent.Squad; Main = main;
            _previous = agent.Objective.Location; _anchor = _squad.Objective.Location;
            _recent = recent; Origin = agent.Position; _started = Time.time;
        }

        internal bool Valid => Time.time - _started < 5f && _owner.IsLocalKillRoam(_agent, Main)
            && ReferenceEquals(_squad, _agent.Squad) && _squad.Members.Contains(_agent)
            && ReferenceEquals(_previous, _agent.Objective.Location)
            && ReferenceEquals(_anchor, _squad.Objective.Location)
            && (_agent.Position - Origin).sqrMagnitude < 4f;

        public bool Step()
        {
            if (!Valid || Probes >= 48) { Done = true; return false; }
            // One sample and at most one path per shared dispatch slice. Search stays local
            // even if the floor has no remaining loot or no authored patrol points.
            var index = Probes++;
            var angle = ((index + _agent.Id * 5) % 16) * Mathf.PI / 8f;
            var radius = 8f * (1 + index / 16);
            var candidate = Origin + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
            if (!NavMesh.SamplePosition(candidate, out var hit, 2f, NavMesh.AllAreas)
                || !Allowed(hit.position) || (hit.position - Origin).sqrMagnitude < 25f) return true;
            foreach (var previous in _recent)
                if ((previous.Point - hit.position).sqrMagnitude < 16f) return true;
            foreach (var other in _squad.Members)
                if (other != _agent && ((other.Position - hit.position).sqrMagnitude < 4f
                    || other.Objective.Location != null && (other.Objective.Location.Position - hit.position).sqrMagnitude < 9f))
                    return true;
            if (!CalculateTimedPath(Origin, hit.position, _path, TransitionPhase.WaypointPath, "Kill patrol", _squad.Id)
                || _path.status != NavMeshPathStatus.PathComplete) return true;
            var corners = _path.corners;
            if (corners.Length == 0 || (corners[corners.Length - 1] - hit.position).sqrMagnitude > 1f
                || PathHelper.TotalLength(corners) > 64f) return true;
            foreach (var corner in corners)
                if (!Allowed(corner)) return true;
            // Patrol never opens an access shortcut through a locked room. A mesh can be
            // carved open for somebody else's unlock roll while the leaf is still locked.
            var locked = _owner.CollectNearbyLockedDoors(Origin, 76f);
            if (locked != null)
                foreach (var door in locked)
                    if (LockedDoorPath.Crosses(door, corners)) return true;
            Result = new Waypoint(_owner.NewRuntimeWaypointId(), WaypointCategory.Synthetic,
                "Kill patrol", hit.position, 1f, new(), new(), null);
            Done = true;
            return false;
        }

        private bool Allowed(Vector3 point)
        {
            var radius = ServerConfig.MainObjectives.RoamSplinterRadius;
            return Mathf.Abs(point.y - Origin.y) <= 1.5f
                && _owner.WorldToCell(point) == Main.CellCoords
                && XzDistanceSqr(point, Main.Position) <= radius * radius
                && _owner.MatchesZoneFloor(Main.ZoneFloorId, point);
        }
    }
}
