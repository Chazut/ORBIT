using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Orbit;

/// <summary>
/// Unified logger for ORBIT. Routes all messages through the BepInEx
/// ManualLogSource attached in Plugin.Awake, prefixed with the current
/// Unity frame counter so per-tick events can be correlated across systems.
/// </summary>
public static class Log
{
    [Conditional("DEBUG")]
    public static void Debug(string message)
    {
        Plugin.LogSource.LogInfo($"F{Time.frameCount}: {message}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(string message)
    {
        Plugin.LogSource.LogInfo($"F{Time.frameCount}: {message}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warning(string message)
    {
        Plugin.LogSource.LogWarning($"F{Time.frameCount}: {message}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(string message)
    {
        Plugin.LogSource.LogError($"F{Time.frameCount}: {message}");
    }
}
