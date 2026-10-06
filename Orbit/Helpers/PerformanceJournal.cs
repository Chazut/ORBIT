using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace Orbit.Helpers;

// A bounded journal independent of the large-capture cooldown. Unity reports the previous
// frame's delta: keep that frame's work, including engine callbacks after ORBIT.Update.
internal static class PerformanceJournal
{
    internal const int TargetFps = 60;
    internal const float FrameBudgetSeconds = 1f / TargetFps;
    private const int MaxHitches = 1024, FlushHitches = 256, MaxEvents = 2048;
    private const float FlushSeconds = 15f;
    internal struct Timing { internal int Calls; internal long Ticks; }
    private sealed class FrameWork
    {
        internal int Frame = -1;
        internal float RecordedAt;
        internal double UpdateMs, ExternalMs, BookkeepingMs;
        internal bool HasUpdate;
        internal readonly Timing[] Phases = new Timing[(int)TransitionPhase.Count];
        internal void Reset(int frame)
        {
            Frame = frame; RecordedAt = Time.realtimeSinceStartup;
            UpdateMs = ExternalMs = BookkeepingMs = 0; HasUpdate = false;
            Array.Clear(Phases, 0, Phases.Length);
        }
    }
    internal sealed class PhaseWork { public string Phase; public int Calls; public double TotalMs; }
    internal sealed class Hitch
    {
        public int Frame, ObservedAtFrame, Gc0, Gc1, Gc2;
        public float ObservedAt, FrameMs;
        public float? RaidSeconds;
        public bool UpdateMatched;
        public double? OrbitUpdateMs, ExternalActivationMs, DiagnosticBookkeepingMs, UnattributedMs;
        // Reuse raw counters on the game thread; JSON expansion happens only in the writer.
        [JsonProperty("Phases"), JsonConverter(typeof(PhaseTimingsConverter))]
        internal readonly Timing[] Timings = new Timing[(int)TransitionPhase.Count];
        internal PhaseWork[] Phases => Snapshot(Timings);
    }
    private sealed class PhaseTimingsConverter : JsonConverter
    {
        public override bool CanRead => false;
        public override bool CanConvert(Type type) => type == typeof(Timing[]);
        public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
            => throw new NotSupportedException();
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            var timings = (Timing[])value;
            for (var i = 0; i < timings.Length; i++)
            {
                if (timings[i].Calls == 0) continue;
                writer.WriteStartObject();
                writer.WritePropertyName("Phase"); writer.WriteValue(PhaseNames[i]);
                writer.WritePropertyName("Calls"); writer.WriteValue(timings[i].Calls);
                writer.WritePropertyName("TotalMs"); writer.WriteValue(Milliseconds(timings[i].Ticks));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }
    internal sealed class JournalEvent
    {
        public int Frame, SquadId;
        public float RecordedAt;
        public float? RaidSeconds;
        public string Kind, Detail, ProfileId;
        public double? DurationMs;
        public PhaseWork[] Phases;
        public int? AwakeBots, GhostBots, AwakeWithinWakeDistance;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public GhostDeathTrace.Death DeathDetails;
    }
    private static readonly FrameWork[] Frames = { new(), new(), new(), new() };
    private static readonly List<Hitch> Hitches = new(MaxHitches);
    private static readonly List<JournalEvent> Events = new(MaxEvents);
    private static readonly string[] PhaseNames = Enum.GetNames(typeof(TransitionPhase));
    private static string _map, _path;
    private static bool _inUpdate, _wasEnabled;
    private static float _nextFlush, _raidStartedAt = float.NaN;
    private static int _observedFrame = -1, _gc0, _gc1, _gc2, _totalHitches, _droppedHitches, _droppedEvents, _batch;
    private static Task<string> _writer;
    private static Hitch[] _buffer, _spareBuffer, _writingBuffer;
    private static Hitch[] CreateBuffer()
    {
        var buffer = new Hitch[MaxHitches];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = new Hitch();
        return buffer;
    }
    internal static bool Enabled => _map != null && Plugin.PerfLogging is { Value: true };
    internal static Task PendingWrite => _writer ?? Task.CompletedTask;

    internal static Task PrepareExport()
    {
        Flush(final: false, force: true);
        return PendingWrite;
    }

    internal static void Reset(string map)
    {
        GhostDeathTrace.ResetRaid();
        _map = map; _path = null; _wasEnabled = _inUpdate = false;
        _raidStartedAt = float.NaN; _observedFrame = -1;
        _totalHitches = _droppedHitches = _droppedEvents = _batch = 0;
        _nextFlush = Time.realtimeSinceStartup + FlushSeconds;
        Hitches.Clear(); Events.Clear();
        foreach (var frame in Frames) frame.Reset(-1);
        _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        if (Enabled) Log.Always("PERF SESSION: map=" + map);
    }

    internal static void RaidStarted()
    {
        if (_map == null) return;
        _raidStartedAt = Time.realtimeSinceStartup;
        Event("raid-start");
    }
    private static float? RaidSeconds(float at) => float.IsNaN(_raidStartedAt) ? null : at - _raidStartedAt;
    private static FrameWork Work(int frame)
    {
        var slot = Frames[(frame & int.MaxValue) % Frames.Length];
        if (slot.Frame != frame) slot.Reset(frame);
        return slot;
    }

    internal static void BeginFrame()
    {
        RefreshWriter();
        if (!Enabled)
        {
            _inUpdate = false;
            if (_wasEnabled) Flush(final: false, force: true);
            _wasEnabled = false;
            return;
        }
        if (!_wasEnabled)
        {
            _buffer ??= CreateBuffer();
            if (_spareBuffer == null && _writer == null) _spareBuffer = CreateBuffer();
            foreach (var frame in Frames) frame.Reset(-1);
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _observedFrame = -1; _wasEnabled = true;
            Event("recording-enabled");
        }
        ObservePreviousFrame();
        Work(Time.frameCount);
        _inUpdate = true;
    }

    private static void ObservePreviousFrame()
    {
        var observed = Time.frameCount;
        if (_observedFrame == observed) return;
        _observedFrame = observed;
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var ms = Time.unscaledDeltaTime * 1000f;
        if (Time.unscaledDeltaTime > FrameBudgetSeconds)
        {
            _totalHitches++;
            if (Hitches.Count >= MaxHitches) _droppedHitches++;
            else
            {
                var frame = observed - 1;
                var work = Frames[(frame & int.MaxValue) % Frames.Length];
                var matched = work.Frame == frame && work.HasUpdate;
                _buffer ??= CreateBuffer(); // A raid can finish before its first Update.
                var item = _buffer[Hitches.Count];
                item.Frame = frame; item.ObservedAtFrame = observed; item.ObservedAt = Time.realtimeSinceStartup;
                item.RaidSeconds = RaidSeconds(Time.realtimeSinceStartup - Time.unscaledDeltaTime); item.FrameMs = ms;
                item.Gc0 = gc0 - _gc0; item.Gc1 = gc1 - _gc1; item.Gc2 = gc2 - _gc2;
                item.UpdateMatched = matched; item.OrbitUpdateMs = matched ? work.UpdateMs : null;
                item.ExternalActivationMs = matched ? work.ExternalMs : null;
                item.DiagnosticBookkeepingMs = matched ? work.BookkeepingMs : null;
                item.UnattributedMs = matched ? Math.Max(0, ms - work.UpdateMs - work.ExternalMs - work.BookkeepingMs) : null;
                if (work.Frame == frame) Array.Copy(work.Phases, item.Timings, item.Timings.Length);
                else Array.Clear(item.Timings, 0, item.Timings.Length);
                Hitches.Add(item);
            }
        }
        _gc0 = gc0; _gc1 = gc1; _gc2 = gc2;
    }

    internal static void EndUpdate(double ms)
    {
        if (!Enabled) return;
        var work = Work(Time.frameCount);
        work.UpdateMs += ms; work.HasUpdate = true; _inUpdate = false;
    }
    internal static void RecordPhase(TransitionPhase phase, long ticks, int frame, bool external = false)
    {
        if (!Enabled) return;
        var work = Work(frame);
        ref var timing = ref work.Phases[(int)phase];
        timing.Calls++; timing.Ticks += ticks;
        if (external && !_inUpdate) work.ExternalMs += Milliseconds(ticks);
    }
    internal static void Bookkeeping(long ticks)
    {
        if (Enabled) Work(Time.frameCount).BookkeepingMs += Milliseconds(ticks);
    }
    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static PhaseWork[] Snapshot(Timing[] timings, Timing[] baseline = null)
    {
        var count = 0;
        for (var i = 0; i < timings.Length; i++)
            if (timings[i].Calls > (baseline?[i].Calls ?? 0)) count++;
        var result = new PhaseWork[count];
        var next = 0;
        for (var i = 0; i < timings.Length; i++)
        {
            var calls = timings[i].Calls - (baseline?[i].Calls ?? 0);
            if (calls > 0) result[next++] = new PhaseWork { Phase = PhaseNames[i], Calls = calls,
                TotalMs = Milliseconds(timings[i].Ticks - (baseline?[i].Ticks ?? 0)) };
        }
        return result;
    }

    internal static void Event(string kind, string profileId = null, string detail = null, int squadId = -1)
    {
        if (!Enabled) return;
        AddEvent(new JournalEvent { Kind = kind, ProfileId = profileId, Detail = detail, SquadId = squadId,
            Frame = Time.frameCount, RecordedAt = Time.realtimeSinceStartup, RaidSeconds = RaidSeconds(Time.realtimeSinceStartup) });
    }
    private static void AddEvent(JournalEvent item)
    {
        if (Events.Count >= MaxEvents) { _droppedEvents++; return; }
        Events.Add(item);
    }

    internal static void Population(int awake, int ghost, int nearby)
    {
        if (!Enabled) return;
        AddEvent(new JournalEvent { Kind = "population", Frame = Time.frameCount, SquadId = -1,
            RecordedAt = Time.realtimeSinceStartup, RaidSeconds = RaidSeconds(Time.realtimeSinceStartup),
            AwakeBots = awake, GhostBots = ghost, AwakeWithinWakeDistance = nearby });
    }

    internal readonly struct WorkScope : IDisposable
    {
        private readonly long _start;
        private readonly int _frame, _squad;
        private readonly string _kind, _profile;
        private readonly object _subject;
        private readonly bool _always;
        private readonly Timing[] _baseline;
        private readonly TransitionPerformance.Scope _phase;
        internal WorkScope(TransitionPhase phase, string kind, object subject, int squad, string profile, bool always, bool details)
        {
            _start = Stopwatch.GetTimestamp(); _frame = Time.frameCount; _kind = kind;
            _subject = subject; _squad = squad; _profile = profile; _always = always;
            _baseline = details ? (Timing[])Work(_frame).Phases.Clone() : null;
            _phase = TransitionPerformance.Measure(phase);
        }
        public void Dispose()
        {
            if (_start == 0) return;
            _phase.Dispose();
            var ms = Milliseconds(Stopwatch.GetTimestamp() - _start);
            if (!Enabled || (!_always && ms < 2d)) return;
            AddEvent(new JournalEvent { Kind = _kind, Frame = _frame, RecordedAt = Time.realtimeSinceStartup,
                RaidSeconds = RaidSeconds(Time.realtimeSinceStartup), SquadId = _squad, ProfileId = _profile,
                Detail = _subject is string text ? text : _subject?.GetType().Name, DurationMs = ms,
                Phases = _baseline != null && Time.frameCount == _frame ? Snapshot(Work(_frame).Phases, _baseline) : null });
        }
    }
    internal static WorkScope Measure(TransitionPhase phase, string kind, object subject = null, int squad = -1,
        string profile = null, bool always = false, bool details = false)
        => Enabled ? new WorkScope(phase, kind, subject, squad, profile, always, details) : default;

    internal static void DeathTrace(string profile, GhostDeathTrace.Death details)
    {
        if (!Enabled) return;
        AddEvent(new JournalEvent { Kind = "ghost-death-detail", Frame = Time.frameCount,
            RecordedAt = Time.realtimeSinceStartup, RaidSeconds = RaidSeconds(Time.realtimeSinceStartup),
            ProfileId = profile, DurationMs = details.DamageContextMs, DeathDetails = details });
    }

    internal static void Tick()
    {
        if (Enabled && (Hitches.Count >= FlushHitches || Time.realtimeSinceStartup >= _nextFlush)) Flush(false);
    }
    internal static void RefreshWriter()
    {
        if (_writer?.IsCompleted != true) return;
        Log.Always(_writer.GetAwaiter().GetResult());
        _writer = null;
        _spareBuffer = _writingBuffer; _writingBuffer = null;
    }
    private static void Flush(bool final, bool force = false)
    {
        if (_map == null || (!_wasEnabled && Hitches.Count == 0 && Events.Count == 0)) return;
        RefreshWriter();
        if (!final && !force && _writer != null) return;
        if (!final && !force && Hitches.Count == 0 && Events.Count == 0) { _nextFlush = Time.realtimeSinceStartup + FlushSeconds; return; }
        _path ??= Path.Combine(BepInEx.Paths.BepInExRootPath, "ORBIT", "diagnostics",
            "performance-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        DebugExport.RegisterDiagnostic(_path);
        var frame = Work(Time.frameCount);
        var packet = new
        {
            Schema = 1, TargetFps, FrameThresholdMs = 1000d / TargetFps, Map = _map, Batch = ++_batch, Final = final, SavedAt = Time.realtimeSinceStartup,
            RaidStartedAt = float.IsNaN(_raidStartedAt) ? (float?)null : _raidStartedAt,
            TotalHitches = _totalHitches, DroppedHitches = _droppedHitches, DroppedEvents = _droppedEvents,
            WakeIntervalMs = ServerConfig.GhostMode.WakeIntervalMs,
            Notes = "Hitches contains every observed frame strictly over the 60 FPS budget, not necessarily a perceptible stutter. FrameMs is Unity's previous-frame delta. Frame=ObservedAtFrame-1; UpdateMatched=false means no matching Update. RaidSeconds is null until OnGameStarted and may be negative during loading. Phase work is inclusive, never sum nested phases. ExternalActivationMs excludes work inside ORBIT Update. UnattributedMs is an estimate, not evidence that ORBIT is uninvolved. GC counts do not measure pause duration. Events include slow calls >=2ms and every Ghost death. Dropped counters disclose buffer overflow. FinalFrameWork has no observed frame duration yet.",
            Hitches = Hitches.ToArray(), Events = Events.ToArray(),
            FinalFrameWork = final ? new { Frame = frame.Frame, frame.UpdateMs, frame.ExternalMs,
                frame.BookkeepingMs, Phases = Snapshot(frame.Phases) } : null,
        };
        // The writer owns the detached buffer until it completes. A final batch can queue
        // behind it; only that uncommon path needs another buffer instead of the spare.
        _writingBuffer = _buffer;
        _buffer = _spareBuffer ?? CreateBuffer(); _spareBuffer = null;
        Hitches.Clear(); Events.Clear(); _nextFlush = Time.realtimeSinceStartup + FlushSeconds;
        var path = _path;
        var prior = _writer;
        _writer = Task.Run(async () =>
        {
            var priorResult = prior == null ? null : await prior.ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = File.AppendText(path))
                using (var json = new JsonTextWriter(stream))
                {
                    JsonSerializer.CreateDefault().Serialize(json, packet);
                    stream.WriteLine();
                }
                var result = "PERF JOURNAL saved: " + path + (final ? " (final)" : "");
                return priorResult != null && priorResult.Contains("failed:") ? priorResult + " | " + result : result;
            }
            catch (Exception e) { return "PERF JOURNAL failed: " + path + " batch=" + packet.Batch + " " + e.Message; }
        });
        Log.Always(FormattableString.Invariant($"PERF JOURNAL: batch={_batch} final={final} hitches={packet.Hitches.Length} total={_totalHitches} droppedHitches={_droppedHitches} droppedEvents={_droppedEvents} path={path}"));
    }
    internal static void Finish()
    {
        if (Enabled) { ObservePreviousFrame(); Event("raid-end"); }
        Flush(final: true);
        _map = null; _inUpdate = _wasEnabled = false;
        Hitches.Clear(); Events.Clear();
    }
}
