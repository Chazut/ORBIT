using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Orbit.Helpers;

internal static class NativeExtractionDiagnostics
{
    [ThreadStatic] private static bool _active;
    internal readonly struct Context : IDisposable
    {
        private readonly bool _previous;
        private readonly long _start;
        private readonly string _profile;
        private readonly TransitionPerformance.Scope _timing;
        internal Context(string profile)
        {
            _previous = _active; _active = PerformanceJournal.Enabled; _profile = profile;
            _start = _active ? Stopwatch.GetTimestamp() : 0;
            _timing = _active ? TransitionPerformance.Measure(TransitionPhase.NativeExtract) : default;
        }
        public void Dispose()
        {
            _timing.Dispose(); _active = _previous;
            if (_start == 0) return;
            var detail = $"elapsedMs={(Stopwatch.GetTimestamp() - _start) * 1000d / Stopwatch.Frequency:F2}";
            PerformanceJournal.Event("native-extract", _profile, detail);
            Log.Info($"PERF NATIVE EXTRACT: profile={_profile} {detail}");
        }
    }
    internal static Context Begin(string profile) => new(profile);
    private static TransitionPerformance.Scope Enter(int phase)
        => _active ? TransitionPerformance.Measure((TransitionPhase)phase) : default;

    internal static void Enable()
    {
        try
        {
            var method = AccessTools.Method(typeof(BotLeaveData), nameof(BotLeaveData.RemoveFromMap));
            RuntimeCodePreparation.Include(method);
            new Harmony("orbit.extract.diagnostics").Patch(method,
                transpiler: new HarmonyMethod(typeof(NativeExtractionDiagnostics), nameof(Calls)));
        }
        catch (Exception e) { Log.Warning($"Native extraction details unavailable: {e.Message}"); }
    }

    private static IEnumerable<CodeInstruction> Calls(IEnumerable<CodeInstruction> instructions)
    {
        var count = 0;
        foreach (var call in instructions)
        {
            if ((call.opcode == OpCodes.Call || call.opcode == OpCodes.Callvirt) && call.operand is MethodInfo method
                && !method.ContainsGenericParameters && method.ReturnType == typeof(void))
            {
                var phase = method.Name switch
                {
                    "Invoke" when typeof(Delegate).IsAssignableFrom(method.DeclaringType) => TransitionPhase.NativeExtractLeave,
                    "Deactivate" => TransitionPhase.NativeExtractDeactivate,
                    "Dispose" => TransitionPhase.NativeExtractDispose,
                    "BotDespawn" => TransitionPhase.NativeExtractDespawn,
                    _ => TransitionPhase.Count
                };
                if (phase != TransitionPhase.Count)
                {
                    RuntimeCodePreparation.Include(method);
                    var types = (method.IsStatic ? Type.EmptyTypes : new[] { method.DeclaringType })
                        .Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
                    var wrapper = new DynamicMethod("OrbitExtractCall" + count++, typeof(void), types, typeof(NativeExtractionDiagnostics), true);
                    var il = wrapper.GetILGenerator(); var scope = il.DeclareLocal(typeof(TransitionPerformance.Scope));
                    il.Emit(OpCodes.Ldc_I4, (int)phase); il.Emit(OpCodes.Call, AccessTools.Method(typeof(NativeExtractionDiagnostics), nameof(Enter)));
                    il.Emit(OpCodes.Stloc, scope); il.BeginExceptionBlock();
                    for (var i = 0; i < types.Length; i++) il.Emit(OpCodes.Ldarg, (short)i);
                    il.Emit(call.opcode, method);
                    il.BeginFinallyBlock(); il.Emit(OpCodes.Ldloca, scope);
                    il.Emit(OpCodes.Call, AccessTools.Method(typeof(TransitionPerformance.Scope), nameof(TransitionPerformance.Scope.Dispose)));
                    il.EndExceptionBlock(); il.Emit(OpCodes.Ret);
                    var compiled = wrapper.CreateDelegate(System.Linq.Expressions.Expression.GetDelegateType(types.Concat(new[] { typeof(void) }).ToArray()));
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(compiled);
                    call.opcode = OpCodes.Call; call.operand = wrapper;
                }
            }
            yield return call;
        }
        if (count != 4) Log.Warning($"Native extraction details: expected4 call sites, instrumented{count}; inspect current engine layout.");
    }
}
