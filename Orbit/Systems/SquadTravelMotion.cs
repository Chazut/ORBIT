using System;
using UnityEngine;

namespace Orbit.Systems;

// Per-body state: no shared random stream, path jobs or ownership of a task's look target.
internal sealed class SquadTravelMotion
{
    internal int PreparedFrame = -1, Revision = -1, Corner = -1, SquadId = -1;
    internal int Slot, Count, Cycle;
    internal bool CanScan, HasLane, Initialized;
    internal float NextProbe, NextScan, ScanStart, ScanEnd, ScanYaw, NextReport;
    internal float LaneWidth;
    internal Vector3 LaneTarget, Forward;
    private float _phase;

    internal void Initialize(int agentId, int squadId, int slot, int count, float now)
    {
        if (Initialized && SquadId == squadId && Slot == slot && Count == count) return;
        Suspend();
        Initialized = true;
        SquadId = squadId; Slot = slot; Count = count; Cycle = 0;
        uint seed = unchecked((uint)(agentId * 73856093) ^ (uint)(squadId * 19349663));
        _phase = (seed % 997) / 997f;
        NextScan = now + 1f + slot * .8f + _phase;
        NextProbe = now + _phase * .2f;
    }

    internal void Suspend()
    {
        PreparedFrame = -1; CanScan = HasLane = Initialized = false;
        ScanStart = ScanEnd = ScanYaw = LaneWidth = 0;
        Revision = Corner = -1;
    }

    internal float DesiredWidth(float now, bool indoors, float cornerDistance, float targetDistance)
    {
        // Keep a side for the whole route, with slow small variations instead of zigzags.
        float side = Slot == 0 ? 1f : (Slot % 2 == 1 ? -1f : 1f);
        float width = Slot == 0 ? .35f : Math.Min(3.8f, 2.1f + (Slot - 1) / 2 * .7f);
        width += Mathf.Sin(now * .18f + _phase * 6.283185f) * (Slot == 0 ? .2f : .4f);
        if (indoors) width *= .3f;
        return side * width * Taper(cornerDistance, 3f, 9f) * Taper(targetDistance, 6f, 14f);
    }

    internal float WatchYaw(float now, bool allowed, out bool started)
    {
        started = false;
        if (!allowed)
        {
            ScanEnd = 0;
            NextScan = Math.Max(NextScan, now + 1f + _phase);
            return 0;
        }
        if (now >= ScanEnd && now >= NextScan)
        {
            Cycle++;
            // The point man keeps the forward sector. Followers alternate flanks and rear checks.
            int sector = Slot == 0 ? 0 : (Slot + Cycle - 2) % 3 + 1;
            ScanYaw = sector switch
            {
                1 => -75f - _phase * 15f,
                2 => 75f + _phase * 15f,
                3 => (Slot % 2 == 1 ? -1f : 1f) * (150f + _phase * 15f),
                _ => (Cycle % 2 == 0 ? -1f : 1f) * (25f + _phase * 15f)
            };
            ScanStart = now;
            ScanEnd = now + 3.2f + _phase * .8f;
            NextScan = ScanEnd + 4f + _phase * 3f + (Cycle % 3) * .5f;
            started = true;
        }
        if (now >= ScanEnd) return 0;
        float envelope = Taper(now - ScanStart, 0, 1.1f) * Taper(ScanEnd - now, 0, 1.1f);
        return ScanYaw * envelope;
    }

    private static float Taper(float value, float min, float max)
    {
        float t = Mathf.Clamp01((value - min) / (max - min));
        return t * t * (3f - 2f * t);
    }
}
