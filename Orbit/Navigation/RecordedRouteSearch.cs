using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Navigation;

// Join the nearest recorded transit point, then advance monotonically along that route.
// Search failures are resumable; callers keep the objective and retry after a short delay.
internal sealed class RecordedRouteSearch
{
    private readonly OperationRouteSearch _search = new();
    private RecordedRoute _route;
    private string _map, _key;
    private int _cursor, _failures;
    private Vector3 _lastOrigin;
    private readonly List<Vector3> _history = new();
    private Vector3 _arrival;
    private bool _hasArrival;
    internal bool Active { get; private set; }
    internal bool Pending => _search.Pending;
    internal Vector3[] RouteCorners => _search.RouteCorners;
    internal Vector3 Target => _route == null ? default : _route.Points[_cursor];
    internal string Key => _key;
    internal int Cursor => _cursor;

    internal void Reset()
    { Active = false; _route = null; _map = _key = null; _cursor = _failures = 0; _hasArrival = false; _history.Clear(); _search.Reset(); }
    internal void ResetSearch() => _search.Reset();
    internal bool Enable(string map, string key, Vector3 origin)
    {
        if (Active && _map == map && _key == key) return true;
        if (!RecordedRouteLibrary.Has(map, key)) return false;
        Reset(); _map = map; _key = key; Active = true; Join(origin);
        return true;
    }
    private void Join(Vector3 origin, bool alternate = false)
    {
        var best = float.MaxValue;
        RecordedRoute chosen = null;
        var cursor = 0;
        foreach (var route in RecordedRouteLibrary.Routes)
        {
            if (!RecordedRouteLibrary.Matches(route, _map, _key)) continue;
            if (alternate && route == _route) continue;
            for (var i = 0; i < route.Points.Length; i++)
            {
                var delta = route.Points[i] - origin;
                var score = delta.sqrMagnitude + Mathf.Abs(delta.y) * 12f;
                if (score >= best) continue;
                best = score; chosen = route; cursor = i;
            }
        }
        if (chosen != null) { _route = chosen; _cursor = cursor; _hasArrival = false; _history.Clear(); }
        _lastOrigin = origin; _search.Reset();
    }
    internal bool Find(Vector3 origin, out Vector3 point, Func<Vector3, bool> allowed = null)
    {
        point = default;
        if (!Active || _route == null) return false;
        // A combat relocation can move us to another part of the route. Never restart at its beginning.
        if ((origin - _lastOrigin).sqrMagnitude > 400f && !_search.Pending) Join(origin);
        _lastOrigin = origin;
        var points = _route.Points;
        if (!_search.Pending)
        {
            var previous = _cursor;
            // A native mesh sample can be beside the recorded footsteps. Reaching that
            // verified endpoint finishes the leg even when it is outside the hint radius.
            if (_hasArrival && _cursor + 1 < points.Length && Mathf.Abs(origin.y - _arrival.y) < 1.25f
                && (origin - _arrival).sqrMagnitude < 6.25f) { _cursor++; _hasArrival = false; }
            while (_cursor + 1 < points.Length && Mathf.Abs(origin.y - points[_cursor].y) < 1.25f
                && (origin - points[_cursor]).sqrMagnitude < 6.25f) _cursor++;
            if (previous != _cursor) { _hasArrival = false; _history.Clear(); }
            OperationRouteSearch.Remember(_history, origin, 2f);
        }
        if (_search.Find(origin, points[_cursor], out point, out var final, Mathf.Min(2, _failures), _history,
            floorAware: true, finalHeightTolerance: 1.25f, allowedPoint: allowed))
        { _failures = 0; _hasArrival = final; _arrival = point; return true; }
        if (!_search.Pending)
        {
            _failures++; _search.Reset();
            if (_failures % 3 == 0) Join(origin, alternate: true);
        }
        return false;
    }
}
