using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;

namespace Orbit.Fika;

internal static class FikaPatchCleanup
{
    // Constructor rollback must never replace the exception that triggered it.
    internal static void Rollback(IDisposable bridge, ManualLogSource log)
    {
        try { bridge.Dispose(); }
        catch (Exception error) { log.LogWarning($"Fika cleanup failed ({bridge.GetType().Name}): {error}"); }
    }

    internal static void Unpatch(Harmony harmony, ManualLogSource log)
    {
        foreach (var original in harmony.GetPatchedMethods().ToArray())
        {
            try { harmony.Unpatch(original, HarmonyPatchType.All, harmony.Id); }
            catch (Exception error)
            {
                log.LogWarning($"Fika cleanup failed ({harmony.Id}, {original.DeclaringType}.{original.Name}): {error}");
            }
        }
    }
}
