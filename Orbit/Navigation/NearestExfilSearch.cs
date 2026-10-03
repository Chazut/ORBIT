using System;
using System.Collections.Generic;

namespace Orbit.Navigation;

// Reusable scratch for one synchronous search. Entry-side eligibility has priority over distance;
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
    internal void Clear() => _candidates.Clear();
    internal void Add(Waypoint point, float distanceSqr, bool entryEligible)
        => _candidates.Add(new Candidate { Point = point, DistanceSqr = distanceSqr,
            EntryEligible = entryEligible, Order = _candidates.Count });

    internal Waypoint Find(Func<Waypoint, Route> query, out bool fallback, out bool partial)
    {
        _candidates.Sort(ByDistance.Instance);
        var entry = Scan(query, false, out var partialEntry);
        fallback = false; partial = false;
        if (entry != null) return entry;
        var any = Scan(query, true, out var partialAny);
        if (any != null) { fallback = true; return any; }
        var best = partialEntry ?? partialAny;
        partial = best != null;
        return best;
    }

    private Waypoint Scan(Func<Waypoint, Route> query, bool ignoreEntry, out Waypoint partial)
    {
        partial = null;
        var bestGap = float.MaxValue;
        var bestOrder = int.MaxValue;
        for (var i = 0; i < _candidates.Count; i++)
        {
            var candidate = _candidates[i];
            if (!ignoreEntry && !candidate.EntryEligible) continue;
            if (!candidate.Queried)
            {
                candidate.Route = query(candidate.Point);
                candidate.Queried = true;
                _candidates[i] = candidate;
            }
            // All remaining candidates are farther away. Their paths cannot improve this result.
            if (candidate.Route.Complete && candidate.DistanceSqr < float.MaxValue) return candidate.Point;
            if (candidate.Route.Complete) continue;
            var gap = candidate.Route.GapSqr;
            if (gap < bestGap || (partial != null && gap == bestGap && candidate.Order < bestOrder))
            {
                bestGap = gap; bestOrder = candidate.Order; partial = candidate.Point;
            }
        }
        return null;
    }
}
