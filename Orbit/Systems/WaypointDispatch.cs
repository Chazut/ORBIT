using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly FrameSearchQueue _dispatchQueue = new();
    private readonly Dictionary<Entity, DispatchSearch> _dispatchSearches = new();
    private readonly List<Entity> _expiredDispatches = new();
    private bool _cachedDispatchPick;

    private sealed class DispatchSearch : IFrameSearch, IDisposable
    {
        internal readonly Squad Squad;
        internal readonly Agent Leader;
        internal readonly Vector3 RequestPosition, Origin;
        internal readonly Waypoint Previous, Objective;
        internal readonly bool Extract;
        internal readonly int MainState, FirstFrame;
        internal readonly SquadObjectiveState ObjectiveState;
        internal IEnumerator<Waypoint> Steps;
        internal Vector2Int? Cell;
        internal Waypoint Result;
        internal bool Done;
        internal float RequestedAt;
        internal int Queries, Slices;
        internal DispatchSearch(WaypointSystem owner, Entity entity, Vector3 position, Waypoint previous)
        {
            Squad = entity as Squad; Leader = Squad?.Leader;
            RequestPosition = position; Previous = previous; Origin = Leader?.Position ?? position;
            Objective = Squad?.Objective.Location; ObjectiveState = Squad?.Objective.Status ?? default;
            Extract = Squad?.ExtractRequested ?? false; MainState = MainSignature(Squad);
            RequestedAt = Time.time; FirstFrame = Time.frameCount;
            Steps = owner.SearchNear(this, entity, position, previous).GetEnumerator();
        }
        internal bool Valid => Time.time - RequestedAt < 3f && (Squad == null ||
            Leader != null && ReferenceEquals(Leader, Squad.Leader) && !Leader.Bot.IsDead
            && ReferenceEquals(Objective, Squad.Objective.Location) && ObjectiveState == Squad.Objective.Status
            && Extract == Squad.ExtractRequested && MainState == MainSignature(Squad)
            && (Leader.Position - Origin).sqrMagnitude < 25f && !ObjectiveCombat(Squad));
        public bool Step()
        {
            if (!Valid) { Dispose(); return false; }
            using var timing = PerformanceJournal.Measure(TransitionPhase.WaypointSearch, "waypoint-search", "RequestNear slice", Squad?.Id ?? -1);
            Slices++;
            try
            {
                if (Steps.MoveNext())
                {
                    Result = Steps.Current;
                    if (Result == null) return true;
                }
                Dispose();
                if (PerformanceJournal.Enabled && (Slices > 4 || Queries > 4))
                {
                    var detail = $"squad={Squad?.Id ?? -1} frames={Time.frameCount - FirstFrame + 1} slices={Slices} paths={Queries} result={Result?.Id ?? -1}";
                    Log.Always("PERF WAYPOINT SEARCH: " + detail);
                    PerformanceJournal.Event("waypoint-search-complete", detail: detail);
                }
                return false;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Done = true; Steps?.Dispose(); Steps = null; }
    }

    private static int MainSignature(Squad squad)
    {
        var hash = 17;
        if (squad?.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
                hash = unchecked(hash * 31 + (main.Completed ? 1 : 0));
        return hash;
    }

    internal bool DispatchPending(Entity entity) => _dispatchSearches.ContainsKey(entity);

    private void CancelDispatch(Entity entity)
    {
        if (!_dispatchSearches.Remove(entity, out var search)) return;
        _dispatchQueue.Remove(search); search.Dispose();
    }

    private void PumpDispatchSearches()
    {
        _expiredDispatches.Clear();
        foreach (var entry in _dispatchSearches)
            if (!entry.Value.Valid) _expiredDispatches.Add(entry.Key);
        foreach (var entity in _expiredDispatches) CancelDispatch(entity);
        _dispatchQueue.Pump(Time.frameCount);
    }

    public Waypoint RequestNear(Entity entity, Vector3 worldPos, Waypoint previous)
    {
        if (_dispatchSearches.TryGetValue(entity, out var search)
            && (!search.Valid || !ReferenceEquals(search.Previous, previous)
                || (search.RequestPosition - worldPos).sqrMagnitude >= 25f))
        { CancelDispatch(entity); search = null; }
        if (search == null)
        {
            search = new DispatchSearch(this, entity, worldPos, previous);
            _dispatchSearches.Add(entity, search); _dispatchQueue.Add(search);
        }
        search.RequestedAt = Time.time;
        _dispatchQueue.Pump(Time.frameCount);
        if (!search.Done) return null;
        var result = search.Result;
        var cell = search.Cell;
        CancelDispatch(entity);
        if (result == null) return null;
        // Recheck live filters when consuming a result: loot, doors and claims may have changed
        // while another squad was being processed. No new path queries are allowed here.
        _cachedDispatchPick = true;
        try
        {
            result = cell.HasValue ? PickFromCell(_cells[cell.Value.x, cell.Value.y], entity, cell.Value)
                : TryPickOwnKillCorpse(entity as Squad);
            if (result != null)
            {
                var assigned = cell.HasValue ? CommitWaypoint(entity, cell.Value, result) : result;
                if (assigned != null) return assigned;
            }
        }
        finally { _cachedDispatchPick = false; }
        // A stale result is a new search, not evidence that the squad is stranded.
        search = new DispatchSearch(this, entity, worldPos, previous);
        _dispatchSearches.Add(entity, search); _dispatchQueue.Add(search);
        return null;
    }

    private bool NeedsDispatchPath(Waypoint point, Squad squad)
        => RequiresReachabilityCheck(point.Category) && !_pathReachable.Contains(point.Id)
            && !IsSquadKnownUnreachable(squad, point.Id);

    private IEnumerable<Waypoint> PrepareOwnCorpses(Squad squad)
    {
        var corpses = new List<Waypoint>();
        var leaderCell = WorldToCell(squad.Leader.Position);
        foreach (var pair in _corpseKillerSquadId)
        {
            if (pair.Value != squad.Id || squad.CompletedPoiIds.Contains(pair.Key) || _claims.ContainsKey(pair.Key)
                || !_waypointCells.TryGetValue(pair.Key, out var cell)) continue;
            var delta = cell - leaderCell;
            if (Mathf.Abs(delta.x) > OwnKillCorpseMaxCellDistance || Mathf.Abs(delta.y) > OwnKillCorpseMaxCellDistance) continue;
            foreach (var point in _cells[cell.x, cell.y].Waypoints)
                if (point.Id == pair.Key && NeedsDispatchPath(point, squad)) { corpses.Add(point); break; }
        }
        foreach (var point in corpses)
        {
            yield return null;
            if (_waypointCells.ContainsKey(point.Id) && NeedsDispatchPath(point, squad))
            { IsWaypointReachable(point, squad); _dispatchSearches[squad].Queries++; }
        }
    }

    private IEnumerable<Waypoint> SearchCell(DispatchSearch search, Entity entity, Vector2Int intended)
    {
        var coords = ChooseAssignmentCell(entity, intended);
        var squad = entity as Squad;
        // Capture references, never hold an enumerator into a cell that runtime loot can mutate.
        var points = _cells[coords.x, coords.y].Waypoints.ToArray();
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if ((i & 31) == 0) yield return null;
            if (squad == null || !NeedsDispatchPath(point, squad) || _claims.ContainsKey(point.Id)
                || point.Category == WaypointCategory.Quest && !SquadOwnsQuest(squad, point)
                || !SquadCanUseWaypoint(squad, IsSquadPmc(squad), point)) continue;
            yield return null;
            if (_waypointCells.ContainsKey(point.Id) && NeedsDispatchPath(point, squad))
            { IsWaypointReachable(point, squad); search.Queries++; }
        }
        _cachedDispatchPick = true;
        Waypoint pick;
        try { pick = PickFromCell(_cells[coords.x, coords.y], entity, coords); }
        finally { _cachedDispatchPick = false; }
        if (pick != null) { search.Cell = coords; yield return pick; }
    }

    private IEnumerable<Waypoint> SearchFar(DispatchSearch search, Entity entity)
    {
        // Snapshot the round-robin order once; concurrent searches retain their own cursor.
        var cells = _validCellQueue.ToArray();
        if (_validCellQueue.Count > 0) _validCellQueue.Enqueue(_validCellQueue.Dequeue());
        foreach (var cell in cells)
            foreach (var point in SearchCell(search, entity, cell))
            { yield return point; if (point != null) yield break; }
    }
    private IEnumerable<Waypoint> SearchNear(DispatchSearch search, Entity entity, Vector3 worldPos, Waypoint previous)
    {
        // Always try and return assignments first to avoid counting our own influence into the decision.
        ReleaseAssignment(entity);

        // Pre-scan: if this squad has any tagged own-kill Corpse waypoint still alive (not claimed, not
        // blacklisted, reachable), bee-line to it before the normal neighbour scan runs. The neighbour scan
        // returns the first cell that yields any pick, so a fresh Synthetic in a closer-to-prefDir neighbour
        // could beat an own- kill Corpse two cells away.
        if (entity is Squad squadForKill)
        {
            foreach (var point in PrepareOwnCorpses(squadForKill)) yield return point;
            _cachedDispatchPick = true;
            Waypoint killPick;
            try { killPick = TryPickOwnKillCorpse(squadForKill); }
            finally { _cachedDispatchPick = false; }
            if (killPick != null) { yield return killPick; yield break; }
        }

        var requestCoords = WorldToCell(worldPos);

        if (!IsValidCell(requestCoords))
        {
            if (!FactionLocalOnly(entity))
                foreach (var point in SearchFar(search, entity)) yield return point;
            yield break;
        }

        // Closeness short-circuit: if the squad is currently in the anchor cell of any pending main
        // objective, pick from THIS cell instead of scanning neighbours. Without this, the inverse-distance
        // force pulls the squad into the anchor cell but the standard neighbour scan keeps picking waypoints
        // in surrounding cells, the squad orbits the objective without draining it.
        if (entity is Squad squadInAnchorCell
            && squadInAnchorCell.MainObjectives != null
            && IsCurrentCellAnchorOfPendingMain(squadInAnchorCell, requestCoords))
        {
            var localCellRef = _cells[requestCoords.x, requestCoords.y];
            if (localCellRef.HasWaypoints)
            {
                foreach (var point in SearchCell(search, entity, requestCoords))
                { yield return point; if (point != null) yield break; }
            }
            // No pick in the anchor cell (all claimed, all blacklisted, Quest reserved for a different squad)
            //, fall through.
        }

        // If this squad has been failing to find anything reachable for a while (scavs spawned on an island,
        // PMC trapped behind a closed door), don't let the dispatcher keep handing them neighbour/ map-wide
        // cells they can't reach either. Pin them to their own cell only, they'll wait there until a member
        // naturally drifts or the failure counter resets.
        if (IsSquadIslanded(entity))
        {
            var currentCell = _cells[requestCoords.x, requestCoords.y];
            if (!currentCell.HasWaypoints) yield break;
            foreach (var point in SearchCell(search, entity, requestCoords)) yield return point;
            yield break;
        }

        var previousCoords = previous == null ? requestCoords : WorldToCell(previous.Position);
        var neighbors = new List<Vector2Int>(8);

        // First pass: determine preferential direction
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                var direction = new Vector2Int(dx, dy);
                var coords = requestCoords + direction;

                if (!IsValidCell(coords))
                    continue;

                var cell = _cells[coords.x, coords.y];

                if (!cell.HasWaypoints)
                    continue;

                neighbors.Add(direction);
            }
        }

        var advectionVector = _advectionField[requestCoords.x, requestCoords.y]
            + ScopedAttraction(entity as Squad, requestCoords);
        var convergenceVector = _convergenceField[requestCoords.x, requestCoords.y];
        var randomization = Random.insideUnitCircle;
        randomization *= 0.5f;
        var momentumVector = (Vector2)(requestCoords - previousCoords);
        momentumVector.Normalize();
        momentumVector *= 0.5f;
        var homeVector = ComputeHomeAttraction(entity, requestCoords);
        var mainObjectiveVector = ComputeMainObjectiveAttraction(entity, requestCoords);

        var prefDirection = momentumVector + advectionVector + convergenceVector + randomization + homeVector + mainObjectiveVector;

        Log.Debug(
            $"Waypoint search from {requestCoords} direction: {prefDirection} mom: {momentumVector} adv: {advectionVector} conv: {convergenceVector} rand: {randomization} home: {homeVector} main: {mainObjectiveVector}"
        );

        if (neighbors.Count == 0 || prefDirection == Vector2.zero)
        {
            Log.Debug("Zero vector preferred direction, trying the current cell, and failing that the map-wide least congested cell");
            var currentCell = _cells[requestCoords.x, requestCoords.y];
            if (currentCell.HasWaypoints)
            {
                foreach (var point in SearchCell(search, entity, requestCoords))
                { yield return point; if (point != null) yield break; }
            }
            if (!FactionLocalOnly(entity))
                foreach (var point in SearchFar(search, entity)) yield return point;
            yield break;
        }

        prefDirection.Normalize();

        // Sort candidate neighbours by closeness to the preferred direction (lowest angle first). Iterating
        // in priority order lets us try the next-best neighbour when the first pick's waypoints are all
        // filtered, instead of jumping straight to RequestFar across the map.
        neighbors.Sort((a, b) =>
            Vector2.Angle(a, prefDirection).CompareTo(Vector2.Angle(b, prefDirection)));

        for (var i = 0; i < neighbors.Count; i++)
        {
            var neighbor = requestCoords + neighbors[i];
            foreach (var point in SearchCell(search, entity, neighbor))
            { yield return point; if (point != null) yield break; }
            // Every waypoint in this neighbour was filtered (unreachable from current leader nav position,
            // ineligible exfil, blacklisted), try the next-best neighbour before giving up on the local
            // area.
        }

        // Local area genuinely exhausted: also try the current cell itself (the neighbour-scan loop skipped
        // it), and only if that also fails do we escalate to the map-wide RequestFar.
        var localCellFinal = _cells[requestCoords.x, requestCoords.y];
        if (localCellFinal.HasWaypoints)
        {
            foreach (var point in SearchCell(search, entity, requestCoords))
            { yield return point; if (point != null) yield break; }
        }
        if (!FactionLocalOnly(entity))
            foreach (var point in SearchFar(search, entity)) yield return point;
    }


}
