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

/// <summary>Opt-in, bounded 60-second capture. No per-frame allocations or log writes.</summary>
internal static class DiagnosticCapture
{
    private static readonly Queue<Sample> Samples = new();
    private static readonly Queue<OrbitGhostWake> Wakes = new();
    private static readonly List<Vector3> Humans = new();
    private static string _map;
    private static float _nextSample, _startedAt, _frameSum, _frameMax;
    private static double _orbitSum, _orbitMax;
    private static int _frames, _hitches, _gc, _saved;
    private static bool _enabled;
    private static Task<string> _writer;
    internal static string Status { get; private set; } = "Enable capture during a raid.";

    internal sealed class Sample
    {
        public float RecordedAt, AverageFrameMs, MaxFrameMs;
        public double AverageOrbitUpdateMs, MaxOrbitUpdateMs;
        public int Frames, HitchesOver100Ms, Gc0, AwakeBots, GhostBots, AwakeWithinWakeDistance;
    }

    internal static void Reset(string map)
    {
        Samples.Clear(); Wakes.Clear(); Humans.Clear();
        _map = map; _enabled = false; _saved = 0;
        Status = "Enable capture during a raid.";
        ClearWindow();
    }

    private static void ClearWindow()
    {
        _nextSample = Time.realtimeSinceStartup + 1f;
        _frames = _hitches = 0; _frameSum = _frameMax = 0f; _orbitSum = _orbitMax = 0;
        _gc = GC.CollectionCount(0);
    }

    internal static long BeginFrame()
    {
        RefreshSaveStatus();
        var enabled = Plugin.DiagnosticsEnabled is { Value: true } && _map != null;
        if (enabled != _enabled)
        {
            _enabled = enabled;
            Samples.Clear(); Wakes.Clear(); ClearWindow();
            _startedAt = Time.realtimeSinceStartup;
            Status = enabled ? "Capturing the latest 60 seconds." : "Capture disabled.";
        }
        return enabled ? Stopwatch.GetTimestamp() : 0;
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
        _orbitSum += elapsed; _orbitMax = Math.Max(_orbitMax, elapsed);
        var frameMs = Time.unscaledDeltaTime * 1000f;
        _frames++; _frameSum += frameMs; _frameMax = Math.Max(_frameMax, frameMs);
        if (frameMs > 100f) _hitches++;
        var now = Time.realtimeSinceStartup;
        if (now < _nextSample) return;
        var sample = new Sample
        {
            RecordedAt = now, Frames = _frames, AverageFrameMs = _frameSum / _frames, MaxFrameMs = _frameMax,
            AverageOrbitUpdateMs = _orbitSum / _frames, MaxOrbitUpdateMs = _orbitMax,
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
        Samples.Enqueue(sample);
        Prune(now);
        ClearWindow();
    }

    internal static void Wake(OrbitGhostWake wake)
    {
        if (!_enabled) return;
        if (Wakes.Count >= 1024) Wakes.Dequeue();
        Wakes.Enqueue(wake);
        Prune(wake.RecordedAt);
    }

    private static void Prune(float now)
    {
        while (Samples.Count > 60 || Samples.Count > 0 && Samples.Peek().RecordedAt < now - 60f) Samples.Dequeue();
        while (Wakes.Count > 0 && Wakes.Peek().RecordedAt < now - 60f) Wakes.Dequeue();
    }

    internal static void Save()
    {
        if (!_enabled || Samples.Count == 0) { Status = "Enable capture and wait at least one second in a raid."; return; }
        if (_writer != null && !_writer.IsCompleted) { Status = "A capture is already being saved."; return; }
        if (_saved >= 10) { Status = "Capture limit reached (10 files per raid)."; return; }
        Prune(Time.realtimeSinceStartup);
        var data = new
        {
            Schema = 1, Map = _map, CapturedUtc = DateTime.UtcNow, CaptureStartedAt = _startedAt,
            Notes = "Times use Unity realtime seconds. Samples summarize the preceding interval; bot counts are sampled at its end. ORBIT timing covers OrbitManager.Update only, not every patch or the rest of the game. Correlation does not establish the cause of a hitch.",
            WakeDistance = ServerConfig.GhostMode.WakeDistance,
            SleepDistance = ServerConfig.GhostMode.SleepDistance,
            HostileWakeDistance = ServerConfig.GhostMode.HostileWakeDistance,
            GhostAwakeBehavior = ServerConfig.GhostMode.GhostAwakeBehavior,
            Samples = Samples.ToArray(), Wakes = Wakes.ToArray(),
        };
        var folder = Path.Combine(BepInEx.Paths.BepInExRootPath, "ORBIT", "diagnostics");
        var path = Path.Combine(folder, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
        _saved++;
        Status = "Saving diagnostic capture...";
        // Snapshot contains only detached values. Never read Unity objects from the writer thread.
        _writer = Task.Run(() =>
        {
            try { Directory.CreateDirectory(folder); File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented)); return "ORBIT diagnostics saved: " + path; }
            catch (Exception ex) { return "ORBIT diagnostics could not be saved: " + ex.Message; }
        });
    }

    internal static void Finish()
    {
        _map = null; _enabled = false;
        Samples.Clear(); Wakes.Clear(); Humans.Clear();
    }
}
