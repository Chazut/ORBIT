using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Orbit;
using Orbit.Helpers;
using UnityEngine;

internal static class JournalRegression
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Directory.CreateTempSubdirectory("orbit-journal-").FullName;
        BepInEx.Paths.BepInExRootPath = root;
        Plugin.PerfLogging.Value = true;
        Time.frameCount = 42975;
        Time.realtimeSinceStartup = 910.4113f;
        Time.unscaledDeltaTime = .01f;
        PerformanceJournal.Reset("factory4_day");
        PerformanceJournal.RaidStarted();
        PerformanceJournal.BeginFrame();
        PerformanceJournal.BeginBookkeeping(Stopwatch.Frequency / 1000);
        PerformanceJournal.EndUpdate(.03d);
        PerformanceJournal.EndBookkeeping(Stopwatch.Frequency / 500, Stopwatch.Frequency * 4);
        Time.frameCount++;
        Time.unscaledDeltaTime = 4.1f;
        Time.realtimeSinceStartup += Time.unscaledDeltaTime;
        PerformanceJournal.BeginFrame();
        PerformanceJournal.EndUpdate(.04d);
        PerformanceJournal.Event("factory-drawer");
        PerformanceJournal.PrepareExport().GetAwaiter().GetResult();
        // Pause the real journal-completion sink. The next frame and final JSON must still progress.
        Log.DebugFlush()();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Plugin.LogSource.BeforeInfo = () =>
        {
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("blocked completion held the game thread");
        };
        try
        {
            PerformanceJournal.RefreshWriter();
            check(entered.Wait(2000), "completed journal reports through the worker");
            Time.frameCount++;
            Time.unscaledDeltaTime = .023f;
            Time.realtimeSinceStartup += Time.unscaledDeltaTime;
            PerformanceJournal.BeginFrame();
            PerformanceJournal.EndUpdate(.02d);
            PerformanceJournal.Finish();
            PerformanceJournal.PendingWrite.GetAwaiter().GetResult();
            PerformanceJournal.RefreshWriter();
            string file = DebugExport.LastFile;
            var packets = File.ReadAllLines(file).Select(JObject.Parse).ToArray();
            check(packets.Length == 2 && (bool)packets[1]["Final"], "JSON finishes even while the text sink is blocked");
            var slow = packets[0]["Hitches"].Single(h => (int)h["Frame"] == 42975);
            check((double)slow["DiagnosticBeginMs"] == 1 && (double)slow["DiagnosticCaptureMs"] == 2
                && (double)slow["DiagnosticJournalTickMs"] == 4000 && (double)slow["DiagnosticBookkeepingMs"] == 4003,
                "slowest bookkeeping stage and total are attributed to the previous Unity frame");
            var normal = packets[1]["Hitches"].Single(h => (int)h["Frame"] == 42976);
            check(normal["DiagnosticJournalTickMs"] == null, "ordinary hitches carry no extra diagnostic fields");
            check(packets.SelectMany(p => p["Events"]).Count(e => (string)e["Kind"] == "factory-drawer") == 1
                && DebugExport.Registrations == 1, "no lost event and only one archive registration");
            File.Delete(file);
            Directory.Delete(Path.GetDirectoryName(file));
            Directory.Delete(Path.Combine(root, "ORBIT"));
            Directory.Delete(root);
        }
        finally { release.Set(); Log.DebugFlush()(); Plugin.LogSource.BeforeInfo = null; }
    }
}

namespace BepInEx { internal static class Paths { internal static string BepInExRootPath; } }
namespace Orbit.Helpers
{
    internal enum TransitionPhase { Sample, Count }
    internal static class TransitionPerformance
    {
        internal readonly struct Scope { public void Dispose() { } }
        internal static Scope Measure(TransitionPhase phase) => default;
    }
    internal static class DebugExport
    {
        internal static string LastFile;
        internal static int Registrations;
        internal static void RegisterDiagnostic(string path) { LastFile = path; Registrations++; }
    }
    internal static class GhostDeathTrace
    {
        internal sealed class Death { public double DamageContextMs { get; set; } }
        internal static void ResetRaid() { }
    }
    internal static class ServerConfig
    {
        internal static class GhostMode { internal const int WakeIntervalMs = 200; }
    }
}
