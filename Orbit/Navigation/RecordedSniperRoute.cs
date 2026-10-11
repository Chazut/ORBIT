using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// Sniper rescue follows the recorded stairs once joined. Native navigation still validates
// every leg, but an unrelated lower-floor detour cannot count as progress along the recording.
internal sealed class RecordedSniperRoute
{
    private readonly NavMeshPath _path = new();
    private readonly List<Entry> _entries = new();
    private readonly List<Vector3> _partialEnds = new();
    private readonly struct Entry(RecordedRoute route, int index, float score)
    {
        internal readonly RecordedRoute Route = route;
        internal readonly int Index = index;
        internal readonly float Score = score;
    }
    private RecordedRoute _route;
    private string _map;
    private int _entry, _probe, _variant;
    private Vector3 _arrival, _issued, _sampledTarget;
    private bool _hasArrival, _partialLeg, _hasSample;
    private float _bestGap = float.MaxValue;
    private string _reason = "entry pending";
    internal bool Active { get; private set; }
    internal bool Pending { get; private set; }
    internal bool Complete { get; private set; }
    internal bool HasTarget => _route != null;
    internal string Key { get; private set; }
    internal int Cursor { get; private set; }
    internal Vector3 Target => HasTarget ? _route.Points[Cursor] : default;
    internal Vector3 TransitTarget => _hasSample ? _sampledTarget : Target;
    internal Vector3[] RouteCorners { get; private set; }
    internal float StalledFor { get; private set; }
    internal string Diagnostics => $"recording={Key ?? "none"} variant={_variant} cursor={Cursor} joined={HasTarget} routeComplete={Complete} stalled={StalledFor:F1}s routeReason={_reason}";

    internal void Reset()
    {
        Active = Pending = Complete = _hasArrival = _partialLeg = _hasSample = false;
        _map = Key = null; _route = null; Cursor = _entry = _probe = _variant = 0;
        _entries.Clear(); _partialEnds.Clear(); RouteCorners = null;
        _bestGap = float.MaxValue; StalledFor = 0; _reason = "entry pending";
    }
    // Retries and combat pauses must not erase the cursor or the failed partial endpoints.
    internal void ResetSearch() { Pending = false; RouteCorners = null; }
    internal void Suspend() => StalledFor = 0;
    internal void RememberPartialLeg()
    {
        if (_partialLeg) OperationRouteSearch.Remember(_partialEnds, _issued, 1.5f);
        _partialLeg = false;
    }
    internal bool Enable(string map, string key, Vector3 origin)
    {
        if (Active && _map == map && Key == key) return true;
        if (!RecordedRouteLibrary.Has(map, key)) return false;
        Reset(); Active = true; _map = map; Key = key;
        foreach (var route in RecordedRouteLibrary.Routes)
        {
            if (!RecordedRouteLibrary.Matches(route, map, key)) continue;
            for (var i = 0; i < route.Points.Length; i++)
            {
                var delta = route.Points[i] - origin;
                // Same-floor entries come first, then the closest other connected entry.
                var score = delta.sqrMagnitude + Mathf.Abs(delta.y) * 12f
                    + (Mathf.Abs(delta.y) > 1.25f ? 1000000f : 0);
                _entries.Add(new Entry(route, i, score));
            }
        }
        _entries.Sort((a, b) => a.Score.CompareTo(b.Score));
        return true;
    }

    internal void Observe(Vector3 origin, float elapsed)
    {
        if (!HasTarget || Complete) return;
        var previous = Cursor;
        if (Reached(origin, _hasArrival ? _arrival : Target))
        {
            if (Cursor + 1 < _route.Points.Length) Cursor++;
            else Complete = true;
            _hasArrival = false;
            while (Cursor + 1 < _route.Points.Length && Reached(origin, Target)) Cursor++;
            if (Cursor == _route.Points.Length - 1 && Reached(origin, Target)) Complete = true;
        }
        if (Cursor != previous || Complete)
        {
            _partialEnds.Clear(); _partialLeg = _hasSample = false; _probe = 0;
            _bestGap = float.MaxValue; StalledFor = 0;
        }
        var gap = Vector3.Distance(origin, Target);
        if (gap < _bestGap - .75f) { _bestGap = gap; StalledFor = 0; }
        else StalledFor += Mathf.Clamp(elapsed, 0, 1);
    }

    internal bool Find(Vector3 origin, out Vector3 point)
    {
        point = default; Pending = false; RouteCorners = null;
        if (!Active) return false;
        if (!HasTarget) return Join(origin, out point);
        Observe(origin, 0);
        if (Complete) { point = _hasArrival ? _arrival : Target; return true; }
        // One target query and, for a partial path, one verification query per work slot.
        // Small samples tolerate footstep/mesh differences without changing floor.
        var probe = _probe++ % 5;
        var sample = Target + (probe == 1 ? new Vector3(.35f, 0, 0)
            : probe == 2 ? new Vector3(-.35f, 0, 0)
            : probe == 3 ? new Vector3(0, 0, .35f)
            : probe == 4 ? new Vector3(0, 0, -.35f) : Vector3.zero);
        if (!Sample(sample, Target, out var target)) { _reason = "target mesh"; return false; }
        _sampledTarget = target; _hasSample = true;
        if (!NavMesh.CalculatePath(origin, target, NavMesh.AllAreas, _path)
            || _path.status == NavMeshPathStatus.PathInvalid) { _reason = "invalid path"; return false; }
        var corners = _path.corners;
        if (!LocalPath(origin, target, corners)) { _reason = "outside recorded segment"; return false; }
        var end = corners[corners.Length - 1];
        if (_path.status == NavMeshPathStatus.PathComplete && Vector3.Distance(end, target) <= .75f)
        {
            _arrival = target; _hasArrival = true; _partialLeg = false;
            RouteCorners = corners; point = target; _reason = "recorded leg"; return true;
        }
        if (Vector3.Distance(origin, end) < 1.5f
            || Vector3.Distance(end, Target) > Vector3.Distance(origin, Target) - .75f)
        { _reason = "partial without progress"; return false; }
        foreach (var failed in _partialEnds)
            if ((end - failed).sqrMagnitude < 2.25f) { _reason = "repeated partial endpoint"; return false; }
        if (!NavMesh.CalculatePath(origin, end, NavMesh.AllAreas, _path)
            || _path.status != NavMeshPathStatus.PathComplete || !LocalPath(origin, target, _path.corners))
        { _reason = "partial endpoint unreachable"; return false; }
        corners = _path.corners;
        if (Vector3.Distance(corners[corners.Length - 1], end) > .75f) return false;
        _issued = end; _partialLeg = true; _hasArrival = false;
        RouteCorners = corners; point = end; _reason = "partial approach"; return true;
    }

    private bool Join(Vector3 origin, out Vector3 point)
    {
        point = default;
        // Enumeration resumes through the existing shared work queue, never one raid-frame scan.
        for (var work = 0; work < 3 && _entry < _entries.Count; work++)
        {
            var entry = _entries[_entry++];
            if (!Sample(entry.Route.Points[entry.Index], entry.Route.Points[entry.Index], out var target)) continue;
            if (!NavMesh.CalculatePath(origin, target, NavMesh.AllAreas, _path)
                || _path.status != NavMeshPathStatus.PathComplete) continue;
            var corners = _path.corners;
            if (corners == null || corners.Length == 0 || Vector3.Distance(corners[corners.Length - 1], target) > .75f) continue;
            _route = entry.Route; Cursor = entry.Index;
            foreach (var route in RecordedRouteLibrary.Routes)
            { if (route == _route) break; if (RecordedRouteLibrary.Matches(route, _map, Key)) _variant++; }
            _arrival = _sampledTarget = target; _hasArrival = _hasSample = true;
            RouteCorners = corners; point = target; _reason = "reachable entry";
            _bestGap = Vector3.Distance(origin, target); StalledFor = 0;
            _entries.Clear(); return true;
        }
        Pending = _entry < _entries.Count;
        if (!Pending) { _entry = 0; _reason = "no reachable entry"; }
        return false;
    }

    private static bool Sample(Vector3 sample, Vector3 hint, out Vector3 target)
    {
        target = default;
        if (!NavMesh.SamplePosition(sample, out var hit, .75f, NavMesh.AllAreas)
            || Mathf.Abs(hit.position.y - hint.y) > .6f || Vector3.Distance(hit.position, hint) > 1.25f) return false;
        target = hit.position; return true;
    }
    internal static bool Reached(Vector3 origin, Vector3 target)
        => Mathf.Abs(origin.y - target.y) <= .8f && (origin - target).sqrMagnitude <= 4f;

    internal static bool LocalPath(Vector3 origin, Vector3 target, Vector3[] corners)
    {
        if (corners == null || corners.Length == 0) return false;
        var length = 0f;
        var lower = Mathf.Min(origin.y, target.y) - 1.25f;
        var upper = Mathf.Max(origin.y, target.y) + 1.25f;
        for (var i = 0; i < corners.Length; i++)
        {
            if (corners[i].y < lower || corners[i].y > upper) return false;
            length += Vector3.Distance(i == 0 ? origin : corners[i - 1], corners[i]);
        }
        return length <= Vector3.Distance(origin, target) * 2f + 6f;
    }
}
