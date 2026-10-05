using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Orbit.Helpers;

internal enum TransitionPhase
{
    SleepGroup, WakeGroup, SleepBody, WakeBody, WakeNavigation, DoorSync,
    GuardSchedule, GuardComplete, GhostDeath, WakeQueue,
    UpdatePurge, UpdateLanding, UpdateTelemetry, UpdateStrategy, UpdateActions,
    UpdateExtract, UpdateDormancy, UpdateMovement, UpdateLook, UpdateWaypoints, UpdateNavigation,
    GhostDeathPrepare, GhostDeathRestore, GhostDeathDamage, AgentRemove,
    GhostActivation, GhostSpectator, GhostShots, GhostAudioPlay, GhostAudioTail, GhostFights, GhostNativeMove,
    GhostScan, GhostScope, GhostAdmission, GhostContacts, GhostHearing, GhostHealing,
    GhostDeathInfo, GhostDeathChest, GhostDeathHead, GhostDeathFallback,
    GhostHealthDamage, GhostHealthKill, GhostOnDead, GhostAggressor, GhostCorpse, GhostCorpseImpulse,
    GhostDeathSound, GhostPlayerDeadCallbacks, GhostGlobalDeadCallbacks, GhostUnspawnCallbacks,
    GhostIPlayerUnspawnCallbacks, GhostExfilCallback, GhostInteractionCallback, GhostCorpseRegistration,
    StrategyScores, StrategyPick, StrategyTasks, StrategySquad,
    StrategyObjectives, StrategyDispatch, StrategySelection, StrategyReachability,
    HearingCategory, HearingTypeLookup, HearingCompile, HearingComponentRead, HearingNative, HearingAvailability, HearingInvestigate, HearingTelemetry, HearingSource,
    OptionalTypeLookup, NativeBindings, ExfilSearch, ExfilPath, WaypointSearch, WaypointPath,
    SpawnEntryScan, SpawnEntryResolve, ExfilEligibility, ExfilDiagnostics,
    MovementDoorWatch, MovementPendingDoors, MovementJobs, MovementJob, MovementAgent,
    MovementRecovery, MovementIdleRescue, MovementSpawnRescue, MovementGhost, MovementGhostDoors, MovementAwake,
    TravelPlanning, TravelExposure, TravelCoverScan, TravelPath, TravelGait, SniperPlanning,
    GhostDeathDiagnosticReport,
    CampSearch, GhostWeather,
    LootResume, LootPrepare, LootEvaluate, NativeExtract, NativeExtractLeave, NativeExtractDeactivate,
    NativeExtractDispose, NativeExtractDespawn, GhostPlayerDamage,
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
    private static readonly Sample[] CaptureSamples = new Sample[(int)TransitionPhase.Count];
    private static readonly Sample[] FrameSamples = new Sample[(int)TransitionPhase.Count];
    private static readonly Sample[] PeakUpdateSamples = new Sample[(int)TransitionPhase.Count];
    private static readonly string[] PhaseNames = Enum.GetNames(typeof(TransitionPhase));
    private static int _generation;
    private static int _captureGeneration;
    private static bool _captureFrameActive;
    private static double _peakUpdateMs;

    internal sealed class PhaseSample
    {
        public string Phase;
        public int Calls, PeakFrame;
        public double TotalMs, MaxMs;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly long _start;
        private readonly TransitionPhase _phase;
        private readonly int _generation, _frame, _captureGeneration;
        internal Scope(TransitionPhase phase)
        {
            _phase = phase;
            _generation = TransitionPerformance._generation;
            _captureGeneration = _captureFrameActive ? TransitionPerformance._captureGeneration : -1;
            _frame = Time.frameCount;
            _start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_start == 0) return;
            var elapsed = Stopwatch.GetTimestamp() - _start;
            if (_generation == TransitionPerformance._generation && _frame == Time.frameCount)
                PerformanceJournal.RecordPhase(_phase, elapsed, _frame);
            if (_generation == TransitionPerformance._generation)
                Record(Samples, _phase, elapsed, _frame);
            // The periodic log flush can reset its window during UpdateTelemetry. Capture windows
            // have a separate lifetime, so that work still belongs to the current Update.
            if (_captureFrameActive && _captureGeneration == TransitionPerformance._captureGeneration
                && _frame == Time.frameCount)
            {
                Record(CaptureSamples, _phase, elapsed, _frame);
                Record(FrameSamples, _phase, elapsed, _frame);
            }
        }
    }

    private static void Record(Sample[] samples, TransitionPhase phase, long elapsed, int frame)
    {
        ref var sample = ref samples[(int)phase];
        sample.Calls++;
        sample.Total += elapsed;
        if (sample.Calls == 1 || elapsed > sample.Max) { sample.Max = elapsed; sample.MaxFrame = frame; }
    }

    internal static Scope Measure(TransitionPhase phase)
        => Plugin.PerfLogging is { Value: true } ? new Scope(phase) : default;

    // Engine activation runs outside OrbitManager.Update. Include it in the log summary only;
    // its detached capture events carry explicit frames without polluting Update-only snapshots.
    internal static void RecordExternal(TransitionPhase phase, long ticks, int frame)
    {
        if (Plugin.PerfLogging is not { Value: true }) return;
        Record(Samples, phase, ticks, frame);
        PerformanceJournal.RecordPhase(phase, ticks, frame, external: true);
    }

    internal static void BeginCaptureFrame()
    {
        Array.Clear(FrameSamples, 0, FrameSamples.Length);
        _captureGeneration++;
        _captureFrameActive = true;
    }

    internal static void EndCaptureFrame(double updateMs)
    {
        if (!_captureFrameActive) return;
        _captureFrameActive = false;
        if (updateMs <= _peakUpdateMs) return;
        _peakUpdateMs = updateMs;
        Array.Copy(FrameSamples, PeakUpdateSamples, FrameSamples.Length);
    }

    internal static void ResetCaptureWindow()
    {
        _captureFrameActive = false;
        _captureGeneration++;
        _peakUpdateMs = 0;
        Array.Clear(CaptureSamples, 0, CaptureSamples.Length);
        Array.Clear(FrameSamples, 0, FrameSamples.Length);
        Array.Clear(PeakUpdateSamples, 0, PeakUpdateSamples.Length);
    }

    internal static PhaseSample[] SnapshotInterval() => Snapshot(CaptureSamples);
    internal static PhaseSample[] SnapshotPeakUpdate() => Snapshot(PeakUpdateSamples);

    // Allocate detached values only when taking the existing one-second sample, never per bot/frame.
    private static PhaseSample[] Snapshot(Sample[] samples)
    {
        var count = 0;
        for (var i = 0; i < samples.Length; i++) if (samples[i].Calls > 0) count++;
        if (count == 0) return Array.Empty<PhaseSample>();
        var result = new PhaseSample[count];
        var index = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            if (sample.Calls == 0) continue;
            result[index++] = new PhaseSample
            {
                Phase = PhaseNames[i], Calls = sample.Calls, PeakFrame = sample.MaxFrame,
                TotalMs = sample.Total * 1000d / Stopwatch.Frequency,
                MaxMs = sample.Max * 1000d / Stopwatch.Frequency,
            };
        }
        return result;
    }

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
        // A scope can span this flush (UpdateTelemetry). Let it finish in the next log window;
        // only an explicit lifecycle reset invalidates outstanding scopes.
        Array.Clear(Samples, 0, Samples.Length);
    }
}
