using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// One bounded search per operation. A partial path is only an approach leg, never permission to interact.
internal sealed class OperationRouteSearch
{
    private readonly NavMeshPath _path = new();
    private int _candidate;
    // Reserve space for both kinds: attractive partial endpoints must not evict known complete legs.
    private readonly List<Approach> _complete = new(MaxApproachesPerKind);
    private readonly List<Approach> _partial = new(MaxApproachesPerKind);
    private readonly struct Approach(Vector3 point, float score)
    {
        internal readonly Vector3 Point = point;
        internal readonly float Score = score;
    }
    private const int MaxApproachesPerKind = 8;
    private int _completePaths, _checks, _rejected, _invalidQueries;
    private int _noSample, _wrongFloor, _outsideTarget, _noCorners, _tooClose, _noProgress, _visited;
    private int _outsideScope, _blockedInteraction;
    private Vector3 _lastRejected;
    private Vector3 _target;
    private bool _localAccess;
    private string _lastRejection = "none";
    internal bool Pending { get; private set; }
    internal int Samples { get; private set; }
    internal int PartialPaths { get; private set; }
    internal int InvalidPaths { get; private set; }
    private const int LocalCandidates = 20;
    private const int DirectCandidates = LocalCandidates + 3;
    private const int ExpandedCandidates = 42;

    internal string Diagnostics => $"complete={_completePaths} invalidQueries={_invalidQueries} approachChecks={_checks} approachRejected={_rejected}"
        + $" searchTarget={_target} lastRejected={_lastRejected} lastRejection={_lastRejection}"
        + $" filters=[no-sample:{_noSample},floor:{_wrongFloor},outside-target:{_outsideTarget},no-corners:{_noCorners},near:{_tooClose},no-progress:{_noProgress},visited:{_visited},scope:{_outsideScope},interaction:{_blockedInteraction}] localAccess={_localAccess}";

    internal void Reset()
    {
        _candidate = Samples = PartialPaths = InvalidPaths = 0;
        _complete.Clear(); _partial.Clear();
        _completePaths = _checks = _rejected = _invalidQueries = 0;
        _noSample = _wrongFloor = _outsideTarget = _noCorners = _tooClose = _noProgress = _visited = 0;
        _outsideScope = _blockedInteraction = 0;
        _lastRejected = _target = default; _lastRejection = "none";
        _localAccess = false;
        Pending = false;
    }

    internal bool Find(Vector3 origin, Vector3 target, out Vector3 point, out bool final,
        int expansion = 0, IReadOnlyList<Vector3> avoided = null, bool floorAware = false, float finalHeightTolerance = 2.5f,
        Func<Vector3, bool> allowedPoint = null, Func<Vector3, bool> finalPoint = null, bool localAccess = false)
    {
        point = default;
        _target = target;
        final = false;
        Pending = false;
        expansion = Mathf.Clamp(expansion, 0, 2);
        // Vertical objectives need access routes on several floors, including stairs behind us.
        // Enumeration is still resumed in slices of at most three navigation calls.
        var expandedCandidates = floorAware ? 128 : ExpandedCandidates;
        // Once beside a switch, search the entrance and corners of its small enclosure.
        // Complete final paths may still take any necessary detour around the building.
        var nearbyAccess = localAccess && HorizontalDistance(origin, target) <= 15f
            && Mathf.Abs(origin.y - target.y) <= 2.5f;
        _localAccess = nearbyAccess;
        var totalCandidates = DirectCandidates + (nearbyAccess ? 16 : expansion * expandedCandidates);
        var separation = nearbyAccess ? 2f : 5f;
        var work = 0;
        while (_candidate < totalCandidates && work < 3)
        {
            work++;
            var index = _candidate++;
            var local = index < LocalCandidates;
            var sampleRadius = local ? 1.25f : 3f;
            Vector3 sample;
            if (index < 4) sample = target + Vector3.down * (index * .6f);
            else if (local)
            {
                var angle = (index - 4) % 8 * Mathf.PI / 4f;
                var radius = index < 12 ? 1.25f : 2.25f;
                sample = target + new Vector3(Mathf.Cos(angle) * radius, -1f, Mathf.Sin(angle) * radius);
            }
            else if (index < DirectCandidates)
            {
                // Walk connected ground toward a distant room when its interior cannot yet be resolved.
                var distance = Vector3.Distance(origin, target);
                if (distance < 20f) continue;
                var length = index == LocalCandidates ? 80f : index == LocalCandidates + 1 ? 40f : 20f;
                sample = Vector3.MoveTowards(origin, target, Mathf.Min(length, distance * .65f));
                sample.y = origin.y;
            }
            else if (nearbyAccess)
            {
                var slot = index - DirectCandidates;
                var angle = slot % 8 * Mathf.PI / 4f;
                var radius = slot < 8 ? 4f : 7f;
                sample = target + new Vector3(Mathf.Cos(angle) * radius, -1.4f, Mathf.Sin(angle) * radius);
                sampleRadius = 1.25f;
            }
            else if (floorAware)
            {
                var probe = index - DirectCandidates;
                var pass = probe / expandedCandidates;
                var slot = probe % expandedCandidates;
                var heading = slot % 16;
                var angle = heading * Mathf.PI / 8f;
                var length = (slot / 16 % 2 == 0 ? 20f : 40f) * (pass + 1);
                var direction = target - origin; direction.y = 0;
                if (direction.sqrMagnitude < .01f) direction = new Vector3(1, 0, 0);
                direction = direction.normalized;
                sample = origin + new Vector3(direction.x * Mathf.Cos(angle) - direction.z * Mathf.Sin(angle), 0,
                    direction.x * Mathf.Sin(angle) + direction.z * Mathf.Cos(angle)) * length;
                sample.y = origin.y + (target.y - origin.y) * (slot / 32 / 3f);
                sampleRadius = pass == 0 ? 3f : 5f;
            }
            else
            {
                // Broaden failed Rush approaches across terrain and around obstacles. These are
                // intermediate destinations only; the real room/door still needs a local route.
                var probe = index - DirectCandidates;
                var pass = probe / ExpandedCandidates;
                var slot = probe % ExpandedCandidates;
                var heading = slot % 7;
                var angle = ((heading + 1) / 2) * (heading % 2 == 0 ? -1 : 1) * Mathf.PI / 6f;
                var distance = Vector3.Distance(origin, target);
                var length = Mathf.Min((slot / 7 % 2 == 0 ? 40f : 80f) * (pass + 1), Mathf.Max(10f, distance * .65f));
                var direction = target - origin; direction.y = 0;
                direction = direction.normalized;
                sample = origin + new Vector3(direction.x * Mathf.Cos(angle) - direction.z * Mathf.Sin(angle), 0,
                    direction.x * Mathf.Sin(angle) + direction.z * Mathf.Cos(angle)) * length;
                var height = slot / 14;
                sample.y = height == 0 ? origin.y : origin.y + (target.y - origin.y) * Mathf.Min(1f, length / Mathf.Max(1f, distance))
                    + (height == 1 ? -8f : 8f);
                sampleRadius = pass == 0 ? 6f : 12f;
            }
            if (!NavMesh.SamplePosition(sample, out var hit, sampleRadius, NavMesh.AllAreas)) { _noSample++; continue; }
            if (floorAware && !local && Mathf.Abs(hit.position.y - sample.y) > 2.5f) { _wrongFloor++; continue; }
            if (local && Mathf.Abs(hit.position.y - target.y) > finalHeightTolerance) { _wrongFloor++; continue; }
            if (local && HorizontalDistance(hit.position, target) > 3f) { _outsideTarget++; continue; }
            if (allowedPoint != null && !allowedPoint(hit.position)) { _outsideScope++; continue; }
            if (local && finalPoint != null && !finalPoint(hit.position)) { _blockedInteraction++; continue; }
            Samples++;
            if (!NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, _path)
                || _path.status == NavMeshPathStatus.PathInvalid) { InvalidPaths++; _invalidQueries++; continue; }
            if (_path.status == NavMeshPathStatus.PathComplete) _completePaths++;
            if (_path.status == NavMeshPathStatus.PathPartial) PartialPaths++;
            var corners = _path.corners;
            if (!OperationRoutePolicy.AllowsPath(corners, allowedPoint)) { _outsideScope++; continue; }
            if (_path.status == NavMeshPathStatus.PathComplete && local)
            {
                if (corners == null || corners.Length == 0
                    || Vector3.Distance(corners[corners.Length - 1], hit.position) > 1f) { _noCorners++; continue; }
                point = hit.position; final = true; return true;
            }
            if (corners == null || corners.Length < 2) { _noCorners++; continue; }
            var end = corners[corners.Length - 1];
            var gap = Vector3.Distance(end, target);
            var originGap = Vector3.Distance(origin, target);
            // Require actual, useful progress; a wall endpoint beside the bot cannot create a retry loop.
            var score = gap + (floorAware ? Mathf.Abs(end.y - target.y) * 2f : 0);
            if (Vector3.Distance(origin, end) < separation) { _tooClose++; continue; }
            var detour = nearbyAccess ? 6f : floorAware && expansion > 0 ? expansion * 60f : expansion >= 2 ? 20f : -3f;
            if (gap > originGap + detour) { _noProgress++; continue; }
            if (Visited(end, avoided, separation)) { _visited++; continue; }
            RememberApproach(end, score, _path.status == NavMeshPathStatus.PathComplete);
        }
        if (_candidate < totalCandidates) { Pending = true; return false; }
        // Verify in score order, continuing with another candidate after an invalid endpoint.
        // These calls consume the same slice as enumeration, including after a deferred resume.
        while (_complete.Count + _partial.Count > 0)
        {
            if (work >= 3) { Pending = true; return false; }
            work++;
            var candidates = _partial.Count == 0 || _complete.Count > 0 && _complete[0].Score <= _partial[0].Score
                ? _complete : _partial;
            var approach = candidates[0].Point;
            candidates.RemoveAt(0);
            // The actor or its approach history may have advanced while this search was queued.
            if (Vector3.Distance(origin, approach) < separation) { RejectApproach(approach, "too-close"); continue; }
            if (Visited(approach, avoided, separation)) { RejectApproach(approach, "visited"); continue; }
            _checks++;
            var calculated = NavMesh.CalculatePath(origin, approach, NavMesh.AllAreas, _path);
            if (calculated && _path.status == NavMeshPathStatus.PathComplete)
            {
                if (!OperationRoutePolicy.AllowsPath(_path.corners, allowedPoint))
                { _outsideScope++; RejectApproach(approach, "outside-scope"); continue; }
                point = approach; return true;
            }
            InvalidPaths++;
            RejectApproach(approach, !calculated ? "query-failed"
                : _path.status == NavMeshPathStatus.PathPartial ? "partial-endpoint" : "invalid-endpoint");
        }
        return false;
    }

    private void RememberApproach(Vector3 point, float score, bool complete)
    {
        // The same wall corner often comes from every local probe; check it only once per search.
        var candidates = complete ? _complete : _partial;
        var other = complete ? _partial : _complete;
        for (var i = 0; i < other.Count; i++)
            if ((other[i].Point - point).sqrMagnitude < .25f)
            {
                if (!complete) return;
                other.RemoveAt(i);
                break;
            }
        for (var i = 0; i < candidates.Count; i++)
            if ((candidates[i].Point - point).sqrMagnitude < .25f) return;
        var index = 0;
        while (index < candidates.Count && candidates[index].Score <= score) index++;
        if (index >= MaxApproachesPerKind) return;
        if (candidates.Count == MaxApproachesPerKind) candidates.RemoveAt(candidates.Count - 1);
        candidates.Insert(index, new Approach(point, score));
    }

    private void RejectApproach(Vector3 point, string reason)
    { _rejected++; _lastRejected = point; _lastRejection = reason; }

    internal static void Remember(List<Vector3> history, Vector3 point, float separation = 5f)
    {
        if (Visited(point, history, separation)) return;
        if (history.Count >= 64) history.RemoveAt(0);
        history.Add(point);
    }

    private static bool Visited(Vector3 point, IReadOnlyList<Vector3> avoided, float separation = 5f)
    {
        if (avoided == null) return false;
        for (var i = 0; i < avoided.Count; i++)
            if ((point - avoided[i]).sqrMagnitude < separation * separation) return true;
        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    { var delta = a - b; delta.y = 0; return delta.magnitude; }
}
