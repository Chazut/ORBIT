using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using Orbit.Api;
using Orbit.Systems;
using UnityEngine;

namespace Orbit.Helpers;

/// <summary>Performance logging also captures bounded context around slow frames, without a manual export.</summary>
internal static class DiagnosticCapture
{
    private const float TriggerFrameMs = 250f, StartupGraceSeconds = 10f;
    private const float ContextAfterSeconds = 5f, CooldownSeconds = 60f;
    private const int MaxCapturesPerRaid = 10;
    private static readonly Queue<Sample> Samples = new();
    private static readonly Queue<OrbitGhostWake> Wakes = new();
    private static readonly Queue<WakeDecisionSample> WakeDecisions = new();
    private static readonly Queue<WakeActivationSample> WakeActivations = new();
    private static readonly List<Vector3> Humans = new();
    private static string _map;
    private static float _nextSample, _startedAt, _frameSum, _frameMax;
    private static double _orbitSum, _orbitMax;
    private static int _frames, _hitches, _gc, _saved;
    private static int _firstUpdateFrame, _maxFrameObservedAtFrame, _maxOrbitUpdateFrame;
    private static float _maxOrbitUpdateRecordedAt;
    private static bool _enabled, _pending;
    private static float _nextCaptureAt, _saveAt, _triggerAt, _triggerFrameMs;
    private static Task<string> _writer;
    internal static bool IsRecording => _enabled && Plugin.PerfLogging is { Value: true };
    internal static string Status { get; private set; } = "Performance logging is disabled.";

    internal sealed class Sample
    {
        public float RecordedAt, AverageFrameMs, MaxFrameMs;
        public double AverageOrbitUpdateMs, MaxOrbitUpdateMs;
        public int Frames, HitchesOver100Ms, Gc0, AwakeBots, GhostBots, AwakeWithinWakeDistance;
        public int FirstUpdateFrame, LastUpdateFrame, MaxFrameObservedAtFrame, MaxOrbitUpdateFrame;
        public float MaxOrbitUpdateRecordedAt;
        public TransitionPerformance.PhaseSample[] PhaseTimings, SlowestOrbitUpdatePhases;
    }

    internal sealed class WakeDecisionSample
    {
        public string Mode, Cause, Detail, ProfileId;
        public int Frame, PendingGroups;
        public float RecordedAt, WaitedMs;
    }

    internal sealed class WakeActivationSample
    {
        public string ProfileId, State;
        public int Calls, PeakFrame, CompletedFrame;
        public float RecordedAt;
        public double TotalMs, MaxMs, LatencyMs;
    }

    internal static void WakeActivation(string profileId, string state, int calls, double totalMs, double maxMs, int peakFrame, double latencyMs)
    {
        if (!IsRecording) return;
        if (WakeActivations.Count >= 256) WakeActivations.Dequeue();
        WakeActivations.Enqueue(new WakeActivationSample { ProfileId = profileId, State = state, Calls = calls,
            TotalMs = totalMs, MaxMs = maxMs, PeakFrame = peakFrame, LatencyMs = latencyMs,
            RecordedAt = Time.realtimeSinceStartup, CompletedFrame = Time.frameCount });
        Log.Always(FormattableString.Invariant($"PERF WAKE READY: member={profileId} state={state} calls={calls} totalMs={totalMs:F3} maxMs={maxMs:F3} peakFrame={peakFrame} latencyMs={latencyMs:F1}"));
    }

    internal static void WakeDecision(string mode, string cause, string detail, string profileId, int pendingGroups, float waitedMs)
    {
        if (!IsRecording) return;
        var sample = new WakeDecisionSample { Mode = mode, Cause = cause, Detail = detail,
            ProfileId = profileId, Frame = Time.frameCount, RecordedAt = Time.realtimeSinceStartup,
            PendingGroups = pendingGroups, WaitedMs = waitedMs };
        if (WakeDecisions.Count >= 512) WakeDecisions.Dequeue();
        WakeDecisions.Enqueue(sample);
        Log.Always(FormattableString.Invariant($"PERF WAKE: mode={mode} cause={cause} detail={detail} member={profileId} pending={pendingGroups} waitedMs={waitedMs:F1}"));
    }

    internal static void Reset(string map)
    {
        DebugExport.BeginRaid(map);
        Samples.Clear(); Wakes.Clear(); WakeDecisions.Clear(); WakeActivations.Clear(); Humans.Clear();
        _map = map; _enabled = _pending = false; _saved = 0;
        PerformanceJournal.Reset(map);
        Status = "Waiting for Performance logging during a raid.";
        ClearWindow();
    }

    private static void ClearWindow()
    {
        _nextSample = Time.realtimeSinceStartup + 1f;
        _frames = _hitches = 0; _frameSum = _frameMax = 0f; _orbitSum = _orbitMax = 0;
        _firstUpdateFrame = _maxFrameObservedAtFrame = _maxOrbitUpdateFrame = -1;
        _maxOrbitUpdateRecordedAt = 0;
        TransitionPerformance.ResetCaptureWindow();
        _gc = GC.CollectionCount(0);
    }

    internal static long BeginFrame()
    {
        var bookkeeping = PerformanceJournal.Enabled ? Stopwatch.GetTimestamp() : 0;
        PerformanceJournal.BeginFrame();
        RefreshSaveStatus();
        var enabled = Plugin.PerfLogging is { Value: true } && _map != null;
        if (enabled != _enabled)
        {
            _enabled = enabled;
            Samples.Clear(); Wakes.Clear(); WakeDecisions.Clear(); WakeActivations.Clear(); ClearWindow();
            _startedAt = Time.realtimeSinceStartup;
            _nextCaptureAt = _startedAt + StartupGraceSeconds;
            _pending = false;
            Status = enabled ? "Automatic performance capture enabled." : "Performance logging is disabled.";
        }
        if (!enabled) return 0;
        TransitionPerformance.BeginCaptureFrame();
        PerformanceJournal.Bookkeeping(Stopwatch.GetTimestamp() - bookkeeping);
        return Stopwatch.GetTimestamp();
    }

    internal static void RefreshSaveStatus()
    {
        if (_writer?.IsCompleted == true)
        {
            Status = _writer.Result;
            Log.Always(Status);
            _writer = null;
        }
    }

    internal static void EndFrame(long start)
    {
        if (start == 0) return;
        var elapsed = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
        PerformanceJournal.EndUpdate(elapsed);
        var bookkeeping = Stopwatch.GetTimestamp();
        try
        {
            TransitionPerformance.EndCaptureFrame(elapsed);
            var frame = Time.frameCount;
            if (_frames == 0) _firstUpdateFrame = frame;
            _orbitSum += elapsed;
            if (_frames == 0 || elapsed > _orbitMax)
            {
                _orbitMax = elapsed; _maxOrbitUpdateFrame = frame;
                _maxOrbitUpdateRecordedAt = Time.realtimeSinceStartup;
            }
            var frameMs = Time.unscaledDeltaTime * 1000f;
            if (_frames == 0 || frameMs > _frameMax) { _frameMax = frameMs; _maxFrameObservedAtFrame = frame; }
            _frames++; _frameSum += frameMs;
            if (frameMs > 100f) _hitches++;
            var now = Time.realtimeSinceStartup;
            if (!_pending && _saved < MaxCapturesPerRaid && now >= _nextCaptureAt && frameMs >= TriggerFrameMs)
            {
                _pending = true;
                _triggerAt = now; _triggerFrameMs = frameMs;
                _saveAt = now + ContextAfterSeconds;
                _nextCaptureAt = now + CooldownSeconds;
            }
            if (now < _nextSample) { SavePending(now, frameMs); return; }
            var sample = new Sample
            {
                RecordedAt = now, Frames = _frames, AverageFrameMs = _frameSum / _frames, MaxFrameMs = _frameMax,
                AverageOrbitUpdateMs = _orbitSum / _frames, MaxOrbitUpdateMs = _orbitMax,
                FirstUpdateFrame = _firstUpdateFrame, LastUpdateFrame = frame,
                MaxFrameObservedAtFrame = _maxFrameObservedAtFrame, MaxOrbitUpdateFrame = _maxOrbitUpdateFrame,
                MaxOrbitUpdateRecordedAt = _maxOrbitUpdateRecordedAt,
                PhaseTimings = TransitionPerformance.SnapshotInterval(),
                SlowestOrbitUpdatePhases = TransitionPerformance.SnapshotPeakUpdate(),
                HitchesOver100Ms = _hitches, Gc0 = GC.CollectionCount(0) - _gc,
            };
            var players = Singleton<GameWorld>.Instance?.AllAlivePlayersList;
            if (players != null)
            {
                Humans.Clear();
                foreach (var p in players)
                    if (p != null && !p.IsAI && p.HealthController is { IsAlive: true }) Humans.Add(p.Position);
                var wakeSqr = ServerConfig.GhostMode.WakeDistance * ServerConfig.GhostMode.WakeDistance;
                foreach (var p in players)
                {
                    if (p == null || !p.IsAI || p.HealthController is not { IsAlive: true }) continue;
                    if (DormancySystem.IsDormantProfile(p.ProfileId)) { sample.GhostBots++; continue; }
                    sample.AwakeBots++;
                    foreach (var human in Humans)
                        if ((p.Position - human).sqrMagnitude <= wakeSqr) { sample.AwakeWithinWakeDistance++; break; }
                }
            }
            PerformanceJournal.Population(sample.AwakeBots, sample.GhostBots, sample.AwakeWithinWakeDistance);
            Samples.Enqueue(sample);
            Prune(now);
            ClearWindow();
            SavePending(now, frameMs);
        }
        finally
        {
            PerformanceJournal.Tick();
            PerformanceJournal.Bookkeeping(Stopwatch.GetTimestamp() - bookkeeping);
        }
    }

    private static void SavePending(float now, float frameMs)
    {
        // Collect a little context after the hitch, then prefer a recovered frame for the snapshot.
        // Bound the extra wait so sustained slow frames still produce a useful capture.
        if (_pending && now >= _saveAt && (frameMs < 100f || now >= _saveAt + ContextAfterSeconds)) Save();
    }

    internal static void Wake(OrbitGhostWake wake)
    {
        if (!_enabled) return;
        if (Wakes.Count >= 1024) Wakes.Dequeue();
        Wakes.Enqueue(wake);
        PerformanceJournal.Event("wake", wake.ProfileId, wake.Cause);
        Prune(wake.RecordedAt);
    }

    private static void Prune(float now)
    {
        while (Samples.Count > 60 || Samples.Count > 0 && Samples.Peek().RecordedAt < now - 60f) Samples.Dequeue();
        while (Wakes.Count > 0 && Wakes.Peek().RecordedAt < now - 60f) Wakes.Dequeue();
        while (WakeDecisions.Count > 0 && WakeDecisions.Peek().RecordedAt < now - 60f) WakeDecisions.Dequeue();
        while (WakeActivations.Count > 0 && WakeActivations.Peek().RecordedAt < now - 60f) WakeActivations.Dequeue();
    }

    private static void Save()
    {
        if (!_enabled || !_pending || Plugin.PerfLogging is not { Value: true } || Samples.Count == 0) return;
        if (_writer != null && !_writer.IsCompleted) return;
        if (_saved >= MaxCapturesPerRaid) { _pending = false; return; }
        RefreshSaveStatus();
        Prune(Time.realtimeSinceStartup);
        var data = new
        {
            Schema = 3, Map = _map, CapturedUtc = DateTime.UtcNow, CaptureStartedAt = _startedAt,
            TriggeredAt = _triggerAt, TriggerFrameMs = _triggerFrameMs, SavedAt = Time.realtimeSinceStartup,
            Notes = "Times use Unity realtime seconds. Samples summarize the preceding interval; bot counts are sampled at its end. Frame duration uses Unity's previous-frame delta, observed at MaxFrameObservedAtFrame; current Update timing uses MaxOrbitUpdateFrame and MaxOrbitUpdateRecordedAt. PhaseTimings cover measured calls in the interval. SlowestOrbitUpdatePhases cover only MaxOrbitUpdateFrame. Nested phases and death callback groups are inclusive and must not be added together. Update timing excludes capture bookkeeping and the rest of the game. WakeActivations separately measure the engine's deferred PreActive ticks, outside ORBIT Update: TotalMs/MaxMs measure work; LatencyMs also includes waiting between ticks. Correlation does not establish the cause of a hitch.",
            WakeDistance = ServerConfig.GhostMode.WakeDistance,
            SleepDistance = ServerConfig.GhostMode.SleepDistance,
            HostileWakeDistance = ServerConfig.GhostMode.HostileWakeDistance,
            GhostAwakeBehavior = ServerConfig.GhostMode.GhostAwakeBehavior,
            WakeIntervalMs = ServerConfig.GhostMode.WakeIntervalMs,
            Samples = Samples.ToArray(), Wakes = Wakes.ToArray(), WakeDecisions = WakeDecisions.ToArray(), WakeActivations = WakeActivations.ToArray(),
        };
        var folder = Path.Combine(BepInEx.Paths.BepInExRootPath, "ORBIT", "diagnostics");
        var path = Path.Combine(folder, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        DebugExport.RegisterDiagnostic(path);
        _saved++;
        _pending = false;
        _nextCaptureAt = Time.realtimeSinceStartup + CooldownSeconds;
        Status = "Saving diagnostic capture...";
        Log.Always("ORBIT diagnostics queued: " + path);
        // Snapshot contains only detached values. Never read Unity objects from the writer thread.
        _writer = Task.Run(() =>
        {
            try { Directory.CreateDirectory(folder); File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented)); return "ORBIT diagnostics saved: " + path; }
            catch (Exception ex) { return "ORBIT diagnostics could not be saved: " + ex.Message; }
        });
    }

    internal static void Finish()
    {
        // Preserve a pending hitch if the raid ends before its post-hitch window is complete.
        Save();
        PerformanceJournal.Finish();
        DebugExport.FinishRaid(Task.WhenAll(_writer ?? Task.CompletedTask, PerformanceJournal.PendingWrite));
        _map = null; _enabled = _pending = false;
        Samples.Clear(); Wakes.Clear(); WakeDecisions.Clear(); WakeActivations.Clear(); Humans.Clear();
        TransitionPerformance.ResetCaptureWindow();
    }

    internal static Task PrepareExport()
    {
        Save();
        return Task.WhenAll(_writer ?? Task.CompletedTask, PerformanceJournal.PrepareExport());
    }
}
