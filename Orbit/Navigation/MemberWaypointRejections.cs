using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Navigation;

// Per member, independent of loot value and consumption. A different approach or
// a later retry can succeed, so an access failure must not blacklist the loot forever.
public sealed class MemberWaypointRejections
{
    private const float RetryCooldown = 15f;
    private const float MinimumRetryDelay = 3f;
    private readonly Dictionary<int, (Vector3 origin, float until, bool door)> entries = new();
    private static bool Active((Vector3 origin, float until, bool door) e, Vector3 origin, float now)
        => now < e.until && (now < e.until - RetryCooldown + MinimumRetryDelay || (origin - e.origin).sqrMagnitude < 25f);
    private readonly List<int> expired = new();
    public bool Contains(int id, Vector3 origin, float now)
    {
        if (!entries.TryGetValue(id, out var e)) return false;
        // A moving ghost cannot retry the same failed anchor on consecutive ticks.
        // Allow a changed approach after three seconds, or the normal 15s expiry.
        if (Active(e, origin, now)) return true;
        entries.Remove(id); return false;
    }
    public void DoorOpened(int id){if(entries.TryGetValue(id,out var e)&&e.door)entries.Remove(id);}
    public bool Reject(int id, Vector3 origin, float now, bool door=false)
    {
        if (Contains(id, origin, now)) return false;
        if (entries.Count >= 64)
        {
            int oldest = 0; float until = float.MaxValue;
            foreach (var e in entries) if (e.Value.until < until) { oldest = e.Key; until = e.Value.until; }
            entries.Remove(oldest);
        }
        entries[id] = (origin, now + RetryCooldown, door); return true;
    }
    public void AddTo(HashSet<int> excluded, Vector3 origin, float now)
    {
        expired.Clear();
        foreach (var e in entries)
            if (Active(e.Value, origin, now)) excluded.Add(e.Key);
            else expired.Add(e.Key);
        foreach (int id in expired) entries.Remove(id);
    }
}
