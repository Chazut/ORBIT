using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit;

/// <summary>Message levels ORBIT can write. [Flags] so the F12 entry renders as per-level checkboxes.</summary>
[System.Flags]
public enum OrbitLogLevel
{
    None = 0,
    Info = 1,
    Debug = 2,
    Warning = 4,
    Error = 8,
}

/// <summary>
/// Unified logger for ORBIT. Routes through the BepInEx ManualLogSource, prefixed with the Unity frame counter.
/// Levels are gated at RUNTIME (Debug is compiled into release builds now, not stripped) by the F12 "Log levels"
/// flags, with "Quiet logging" as a one-click clean-mode override.
/// </summary>
public static class Log
{
    private static BufferedLogWriter _writer;
    private static int _mainThreadId, _lastFrame;
    private static float _nextReport;

    internal static void Initialize()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _lastFrame = Time.frameCount;
        var source = Plugin.LogSource;
        _writer = new BufferedLogWriter(message => source.LogInfo(message));
    }

    internal static void Refresh()
    {
        Volatile.Write(ref _lastFrame, Time.frameCount);
        if (Time.unscaledTime < _nextReport) return;
        _nextReport = Time.unscaledTime + 5f;
        ReportLosses(Volatile.Read(ref _writer));
    }

    internal static void Shutdown()
    {
        var writer = Interlocked.Exchange(ref _writer, null);
        if (writer == null) return;
        if (!writer.Stop(1000))
            Plugin.LogSource.LogWarning(Stamp("ASYNC LOG: shutdown drain timed out; pending verbose messages may be missing."));
        ReportLosses(writer);
    }

    internal static System.Func<string> DebugFlush()
    {
        var writer = Volatile.Read(ref _writer);
        var disks = new System.Collections.Generic.List<BepInEx.Logging.DiskLogListener>();
        foreach (var listener in BepInEx.Logging.Logger.Listeners)
            if (listener is BepInEx.Logging.DiskLogListener disk) disks.Add(disk);
        return () => {
            var drained = writer?.Drain(5000) != false;
            foreach (var disk in disks) disk.LogWriter?.Flush();
            return drained ? null : "Verbose log drain timed out; some queued messages may be missing.";
        };
    }

    private static void ReportLosses(BufferedLogWriter writer)
    {
        if (writer == null) return;
        var dropped = writer.TakeDroppedCount();
        var failures = writer.TakeFailureCount();
        if (dropped != 0 || failures != 0)
            Plugin.LogSource.LogWarning(Stamp($"ASYNC LOG: dropped={dropped} writeFailures={failures}; verbose log is incomplete."));
    }

    private static string Stamp(string message)
    {
        var frame = Thread.CurrentThread.ManagedThreadId == _mainThreadId ? Time.frameCount : Volatile.Read(ref _lastFrame);
        return $"F{frame}: {message}";
    }

    private static void WriteVerbose(string message)
    {
        var stamped = Stamp(message);
        var writer = Volatile.Read(ref _writer);
        if (writer == null) Plugin.LogSource.LogInfo(stamped);
        else writer.Enqueue(stamped);
    }

    // Quiet logging wins: a one-click "only warnings + errors". Otherwise the per-level flags apply. Before the
    // config is bound (very early boot) default to everything except Debug.
    private static OrbitLogLevel Effective()
    {
        if (Plugin.QuietLogging is { Value: true }) return OrbitLogLevel.Warning | OrbitLogLevel.Error;
        return Plugin.LogLevels?.Value ?? (OrbitLogLevel.Info | OrbitLogLevel.Warning | OrbitLogLevel.Error);
    }

    /// <summary>True while Debug output is enabled — guard expensive debug-string building with this in hot paths.</summary>
    public static bool DebugEnabled => (Effective() & OrbitLogLevel.Debug) != 0;
    public static bool InfoEnabled => (Effective() & OrbitLogLevel.Info) != 0;

    public static void Debug(string message)
    {
        if ((Effective() & OrbitLogLevel.Debug) == 0) return;
        WriteVerbose(message);
    }

    /// <summary>
    /// Interpolated-string overload — the one every <c>Log.Debug($"...")</c> call site binds to. When Debug is
    /// OFF (the default) the compiler skips the message build entirely: neither the string nor any hole
    /// expression (ToString, Vector3 math, ...) is evaluated. This restores what the old
    /// <c>[Conditional("DEBUG")]</c> compile-stripping provided, while keeping Debug runtime-toggleable.
    /// </summary>
    public static void Debug(DebugMessageHandler message)
    {
        if (!message.Enabled) return;
        WriteVerbose(message.GetText());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(string message)
    {
        if ((Effective() & OrbitLogLevel.Info) == 0) return;
        WriteVerbose(message);
    }

    public static void Info(InfoMessageHandler message)
    {
        if (message.Enabled) WriteVerbose(message.GetText());
    }

    /// <summary>Always written (version banner and other must-see one-shots), regardless of levels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Always(string message)
    {
        Plugin.LogSource.LogInfo(Stamp(message));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warning(string message)
    {
        if ((Effective() & OrbitLogLevel.Warning) == 0) return;
        Plugin.LogSource.LogWarning(Stamp(message));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(string message)
    {
        if ((Effective() & OrbitLogLevel.Error) == 0) return;
        Plugin.LogSource.LogError(Stamp(message));
    }
}

/// <summary>
/// Handler behind <see cref="Log.Debug(DebugMessageHandler)"/>. Its constructor's <c>out bool</c> tells the
/// compiler whether to run the Append calls at all — with Debug off, nothing is built and nothing inside the
/// interpolation holes is evaluated.
/// </summary>
[InterpolatedStringHandler]
public struct DebugMessageHandler
{
    private readonly StringBuilder _sb;

    public DebugMessageHandler(int literalLength, int formattedCount, out bool shouldAppend)
        : this(Log.DebugEnabled, literalLength, formattedCount, out shouldAppend) { }

    internal DebugMessageHandler(bool enabled, int literalLength, int formattedCount, out bool shouldAppend)
    {
        shouldAppend = enabled;
        _sb = shouldAppend ? new StringBuilder(literalLength + formattedCount * 16) : null;
    }

    internal bool Enabled => _sb != null;
    internal string GetText() => _sb?.ToString() ?? string.Empty;

    public void AppendLiteral(string s) => _sb.Append(s);
    public void AppendFormatted(string s) => _sb.Append(s);
    public void AppendFormatted<T>(T value) => _sb.Append(value?.ToString());
    public void AppendFormatted<T>(T value, string format)
        => _sb.Append(value is System.IFormattable f ? f.ToString(format, null) : value?.ToString());

    public void AppendFormatted<T>(T value, int alignment, string format = null)
    {
        var text = value is System.IFormattable f ? f.ToString(format, null) : value?.ToString() ?? string.Empty;
        if (alignment > 0 && text.Length < alignment) _sb.Append(' ', alignment - text.Length);
        _sb.Append(text);
        if (alignment < 0 && text.Length < -alignment) _sb.Append(' ', -alignment - text.Length);
    }
}

[InterpolatedStringHandler]
public struct InfoMessageHandler
{
    private DebugMessageHandler _message;

    public InfoMessageHandler(int literalLength, int formattedCount, out bool shouldAppend)
        => _message = new DebugMessageHandler(Log.InfoEnabled, literalLength, formattedCount, out shouldAppend);

    internal bool Enabled => _message.Enabled;
    internal string GetText() => _message.GetText();
    public void AppendLiteral(string value) => _message.AppendLiteral(value);
    public void AppendFormatted(string value) => _message.AppendFormatted(value);
    public void AppendFormatted<T>(T value) => _message.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string format) => _message.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment, string format = null) => _message.AppendFormatted(value, alignment, format);
}
