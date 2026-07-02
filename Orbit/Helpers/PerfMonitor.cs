using System;
using UnityEngine;

namespace Orbit.Helpers;

/// <summary>
/// Lightweight per-raid performance telemetry. Accumulates frame timings plus a few ORBIT-specific counters
/// and writes a single "PERF:" Info line every 30s — cheap enough to stay always-on, and it makes fps history
/// and fix-specific regressions greppable from any user's LogOutput.log instead of relying on feel.
/// </summary>
public static class PerfMonitor
{
    private const float WindowSeconds = 30f;
    private const float HitchThreshold = 0.050f;
    private const float BigHitchThreshold = 0.100f;

    private static float _windowStart = -1f;
    private static int _frames;
    private static float _sumDt;
    private static float _maxDt;
    private static int _hitches;
    private static int _bigHitches;
    private static int _gc0;

    // Fix-specific counters, bumped at the instrumented sites; reset every window. Post-raid health checks:
    // sweeps submitted == completed + drained (no TempJob leak), islandProbes settles to ~3/window (10s
    // cadence) for a permanently-stuck bot, rallyWp stays low even during long fights, navQpeak stays small.
    public static int SpawnIslandProbes;
    public static int RallyWaypointsCreated;
    public static int SweepJobsSubmitted;
    public static int SweepJobsCompleted;
    public static int SweepJobsDrained;
    public static int NavJobsQueuedPeak;

    /// <summary>Fresh window + counters — called at raid start so stale statics don't bleed across raids.</summary>
    public static void Reset()
    {
        _windowStart = Time.unscaledTime;
        _frames = 0;
        _sumDt = 0f;
        _maxDt = 0f;
        _hitches = 0;
        _bigHitches = 0;
        _gc0 = GC.CollectionCount(0);
        SpawnIslandProbes = 0;
        RallyWaypointsCreated = 0;
        SweepJobsSubmitted = 0;
        SweepJobsCompleted = 0;
        SweepJobsDrained = 0;
        NavJobsQueuedPeak = 0;
    }

    public static void Tick(int agentCount)
    {
        // Toggleable (default OFF). Invalidating the window while disabled makes a mid-raid enable start a
        // clean 30s window instead of flushing a stale one.
        if (Plugin.PerfLogging is not { Value: true })
        {
            _windowStart = -1f;
            return;
        }
        if (_windowStart < 0f) Reset();

        var dt = Time.unscaledDeltaTime;
        _frames++;
        _sumDt += dt;
        if (dt > _maxDt) _maxDt = dt;
        if (dt > BigHitchThreshold) _bigHitches++;
        else if (dt > HitchThreshold) _hitches++;

        if (Time.unscaledTime - _windowStart < WindowSeconds || _sumDt <= 0f) return;

        var avg = _frames / _sumDt;
        var worst = _maxDt > 0f ? 1f / _maxDt : avg;
        var gc0 = GC.CollectionCount(0) - _gc0;
        // Always-level on purpose: two lines a minute, and it's the one thing every perf bug report needs —
        // Quiet logging (default ON) must not silence it.
        Log.Always($"PERF: avg={avg:F0}fps worst={worst:F0}fps hitch50={_hitches} hitch100={_bigHitches} gc0={gc0} agents={agentCount} | islandProbes={SpawnIslandProbes} rallyWp={RallyWaypointsCreated} sweeps={SweepJobsSubmitted}s/{SweepJobsCompleted}c/{SweepJobsDrained}d navQpeak={NavJobsQueuedPeak}");
        Reset();
    }
}
