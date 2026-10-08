using System.Collections.Generic;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class MovementSystem
{
    private readonly Dictionary<Agent, SpawnWaypointSearch> _spawnWaypointSearches = new();
    private readonly List<Agent> _expiredSpawnWaypointSearches = new();

    private void PruneSpawnWaypointSearches()
    {
        _expiredSpawnWaypointSearches.Clear();
        foreach (var pair in _spawnWaypointSearches)
            if (!pair.Value.Valid) _expiredSpawnWaypointSearches.Add(pair.Key);
        foreach (var agent in _expiredSpawnWaypointSearches) CancelSpawnWaypointSearch(agent);
    }

    // The distant fallback uses the same shared frame budget as objective searches. A failed
    // human reference must not veto every landing that connects to another part of the map.
    private sealed class SpawnWaypointSearch : IFrameSearch
    {
        private readonly MovementSystem _owner;
        private readonly Agent _agent;
        private readonly Vector3 _origin;
        private readonly float _started = Time.time;
        private readonly float _radiusSqr;
        private readonly List<(Player Player, bool Human)> _references = new(4);
        private readonly List<(Vector3 Point, int Next)> _candidates = new(40);
        private readonly NavMeshPath _path = new();
        private readonly int _total, _start;
        private int _cursor, _reference;
        private bool _hasPoint;
        private Vector3 _point, _referencePosition, _referenceAnchor;
        private Player _selected;
        private bool _selectedHuman;
        private int _tested, _mesh, _height, _range, _occupied, _visible, _unreachable, _recent;
        private int _queries, _partial, _invalid, _referenceMesh, _referenceUnavailable;
        internal bool Done, Found;
        internal Vector3 Destination;

        internal SpawnWaypointSearch(MovementSystem owner, Agent agent, List<Agent> liveAgents,
            List<Waypoint> points, float radius)
        {
            _owner = owner; _agent = agent; _origin = agent.Position; _radiusSqr = radius * radius;
            _total = points.Count;
            _start = _total > 0 ? agent.Stuck.SpawnIslandWaypointCursor % _total : 0;
            var nearCount = _start >= 40 ? Mathf.Min(20, _total) : 0;
            var end = Mathf.Min(_total, _start + 40 - nearCount);
            for (var step = 0; step < nearCount + end - _start; step++)
            {
                var i = step < nearCount ? step : _start + step - nearCount;
                _candidates.Add((points[i].Position, step < nearCount ? -1 : i + 1 < _total ? i + 1 : 0));
            }
            if (_total == 0) agent.Stuck.SpawnIslandWaypointCursor = 0;

            // Reserve two references for external bots even in a multiplayer raid.
            foreach (var human in owner._humanPlayers)
            {
                if (human?.HealthController is not { IsAlive: true }
                    || (human.Position - _origin).sqrMagnitude < SpawnIslandMinReferenceDistSqr) continue;
                _references.Add((human, true));
                if (_references.Count == 2) break;
            }
            Agent first = null, second = null;
            var firstDistance = float.MaxValue;
            var secondDistance = float.MaxValue;
            foreach (var other in liveAgents)
            {
                if (other == null || other == agent || other.Player?.HealthController is not { IsAlive: true }
                    || agent.Squad != null && other.Squad?.Id == agent.Squad.Id) continue;
                var distance = (other.Position - _origin).sqrMagnitude;
                if (distance < SpawnIslandMinReferenceDistSqr) continue;
                if (distance < firstDistance)
                { second = first; secondDistance = firstDistance; first = other; firstDistance = distance; }
                else if (distance < secondDistance) { second = other; secondDistance = distance; }
            }
            if (first != null) _references.Add((first.Player, false));
            if (second != null) _references.Add((second.Player, false));
        }

        internal bool Valid => _agent.IsActive && _agent.Bot != null && !_agent.Bot.IsDead
            && _agent.Player?.HealthController is { IsAlive: true } && !_agent.Stuck.SpawnIslandRescued
            && _agent.Objective.Status is not (ObjectiveStatus.Looting or ObjectiveStatus.Extracting)
            && !_agent.Bot.Memory.IsUnderFire && _agent.Bot.Memory.GoalEnemy == null
            && !GhostBodyTransition.Busy(_agent.Player)
            && (_agent.Squad == null || Time.time >= _agent.Squad.GhostFightUntil)
            && (_agent.Position - _origin).sqrMagnitude <= 1f
            && Mathf.Abs(_agent.Position.y - _origin.y) < .75f && Time.time - _started < 10f;

        internal bool ReferenceStillValid => _selected?.HealthController is { IsAlive: true }
            && (_selected.Position - _referencePosition).sqrMagnitude <= 9f;

        public bool Step()
        {
            if (!Valid) return Finish(false);
            using var timing = MeasureMovement(TransitionPhase.MovementSpawnRescue, "spawn waypoint slice", _agent);
            if (!_hasPoint)
            {
                if (_cursor >= _candidates.Count || _references.Count == 0) return Finish(false);
                var candidate = _candidates[_cursor++];
                _tested++;
                if (candidate.Next >= 0) _agent.Stuck.SpawnIslandWaypointCursor = candidate.Next;
                if (!NavMesh.SamplePosition(candidate.Point, out var hit, 2f, NavMesh.AllAreas)
                    || !OrbitMovementRecovery.TrySample(hit.position, out _point)) { _mesh++; return true; }
                if (Mathf.Abs(_point.y - _origin.y) > 2f) { _height++; return true; }
                if ((_point - _origin).sqrMagnitude > _radiusSqr) { _range++; return true; }
                if ((_point - _origin).sqrMagnitude < 9f
                    || _agent.Stuck.Recovery.RecentlyRescuedAt(_point)) { _recent++; return true; }
                if (!_owner.IsClearOfPlayersAndBots(_point, _agent, _owner._recoveryAgents)
                    || DangerZones.IsInside(_point) || !BotLandingGuard.Accepts(_agent.Bot, _point))
                { _occupied++; return true; }
                if (!_owner.IsRescueDestinationHidden(_point)) { _visible++; return true; }
                _hasPoint = true;
                _reference = 0;
            }

            var reference = _references[_reference++];
            // Sample the current position, not the original raw player transform. Sampling failure
            // only excludes this reference; the next frame slice can still try another one.
            if (reference.Player?.HealthController is not { IsAlive: true }
                || (reference.Player.Position - _origin).sqrMagnitude < SpawnIslandMinReferenceDistSqr)
                _referenceUnavailable++;
            else if (!OrbitMovementRecovery.TrySample(reference.Player.Position, out var anchor)) _referenceMesh++;
            else
            {
                _queries++;
                var calculated = NavMesh.CalculatePath(anchor, _point, NavMesh.AllAreas, _path);
                if (calculated && _path.status == NavMeshPathStatus.PathComplete)
                {
                    _selected = reference.Player; _selectedHuman = reference.Human;
                    _referencePosition = reference.Player.Position;
                    _referenceAnchor = anchor;
                    Destination = _point;
                    return Finish(true);
                }
                if (calculated && _path.status == NavMeshPathStatus.PathPartial) _partial++; else _invalid++;
            }
            if (_reference == _references.Count) { _unreachable++; _hasPoint = false; }
            return true;
        }

        private bool Finish(bool found)
        {
            Done = true; Found = found;
            Log.Debug($"{_agent} spawn-island candidates: total={_total} start={_start} tested={_tested} next={_agent.Stuck.SpawnIslandWaypointCursor} mesh={_mesh} height={_height} range={_range} occupied={_occupied} visible={_visible} unreachable={_unreachable} recent={_recent} found={found} references={_references.Count} queries={_queries} partial={_partial} invalid={_invalid} referenceMesh={_referenceMesh} referenceUnavailable={_referenceUnavailable}");
            if (found)
                Log.Debug($"{_agent} spawn-island reference: selected={(_selectedHuman ? "human" : "bot")} name={_selected.Profile.Nickname} from={_referenceAnchor} raw={_referencePosition} landing={Destination} queries={_queries}");
            return false;
        }
    }

    private void CancelSpawnWaypointSearch(Agent agent)
    {
        if (_spawnWaypointSearches.Remove(agent, out var search)) _waypointSystem.CancelFrameSearch(search);
    }

    private void ContinueSpawnWaypointRescue(Agent agent, List<Agent> liveAgents)
    {
        var stuck = agent.Stuck;
        var found = TryFindReachableWaypoint(agent, liveAgents, agent.Position,
            SpawnIslandWaypointSearchRadius, out var wpDest, out var pending);
        if (pending) return;
        // Players, combat and occupancy may have changed while the search waited in the shared queue.
        if (found && TeleportSafe(agent, _humanPlayers) && IsClearOfPlayersAndBots(wpDest, agent, liveAgents)
            && IsRescueDestinationHidden(wpDest) && !DangerZones.IsInside(wpDest)
            && !BotLandingGuard.IsRejected(agent.Bot, wpDest))
        {
            var fromPos = agent.Position;
            if (BotLandingGuard.TryPlace(agent.Bot, wpDest, "spawn-rescue",
                    () => ResumeGroundPlacement(agent), validateGround: true))
            {
                ResetAfterRescue(agent, resume: false);
                stuck.SpawnIslandRescued = true;
                Log.Info($"{agent} spawn-island rescue: teleported {Vector3.Distance(fromPos, wpDest):F0}m to a reachable waypoint (off the disconnected spawn chunk) from={fromPos} to={wpDest}");
                RefreshSquadAfterIslandRescue(agent, wpDest);
                return;
            }
        }
        var delay = SpawnIslandRetryDelay(++stuck.SpawnIslandAttempts);
        stuck.SpawnIslandNextProbeAt = Time.time + delay;
        Log.Debug($"{agent} spawn-island rescue: attempt {stuck.SpawnIslandAttempts} found no reachable waypoint, next probe in {delay:F0}s");
    }
}
