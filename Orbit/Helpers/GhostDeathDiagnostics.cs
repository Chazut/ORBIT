using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;

namespace Orbit.Helpers;

// Timings only: preserve the engine's damage, kill attribution and event invocation order.
// Hooks are installed at startup, never compiled or patched during a Ghost casualty.
internal static class GhostDeathDiagnostics
{
    [ThreadStatic] private static Player _victim;
    private static readonly Dictionary<MethodBase, TransitionPhase> Phases = new();

    internal readonly struct Context : IDisposable
    {
        private readonly Player _previous;
        internal Context(Player victim) { _previous = _victim; _victim = victim; }
        public void Dispose() { _victim = _previous; }
    }

    internal static Context Begin(Player victim)
        => new(Plugin.PerfLogging is { Value: true } ? victim : null);

    internal static TransitionPerformance.Scope Measure(TransitionPhase phase)
        => _victim != null ? TransitionPerformance.Measure(phase) : default;

    internal static void Enable()
    {
        var harmony = new Harmony("orbit.ghost-death.diagnostics");
        Bind(harmony, typeof(ActiveHealthController), "ApplyDamage", TransitionPhase.GhostHealthDamage);
        Bind(harmony, typeof(ActiveHealthController), "Kill", TransitionPhase.GhostHealthKill);
        Bind(harmony, typeof(Player), "OnBeenKilledByAggressor", TransitionPhase.GhostAggressor);
        Bind(harmony, typeof(Player), "OnDead", TransitionPhase.GhostOnDead, callbacks: true);
        Bind(harmony, typeof(Player), "CreateCorpse", TransitionPhase.GhostCorpse);
        Bind(harmony, typeof(Player), "ApplyCorpseImpulse", TransitionPhase.GhostCorpseImpulse);
        Bind(harmony, typeof(Player), "PlayDeathSound", TransitionPhase.GhostDeathSound);
    }

    private static void Bind(Harmony harmony, Type type, string name, TransitionPhase phase, bool callbacks = false)
    {
        try
        {
            var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == name && !m.IsGenericMethod);
            Phases[method] = phase;
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(GhostDeathDiagnostics), nameof(Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(GhostDeathDiagnostics), nameof(Finalizer)) { priority = Priority.Last },
                transpiler: callbacks ? new HarmonyMethod(typeof(GhostDeathDiagnostics), nameof(Callbacks)) : null);
        }
        catch (Exception e) { Log.Warning($"Ghost death timing unavailable for {type.Name}.{name}: {e.Message}"); }
    }

    private static void Prefix(object __instance, MethodBase __originalMethod, out TransitionPerformance.Scope __state)
    {
        __state = default;
        if (PerformanceJournal.Enabled && Phases[__originalMethod] == TransitionPhase.GhostOnDead && __instance is Player player)
            PerformanceJournal.Event("death", player.ProfileId);
        if (_victim == null || Plugin.PerfLogging is not { Value: true }) return;
        if (ReferenceEquals(__instance, _victim) || ReferenceEquals(__instance, _victim.ActiveHealthController))
            __state = TransitionPerformance.Measure(Phases[__originalMethod]);
    }

    private static Exception Finalizer(Exception __exception, TransitionPerformance.Scope __state)
    {
        __state.Dispose();
        return __exception;
    }

    // Each wrapper keeps the original delegate and arguments, including a multicast delegate's
    // exception/order semantics. We measure the six event boundaries, not individual subscribers.
    private static IEnumerable<CodeInstruction> Callbacks(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var invokes = code.Where(i => (i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call)
            && i.operand is MethodInfo m && m.Name == "Invoke" && typeof(Delegate).IsAssignableFrom(m.DeclaringType)).ToList();
        var counts = new[] { 4, 4, 1, 1, 0, 0 };
        if (invokes.Count != counts.Length || invokes.Where((i, n) =>
                ((MethodInfo)i.operand).GetParameters().Length != counts[n]
                || ((MethodInfo)i.operand).ReturnType != typeof(void)).Any())
        {
            Log.Warning("Ghost death event timings unavailable: unexpected Player.OnDead callback layout; original calls preserved.");
            return code;
        }
        var phases = new[] { TransitionPhase.GhostPlayerDeadCallbacks, TransitionPhase.GhostGlobalDeadCallbacks,
            TransitionPhase.GhostUnspawnCallbacks, TransitionPhase.GhostIPlayerUnspawnCallbacks, TransitionPhase.GhostExfilCallback, TransitionPhase.GhostInteractionCallback };
        for (var n = 0; n < invokes.Count; n++)
        {
            var call = invokes[n];
            var method = (MethodInfo)call.operand;
            var parameters = new[] { method.DeclaringType }.Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
            var wrapper = new DynamicMethod("OrbitTime" + phases[n], typeof(void), parameters, typeof(GhostDeathDiagnostics), true);
            var il = wrapper.GetILGenerator();
            var scope = il.DeclareLocal(typeof(TransitionPerformance.Scope));
            il.Emit(OpCodes.Ldc_I4, (int)phases[n]);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(GhostDeathDiagnostics), nameof(Measure)));
            il.Emit(OpCodes.Stloc, scope);
            il.BeginExceptionBlock();
            for (var p = 0; p < parameters.Length; p++) il.Emit(OpCodes.Ldarg, (short)p);
            il.Emit(call.opcode, method);
            il.BeginFinallyBlock();
            il.Emit(OpCodes.Ldloca, scope);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(TransitionPerformance.Scope), nameof(TransitionPerformance.Scope.Dispose)));
            il.EndExceptionBlock();
            il.Emit(OpCodes.Ret);
            call.opcode = OpCodes.Call;
            call.operand = wrapper;
        }
        return code;
    }
}
