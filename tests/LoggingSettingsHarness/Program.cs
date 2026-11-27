using Orbit;
using System.Collections.Concurrent;
var checks=0;
void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
Log.Initialize();
foreach(var quiet in new[]{false,true})foreach(var flags in Enumerable.Range(0,16))
{
    Plugin.QuietLogging=new(){Value=quiet};Plugin.LogLevels=new(){Value=(OrbitLogLevel)flags};Plugin.PerfLogging=new(){Value=flags%2==0};
    Plugin.LogSource.Lines.Clear();
    var settings=Log.ReportSettings("export");
    Log.Debug("requested-debug");Log.Info("requested-info");Log.Warning("requested-warning");Log.Error("requested-error");
    Check(Log.DebugFlush()()==null,"queued output drained");
    var lines=Plugin.LogSource.Lines.ToArray();
    var effective=quiet?OrbitLogLevel.Warning|OrbitLogLevel.Error:(OrbitLogLevel)flags;
    Check(settings.Contains($"quiet={quiet}") && settings.Contains($"configured=[{(OrbitLogLevel)flags}]") && settings.Contains($"effective=[{effective}]"),"snapshot reflects Quiet override and selected flags");
    Check(lines.Count(l=>l.Contains("LOG SETTINGS: reason=export"))==1,"settings survive every filter combination");
    foreach(var (level,name) in new[]{(OrbitLogLevel.Debug,"debug"),(OrbitLogLevel.Info,"info"),(OrbitLogLevel.Warning,"warning"),(OrbitLogLevel.Error,"error")})
        Check(lines.Any(l=>l.EndsWith("requested-"+name))==((effective&level)!=0),"reported effective filter matches actual output");
}
// A blocked disk/console listener must not hold the game thread at either end of a journal batch.
Plugin.QuietLogging.Value = true;
Plugin.LogSource.Lines.Clear();
using (var entered = new ManualResetEventSlim())
using (var release = new ManualResetEventSlim())
{
    int producer = Environment.CurrentManagedThreadId;
    int sinkThread = producer;
    Plugin.LogSource.BeforeInfo = () =>
    {
        sinkThread = Environment.CurrentManagedThreadId;
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("synchronous log sink");
    };
    try
    {
        UnityEngine.Time.frameCount = 42975;
        Log.Performance("PERF JOURNAL: batch=17");
        Check(entered.Wait(TimeSpan.FromSeconds(2)), "worker reached blocking listener");
        Check(sinkThread != producer, "performance listener runs outside game thread");
        UnityEngine.Time.frameCount = 42977;
        Log.Performance("PERF JOURNAL saved: fixture.jsonl");
        Check(Plugin.LogSource.Lines.IsEmpty, "both producer calls return while the listener remains blocked");
    }
    finally { release.Set(); }
    Check(Log.DebugFlush()() == null, "archive drains both messages");
    Check(Plugin.LogSource.Lines.SequenceEqual(new[] { "F42975: PERF JOURNAL: batch=17", "F42977: PERF JOURNAL saved: fixture.jsonl" }), "original frames, order and quiet-mode visibility survive");
    Plugin.LogSource.BeforeInfo = null;
}
JournalRegression.Run(Check);
if (args.Contains("--compare-blocked-listener"))
{
    // Reproduce the measured Factory delay at the BepInEx boundary only.
    Plugin.LogSource.BeforeInfo = () => Thread.Sleep(4072);
    var timer = System.Diagnostics.Stopwatch.StartNew();
    Log.Always("simulated synchronous PERF JOURNAL");
    var direct = timer.Elapsed.TotalMilliseconds;
    timer.Restart();
    Log.Performance("simulated queued PERF JOURNAL");
    var queued = timer.Elapsed.TotalMilliseconds;
    Check(Log.DebugFlush()() == null, "delayed journal text still written");
    Plugin.LogSource.BeforeInfo = null;
    Console.WriteLine($"Simulated 4072 ms listener, caller cost: direct {direct:F3} ms, queued {queued:F3} ms");
}
Log.Shutdown();Console.WriteLine($"PASS {checks} logging settings checks");
namespace Orbit
{
    public sealed class Entry<T>{public T Value;}
    public static class Plugin
    {
        public static Entry<bool> QuietLogging,PerfLogging;
        public static Entry<OrbitLogLevel> LogLevels;
        public static Source LogSource=new();
    }
    public sealed class Source
    {
        public ConcurrentQueue<string> Lines=new();
        public Action BeforeInfo;
        public void LogInfo(string s) { BeforeInfo?.Invoke(); Lines.Enqueue(s); }
        public void LogWarning(string s)=>Lines.Enqueue(s);
        public void LogError(string s)=>Lines.Enqueue(s);
    }
}
namespace UnityEngine{public static class Time{public static int frameCount;public static float unscaledTime, unscaledDeltaTime, realtimeSinceStartup;}}
namespace BepInEx.Logging
{
    public static class Logger{public static List<DiskLogListener> Listeners=new();}
    public sealed class DiskLogListener{public TextWriter LogWriter;}
}
