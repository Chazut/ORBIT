using System.Collections.Generic;

namespace Orbit.Navigation;

// Remember each destination across alternating splinters, without retaining a raid's entire POI list.
public sealed class ArrivalFailureHistory
{
    private const int Capacity = 32;
    private const float LifetimeSeconds = 300f;
    private readonly Dictionary<int, Entry> _entries = new();

    private struct Entry
    {
        public int Strikes;
        public float Distance;
        public float Time;
    }

    public int Record(int id, float distance, float now, out float progress)
    {
        var known = _entries.TryGetValue(id, out var entry) && now - entry.Time <= LifetimeSeconds;
        progress = known ? entry.Distance - distance : 0f;
        var strikes = known && progress < 20f ? entry.Strikes + 1 : 1;
        if (!_entries.ContainsKey(id) && _entries.Count >= Capacity)
        {
            var oldestId = -1;
            var oldestTime = float.MaxValue;
            foreach (var pair in _entries)
                if (pair.Value.Time < oldestTime) { oldestId = pair.Key; oldestTime = pair.Value.Time; }
            _entries.Remove(oldestId);
        }
        _entries[id] = new Entry { Strikes = strikes, Distance = distance, Time = now };
        return strikes;
    }

    public void Forget(int id) => _entries.Remove(id);
}
