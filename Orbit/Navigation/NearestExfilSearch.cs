using System;
using System.Collections.Generic;

namespace Orbit.Navigation;

// Spawn eligibility is a hard constraint. An incomplete route only changes how we
// approach an allowed exit, never which spawn-side restrictions apply.
internal sealed class NearestExfilSearch
{
    internal readonly struct Route(bool complete, float gapSqr, bool available = true)
    {
        internal readonly bool Complete = complete;
        internal readonly float GapSqr = gapSqr;
        internal readonly bool Available = available;
    }

    private struct Candidate
    {
        internal Waypoint Point;
        internal int Order;
        internal float DistanceSqr;
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
    private int _cursor, _bestOrder;
    private float _bestGap;
    private Waypoint _partial, _unresolved;
    internal Waypoint Result { get; private set; }
    internal bool Partial { get; private set; }
    internal void Clear() => _candidates.Clear();
    internal void Add(Waypoint point, float distanceSqr, bool entryEligible)
    {
        if (entryEligible) _candidates.Add(new Candidate
            { Point = point, DistanceSqr = distanceSqr, Order = _candidates.Count });
    }

    internal void Begin()
    {
        _candidates.Sort(ByDistance.Instance);
        _cursor = 0; _bestGap = float.MaxValue; _bestOrder = int.MaxValue;
        _partial = _unresolved = Result = null; Partial = false;
    }

    // True means pending. At most one route query per step, including failed routes.
    internal bool Step(Func<Waypoint, Route> query)
    {
        if (_cursor >= _candidates.Count)
        {
            Result = _partial ?? _unresolved;
            Partial = Result != null;
            return false;
        }
        var candidate = _candidates[_cursor++];
        var route = query(candidate.Point);
        if (!route.Available) return true;
        if (route.Complete)
        { Result = candidate.Point; return false; }
        // A long invalid query may still have useful local approach legs.
        _unresolved ??= candidate.Point;
        if (route.GapSqr < _bestGap || (_partial != null && route.GapSqr == _bestGap && candidate.Order < _bestOrder))
        { _bestGap = route.GapSqr; _bestOrder = candidate.Order; _partial = candidate.Point; }
        return true;
    }
}
