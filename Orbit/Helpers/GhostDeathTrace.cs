using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orbit.Helpers;

// Only records the synchronous call tree initiated by ORBIT's lethal Ghost damage.
// Site metadata is created at startup; the hot path uses fixed buffers and value scopes.
internal static class GhostDeathTrace
{
    internal sealed class Call
    {
        public string Site;
        public int Count, FirstObservedInvocation;
        public double TotalMs, SelfMs, MaxMs;
        public long AllocatedBytes;
    }
    internal sealed class Death
    {
        public int GhostOrdinal, PreviousObservedDeaths, DroppedCalls, Gc0, Gc1, Gc2;
        public double DamageContextMs, UntracedMs;
        public long AllocatedBytes;
        public Call[] Calls;
    }
    private sealed class Site
    {
        internal string Name;
        internal int Observed;
    }
    private struct Sample { internal int Count, First; internal long Ticks, Children, Max, Allocated; }
    private struct Frame { internal long Children; }
    private static readonly List<Site> Sites = new();
    private static readonly Sample[] Samples = new Sample[512];
    private static readonly Frame[] Stack = new Frame[128];
    private static readonly int OwnerThread = Environment.CurrentManagedThreadId;
    private static int _depth, _dropped, _ordinal, _observedDeaths;
    private static long _rootChildren;
    private static bool _active;
    internal static int Register(string name)
    {
        var id = Sites.Count; Sites.Add(new Site { Name = name }); return id;
    }
    internal static void ResetRaid() { _ordinal = _observedDeaths = 0; }
    internal static void ObservedDeath() { if (Plugin.PerfLogging is { Value: true }) _observedDeaths++; }

    internal readonly struct Context : IDisposable
    {
        private readonly string _profile;
        private readonly long _start, _allocated;
        private readonly int _deaths, _gc0, _gc1, _gc2;
        internal Context(string profile)
        {
            _profile = profile; _deaths = _observedDeaths;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            Array.Clear(Samples, 0, Samples.Length);
            _depth = _dropped = 0; _rootChildren = 0; _active = true;
            _allocated = GC.GetAllocatedBytesForCurrentThread(); _start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_start == 0) return;
            var ticks = Stopwatch.GetTimestamp() - _start;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            _active = false;
            var gc0 = GC.CollectionCount(0) - _gc0; var gc1 = GC.CollectionCount(1) - _gc1; var gc2 = GC.CollectionCount(2) - _gc2;
            using var overhead = TransitionPerformance.Measure(TransitionPhase.GhostDeathDiagnosticReport);
            var calls = new List<Call>();
            for (var i = 0; i < Math.Min(Sites.Count, Samples.Length); i++)
            {
                var sample = Samples[i]; if (sample.Count == 0) continue;
                calls.Add(new Call { Site = Sites[i].Name, Count = sample.Count, FirstObservedInvocation = sample.First,
                    TotalMs = Ms(sample.Ticks), SelfMs = Ms(Math.Max(0, sample.Ticks - sample.Children)),
                    MaxMs = Ms(sample.Max), AllocatedBytes = sample.Allocated });
            }
            calls.Sort((a, b) => b.SelfMs.CompareTo(a.SelfMs));
            var report = new Death { GhostOrdinal = ++_ordinal, PreviousObservedDeaths = _deaths, DroppedCalls = _dropped,
                DamageContextMs = Ms(ticks), UntracedMs = Ms(Math.Max(0, ticks - _rootChildren)), AllocatedBytes = allocated,
                Gc0 = gc0, Gc1 = gc1, Gc2 = gc2, Calls = calls.ToArray() };
            PerformanceJournal.DeathTrace(_profile, report);
            var top = calls.Count > 0 ? calls[0] : null;
            Log.Always(FormattableString.Invariant($"PERF GHOST DEATH DETAIL: profile={_profile} ordinal={report.GhostOrdinal} priorDeaths={_deaths} damageMs={report.DamageContextMs:F2} untracedMs={report.UntracedMs:F2} allocBytes={allocated} gc={gc0}/{gc1}/{gc2} top={top?.Site} selfMs={top?.SelfMs:F2} firstObserved={top?.FirstObservedInvocation} dropped={_dropped}"));
        }
    }
    internal static Context Begin(string profile) => PerformanceJournal.Enabled && !_active
        && Environment.CurrentManagedThreadId == OwnerThread ? new Context(profile) : default;
    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    internal readonly struct Scope : IDisposable
    {
        private readonly int _site, _level;
        private readonly long _start, _allocated;
        private readonly TransitionPerformance.Scope _phase;
        internal Scope(int site, int phase)
        {
            _site = site; _level = _depth++;
            Stack[_level] = default;
            ref var sample = ref Samples[site];
            if (sample.Count++ == 0) sample.First = Sites[site].Observed;
            _phase = phase >= 0 ? TransitionPerformance.Measure((TransitionPhase)phase) : default;
            _allocated = GC.GetAllocatedBytesForCurrentThread(); _start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_start == 0) return;
            var ticks = Stopwatch.GetTimestamp() - _start;
            ref var sample = ref Samples[_site];
            sample.Ticks += ticks; sample.Children += Stack[_level].Children;
            sample.Max = Math.Max(sample.Max, ticks);
            sample.Allocated += GC.GetAllocatedBytesForCurrentThread() - _allocated;
            _depth = _level;
            if (_level > 0) Stack[_level - 1].Children += ticks; else _rootChildren += ticks;
            _phase.Dispose();
        }
    }
    internal static Scope Enter(int site, int phase)
    {
        if (Plugin.PerfLogging is not { Value: true }) return default;
        if (Environment.CurrentManagedThreadId != OwnerThread) return default;
        Sites[site].Observed++;
        if (!_active) return default;
        if (site >= Samples.Length || _depth >= Stack.Length) { _dropped++; return default; }
        return new Scope(site, phase);
    }
}
