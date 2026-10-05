using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Orbit.Helpers;

// Compile a small, explicitly selected set of managed call targets during raid loading.
// Obtaining an entry point does not invoke damage, callbacks, corpse creation or extraction.
internal static class RuntimeCodePreparation
{
    private static readonly HashSet<MethodInfo> Pending = new();
    private static bool _prepared;
    internal static void Include(MethodInfo method)
    {
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method is System.Reflection.Emit.DynamicMethod) return;
        if (method.DeclaringType?.Assembly == typeof(EFT.Player).Assembly) Pending.Add(method);
    }

    internal static void Prepare()
    {
        if (_prepared) return;
        _prepared = true;
        var started = Stopwatch.GetTimestamp();
        var prepared = 0; var unavailable = 0;
        foreach (var method in Pending)
        {
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                if (method.MethodHandle.GetFunctionPointer() != IntPtr.Zero) prepared++;
                else unavailable++;
            }
            catch (Exception) { unavailable++; }
        }
        var detail = $"methods={prepared} unavailable={unavailable} elapsedMs={(Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency:F2}";
        Log.Info("PERF CODE PREPARE: " + detail);
        PerformanceJournal.Event("code-preparation", detail: detail);
        Pending.Clear();
    }
}
