using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Orbit.Helpers;

internal enum TransitionPhase
{
    SleepGroup, WakeGroup, SleepBody, WakeBody, WakeNavigation, DoorSync,
    GuardSchedule, GuardComplete, GhostDeath,
    Count
}

// Opt-in through the existing Performance logging switch. No event strings, allocations or
// stopwatch reads on the disabled path. Nested timings are inclusive, never additive.
internal static class TransitionPerformance
{
    private struct Sample
    {
        internal int Calls, MaxFrame;
        internal long Total, Max;
    }

    private static readonly Sample[] Samples = new Sample[(int)TransitionPhase.Count];
    private static int _generation;

    internal readonly struct Scope : IDisposable
    {
        private readonly long _start;
        private readonly TransitionPhase _phase;
        private readonly int _generation, _frame;
        internal Scope(TransitionPhase phase)
        {
            _phase = phase;
            _generation = TransitionPerformance._generation;
            _frame = Time.frameCount;
            _start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_start == 0 || _generation != TransitionPerformance._generation) return;
            var elapsed = Stopwatch.GetTimestamp() - _start;
            ref var sample = ref Samples[(int)_phase];
            sample.Calls++;
            sample.Total += elapsed;
            if (sample.Calls == 1 || elapsed > sample.Max) { sample.Max = elapsed; sample.MaxFrame = _frame; }
        }
    }

    internal static Scope Measure(TransitionPhase phase)
        => Plugin.PerfLogging is { Value: true } ? new Scope(phase) : default;

    internal static void Reset()
    {
        Array.Clear(Samples, 0, Samples.Length);
        _generation++;
    }

    internal static void Flush()
    {
        if (Plugin.PerfLogging is not { Value: true }) { Reset(); return; }
        StringBuilder line = null;
        for (var i = 0; i < Samples.Length; i++)
        {
            var sample = Samples[i];
            if (sample.Calls == 0) continue;
            line ??= new StringBuilder("PERF TRANSITIONS: inclusive=true format=count/totalMs/maxMs@frame");
            line.Append(' ').Append((TransitionPhase)i).Append('=').Append(sample.Calls).Append('/')
                .Append((sample.Total * 1000d / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture)).Append('/')
                .Append((sample.Max * 1000d / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture))
                .Append('@').Append(sample.MaxFrame);
        }
        if (line != null) Log.Always(line.ToString());
        Reset();
    }
}
