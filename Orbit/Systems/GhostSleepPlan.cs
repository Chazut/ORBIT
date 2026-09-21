using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Systems;

// Plan whole groups together, before any proximity wakes. A group that must stay awake also
// blocks neighbouring candidates, including chains across ORBIT and native groups.
internal sealed class GhostSleepPlan
{
    internal sealed class Unit
    {
        internal object Key;
        internal bool Allowed;
        internal readonly List<Vector3> Positions = new();
    }
    private readonly List<Unit> _pool = new();
    private readonly HashSet<object> _members = new();
    private readonly List<Vector3> _blockers = new();
    internal int Count { get; private set; }
    internal Unit this[int index] => _pool[index];

    internal void Clear() { Count = 0; _members.Clear(); _blockers.Clear(); }

    internal Unit Add(object key)
    {
        if (Count == _pool.Count) _pool.Add(new Unit());
        var unit = _pool[Count++];
        unit.Key = key;
        unit.Allowed = true;
        unit.Positions.Clear();
        return unit;
    }

    internal void Member(Unit unit, object bot, Vector3 position)
    { _members.Add(bot); unit.Positions.Add(position); }

    internal void Awake(object bot, Vector3 position)
    { if (bot == null || !_members.Contains(bot)) _blockers.Add(position); }

    internal void Resolve(float distanceSquared)
    {
        for (var b = 0; b < _blockers.Count; b++)
        {
            var position = _blockers[b];
            for (var u = 0; u < Count; u++)
            {
                var unit = _pool[u];
                if (!unit.Allowed) continue;
                for (var p = 0; p < unit.Positions.Count; p++)
                {
                    if ((unit.Positions[p] - position).sqrMagnitude > distanceSquared) continue;
                    unit.Allowed = false;
                    _blockers.AddRange(unit.Positions);
                    break;
                }
            }
        }
    }
}
