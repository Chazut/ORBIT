using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly List<Waypoint> _exfilPoints = new();
    private readonly Dictionary<Entity, ExfilSearch> _exfilSearches = new();
    private readonly List<Entity> _expiredExfils = new();

    private sealed class ExfilSearch : IFrameSearch
    {
        private readonly WaypointSystem _owner;
        internal readonly Squad Squad;
        internal readonly Agent Agent, Solo;
        internal readonly Vector3 Origin;
        private readonly NearestExfilSearch _search = new();
        private readonly NavMeshPath _path = new();
        private readonly Func<Waypoint, NearestExfilSearch.Route> _query;
        private readonly bool? _pmc;
        private readonly bool _logEligibility;
        private int _candidate, _queries, _firstFrame;
        private bool _routing;
        internal bool Done;
        internal float RequestedAt;
        internal Waypoint Result => _search.Result;
        internal bool Valid => Time.time - RequestedAt < 5f && !Agent.Bot.IsDead
            && Squad.Members.Contains(Agent) && (Solo != null || ReferenceEquals(Squad.Leader, Agent))
            && (Agent.Position - Origin).sqrMagnitude < 100f;

        internal ExfilSearch(WaypointSystem owner, Squad squad, Agent solo)
        {
            _owner = owner; Squad = squad; Solo = solo; Agent = solo ?? squad.Leader;
            Origin = Agent.Position; RequestedAt = Time.time; _firstFrame = Time.frameCount;
            var role = Agent.Bot.Profile?.Info?.Settings?.Role;
            if (role.HasValue) _pmc = role.Value.IsPMC();
            _logEligibility = solo == null && owner.BeginExfilDiagnostics(squad, _pmc);
            _query = Query;
        }

        public bool Step()
        {
            if (!Valid) { Done = true; return false; }
            using var timing = PerformanceJournal.Measure(TransitionPhase.ExfilSearch, "exfil-search", squad: Squad.Id);
            if (!_routing)
            {
                using var eligibility = PerformanceJournal.Measure(TransitionPhase.ExfilEligibility, "exfil-eligibility", squad: Squad.Id);
                // Eligibility is cheap but still bounded independently of the number of exits.
                var stop = Math.Min(_candidate + 8, _owner._exfilPoints.Count);
                for (; _candidate < stop; _candidate++)
                {
                    var point = _owner._exfilPoints[_candidate];
                    var entry = _owner.SquadCanUseWaypoint(Squad, _pmc, point, Solo);
                    var fallback = !entry && _owner.SquadCanUseWaypointIgnoringEntry(Squad, _pmc, point, Solo);
                    if (_logEligibility) _owner.LogEligibleExfil(Squad, point, Origin, entry, fallback);
                    if (Squad.CompletedPoiIds.Contains(point.Id) || !entry && !fallback) continue;
                    _search.Add(point, (point.Position - Origin).sqrMagnitude, entry);
                }
                if (_candidate < _owner._exfilPoints.Count) return true;
                _search.Begin(); _routing = true;
                return true;
            }
            if (_search.Step(_query)) return true;
            Done = true;
            if (_search.Fallback)
                Log.Warning($"{Squad} no spawn-side eligible exfil, falling back to nearest reachable faction-allowed exfil {Result} (entry derivation may have failed)");
            else if (_search.Partial)
                Log.Info($"{Squad} no fully reachable exfil, committing to partial-path exfil {Result} (will walk as far as the mesh allows)");
            if (PerformanceJournal.Enabled)
            {
                var detail = $"squad={Squad.Id} solo={Solo?.Id ?? -1} frames={Time.frameCount - _firstFrame + 1} paths={_queries} result={Result?.Id ?? -1}";
                Log.Info("PERF EXFIL SEARCH: " + detail);
                PerformanceJournal.Event("exfil-search-complete", Agent.Bot.Profile?.Id, detail, Squad.Id);
            }
            return false;
        }

        private NearestExfilSearch.Route Query(Waypoint point)
        {
            if (Squad.CompletedPoiIds.Contains(point.Id) || !_owner.SquadCanUseWaypointIgnoringEntry(Squad, _pmc, point, Solo))
                return new(false, float.MaxValue);
            _queries++;
            var found = CalculateTimedPath(Origin, point.Position, _path, TransitionPhase.ExfilPath, point.Name, Squad.Id);
            if (found && _path.status == NavMeshPathStatus.PathComplete) return new(true, 0);
            var corners = _path.corners;
            var gap = !found || _path.status == NavMeshPathStatus.PathInvalid || corners == null || corners.Length == 0
                ? float.MaxValue : (corners[corners.Length - 1] - point.Position).sqrMagnitude;
            return new(false, gap);
        }

        internal bool ResultValid => Result == null || !Squad.CompletedPoiIds.Contains(Result.Id)
            && _owner.SquadCanUseWaypointIgnoringEntry(Squad, _pmc, Result, Solo);
    }

    internal bool ExfilSearchPending(Squad squad, Agent solo = null)
        => _exfilSearches.TryGetValue((Entity)solo ?? squad, out var search) && !search.Done;

    private void CancelExfilSearch(Entity key)
    {
        if (_exfilSearches.Remove(key, out var search)) _dispatchQueue.Remove(search);
    }

    private void PruneExfilSearches()
    {
        _expiredExfils.Clear();
        foreach (var pair in _exfilSearches)
            if (!pair.Value.Valid) _expiredExfils.Add(pair.Key);
        foreach (var key in _expiredExfils) CancelExfilSearch(key);
    }

    public Waypoint FindNearestEligibleExfil(Squad squad, Agent solo = null)
    {
        if (squad?.Leader?.Bot == null) return null;
        if ((solo ?? squad.Leader).Bot.IsDead) return null;
        Entity key = (Entity)solo ?? squad;
        if (_exfilSearches.TryGetValue(key, out var search)
            && (!search.Valid || search.Done && (!search.ResultValid || Time.time - search.RequestedAt >= 2f)))
        { CancelExfilSearch(key); search = null; }
        if (search == null)
        {
            search = new ExfilSearch(this, squad, solo);
            _exfilSearches.Add(key, search); _dispatchQueue.Add(search);
        }
        // Keep pending requests alive, but let a completed verdict expire after two seconds.
        if (!search.Done) search.RequestedAt = Time.time;
        _dispatchQueue.Pump(Time.frameCount);
        if (!search.Done) return null;
        if (!search.Valid || !search.ResultValid)
        {
            CancelExfilSearch(key);
            search = new ExfilSearch(this, squad, solo);
            _exfilSearches.Add(key, search); _dispatchQueue.Add(search);
            return null;
        }
        if (solo == null)
        { squad.NearestExfilCached = search.Result; squad.NearestExfilCachedAt = search.RequestedAt; }
        return search.Result;
    }
}
