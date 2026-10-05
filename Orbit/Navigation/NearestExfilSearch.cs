using System;
using System.Collections.Generic;

namespace Orbit.Navigation;

// Resumable search. Entry-side eligibility has priority over distance;
// a complete route has priority over a partial route. Equal distances/gaps keep original cell order.
internal sealed class NearestExfilSearch
{
    internal readonly struct Route(bool complete, float gapSqr)
    {
        internal readonly bool Complete = complete;
        internal readonly float GapSqr = gapSqr;
    }

    private struct Candidate
    {
        internal Waypoint Point;
        internal int Order;
        internal float DistanceSqr;
        internal bool EntryEligible, Queried;
        internal Route Route;
    }

    private sealed class ByDistance : IComparer<Candidate>
    {
        internal static readonly ByDistance Instance = new();
        public int Compare(Candidate a, Candidate b)
        {
            var distance = a.DistanceSqr.CompareTo(b.DistanceSqr);
            return distance != 0 ? distance : a.Order.CompareTo(b.Order);
        }
    }

    private readonly List<Candidate> _candidates = new(16);
    private int _cursor, _pass, _bestOrder;
    private float _bestGap;
    private Waypoint _partialEntry, _partialPass;
    internal Waypoint Result { get; private set; }
    internal bool Fallback { get; private set; }
    internal bool Partial { get; private set; }
    internal void Clear() => _candidates.Clear();
    internal void Add(Waypoint point, float distanceSqr, bool entryEligible)
        => _candidates.Add(new Candidate { Point = point, DistanceSqr = distanceSqr,
            EntryEligible = entryEligible, Order = _candidates.Count });

    internal Waypoint Find(Func<Waypoint, Route> query, out bool fallback, out bool partial)
    {
        Begin();
        while (Step(query)) { }
        fallback = Fallback; partial = Partial;
        return Result;
    }

    internal void Begin()
    {
        _candidates.Sort(ByDistance.Instance);
        _cursor = _pass = 0; _bestGap = float.MaxValue; _bestOrder = int.MaxValue;
        _partialEntry = _partialPass = Result = null; Fallback = Partial = false;
    }

    // True means pending, never a failed search. At most one new route query per step.
    internal bool Step(Func<Waypoint, Route> query)
    {
        while (_pass < 2)
        {
            if (_cursor >= _candidates.Count)
            {
                if (_pass++ == 0)
                { _partialEntry = _partialPass; _partialPass = null; _cursor = 0; _bestGap = float.MaxValue; _bestOrder = int.MaxValue; continue; }
                Result = _partialEntry ?? _partialPass; Partial = Result != null;
                return false;
            }
            var i = _cursor++;
            var candidate = _candidates[i];
            if (_pass == 0 && !candidate.EntryEligible) continue;
            var queried = !candidate.Queried;
            if (!candidate.Queried)
            {
                candidate.Route = query(candidate.Point);
                candidate.Queried = true;
                _candidates[i] = candidate;
            }
            // All remaining candidates are farther away. Their paths cannot improve this result.
            if (candidate.Route.Complete && candidate.DistanceSqr < float.MaxValue)
            { Result = candidate.Point; Fallback = _pass == 1; return false; }
            if (!candidate.Route.Complete)
            {
                var gap = candidate.Route.GapSqr;
                if (gap < _bestGap || (_partialPass != null && gap == _bestGap && candidate.Order < _bestOrder))
                { _bestGap = gap; _bestOrder = candidate.Order; _partialPass = candidate.Point; }
            }
            if (queried) return true;
        }
        return false;
    }
}
