using System.Runtime.CompilerServices;
using System.Text;
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
    // Quiet logging wins: a one-click "only warnings + errors". Otherwise the per-level flags apply. Before the
    // config is bound (very early boot) default to everything except Debug.
    private static OrbitLogLevel Effective()
    {
        if (Plugin.QuietLogging is { Value: true }) return OrbitLogLevel.Warning | OrbitLogLevel.Error;
        return Plugin.LogLevels?.Value ?? (OrbitLogLevel.Info | OrbitLogLevel.Warning | OrbitLogLevel.Error);
    }

    /// <summary>True while Debug output is enabled — guard expensive debug-string building with this in hot paths.</summary>
    public static bool DebugEnabled => (Effective() & OrbitLogLevel.Debug) != 0;

    public static void Debug(string message)
    {
        if ((Effective() & OrbitLogLevel.Debug) == 0) return;
        Plugin.LogSource.LogInfo($"F{Time.frameCount}: {message}");
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
        Plugin.LogSource.LogInfo($"F{Time.frameCount}: {message.GetText()}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(string message)
    {
        if ((Effective() & OrbitLogLevel.Info) == 0) return;
        Plugin.LogSource.LogInfo($"F{Time.frameCount}: {message}");
    }

    /// <summary>Always written (version banner and other must-see one-shots), regardless of levels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Always(string message)
    {
        Plugin.LogSource.LogInfo($"F{Time.frameCount}: {message}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warning(string message)
    {
        if ((Effective() & OrbitLogLevel.Warning) == 0) return;
        Plugin.LogSource.LogWarning($"F{Time.frameCount}: {message}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(string message)
    {
        if ((Effective() & OrbitLogLevel.Error) == 0) return;
        Plugin.LogSource.LogError($"F{Time.frameCount}: {message}");
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
    {
        shouldAppend = Log.DebugEnabled;
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
