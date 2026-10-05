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
    private static readonly List<MethodBase> CallTargets = new();

    internal readonly struct Context : IDisposable
    {
        private readonly Player _previous;
        private readonly GhostDeathTrace.Context _trace;
        internal Context(Player victim) { _previous = _victim; _victim = victim; _trace = victim != null ? GhostDeathTrace.Begin(victim.ProfileId) : default; }
        public void Dispose() { _trace.Dispose(); _victim = _previous; }
    }

    internal static Context Begin(Player victim)
        => new(Plugin.PerfLogging is { Value: true } ? victim : null);

    internal static TransitionPerformance.Scope Measure(TransitionPhase phase)
        => _victim != null ? TransitionPerformance.Measure(phase) : default;

    internal static void Enable()
    {
        var harmony = new Harmony("orbit.ghost-death.diagnostics");
        Bind(harmony, typeof(Player), "ApplyDamageInfo", TransitionPhase.GhostPlayerDamage, calls: true);
        Bind(harmony, typeof(ActiveHealthController), "ApplyDamage", TransitionPhase.GhostHealthDamage, calls: true);
        Bind(harmony, typeof(ActiveHealthController), "Kill", TransitionPhase.GhostHealthKill, calls: true);
        Bind(harmony, typeof(Player), "OnBeenKilledByAggressor", TransitionPhase.GhostAggressor);
        Bind(harmony, typeof(Player), "OnDead", TransitionPhase.GhostOnDead, calls: true);
        Bind(harmony, typeof(Player), "CreateCorpse", TransitionPhase.GhostCorpse, calls: true);
        Bind(harmony, typeof(Player), "ApplyCorpseImpulse", TransitionPhase.GhostCorpseImpulse);
        Bind(harmony, typeof(Player), "PlayDeathSound", TransitionPhase.GhostDeathSound);
        // Install every outer timing hook before preparing wrappers that call those methods.
        foreach (var target in CallTargets)
        {
            try { harmony.Patch(target, transpiler: new HarmonyMethod(typeof(GhostDeathDiagnostics), nameof(Calls))); }
            catch (Exception e) { Log.Warning($"Ghost death call details unavailable for {target.DeclaringType?.Name}.{target.Name}: {e.Message}"); }
        }
    }

    private static void Bind(Harmony harmony, Type type, string name, TransitionPhase phase, bool calls = false)
    {
        try
        {
            var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == name && !m.IsGenericMethod);
            Phases[method] = phase;
            RuntimeCodePreparation.Include(method as MethodInfo);
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(GhostDeathDiagnostics), nameof(Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(GhostDeathDiagnostics), nameof(Finalizer)) { priority = Priority.Last });
            if (calls) CallTargets.Add(method);
        }
        catch (Exception e) { Log.Warning($"Ghost death timing unavailable for {type.Name}.{name}: {e.Message}"); }
    }

    private static void Prefix(object __instance, MethodBase __originalMethod, out TransitionPerformance.Scope __state)
    {
        __state = default;
        if (PerformanceJournal.Enabled && Phases[__originalMethod] == TransitionPhase.GhostOnDead && __instance is Player player)
        {
            GhostDeathTrace.ObservedDeath();
            PerformanceJournal.Event("death", player.ProfileId);
        }
        if (_victim == null || Plugin.PerfLogging is not { Value: true }) return;
        if (ReferenceEquals(__instance, _victim) || ReferenceEquals(__instance, _victim.ActiveHealthController))
            __state = TransitionPerformance.Measure(Phases[__originalMethod]);
    }

    private static Exception Finalizer(Exception __exception, TransitionPerformance.Scope __state)
    {
        __state.Dispose();
        return __exception;
    }

    // Calls keep their original opcode, arguments, return value and exception propagation.
    // With diagnostics enabled, a captured multicast snapshot is measured in invocation order.
    // Exceptions still stop dispatch at the failing subscriber; callbacks are never replaced on the event.
    private static IEnumerable<CodeInstruction> Calls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var invokes = code.Where(i => (i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call)
            && i.operand is MethodInfo m && m.Name == "Invoke" && typeof(Delegate).IsAssignableFrom(m.DeclaringType)).ToList();
        var counts = new[] { 4, 4, 1, 1, 0, 0 };
        var callbacks = __originalMethod.Name == "OnDead" && invokes.Count == counts.Length
            && !invokes.Where((i, n) => ((MethodInfo)i.operand).GetParameters().Length != counts[n]
                || ((MethodInfo)i.operand).ReturnType != typeof(void)).Any();
        if (__originalMethod.Name == "OnDead" && !callbacks)
            Log.Warning("Ghost death event timings unavailable: unexpected Player.OnDead callback layout; call diagnostics retain original invocations.");
        var phases = new[] { TransitionPhase.GhostPlayerDeadCallbacks, TransitionPhase.GhostGlobalDeadCallbacks,
            TransitionPhase.GhostUnspawnCallbacks, TransitionPhase.GhostIPlayerUnspawnCallbacks, TransitionPhase.GhostExfilCallback, TransitionPhase.GhostInteractionCallback };
        for (var n = 0; n < code.Count; n++)
        {
            var call = code[n];
            if (call.opcode != OpCodes.Call && call.opcode != OpCodes.Callvirt) continue;
            if (call.operand is not MethodInfo method || method.ContainsGenericParameters
                || method.ReturnType.IsByRef || method.ReturnType.IsPointer || method.ReturnType.IsByRefLike
                || n > 0 && code[n - 1].opcode.OpCodeType == OpCodeType.Prefix) continue;
            // Skip simple getters and profiler/string helpers. State changes, event boundaries,
            // damage, animation, inventory and corpse operations are measured individually.
            if (method.Name.StartsWith("get_") || method.DeclaringType == typeof(string)
                || method.Name.StartsWith("ReleaseBeginSample") || method.Name.StartsWith("ReleaseEndSample")) continue;
            RuntimeCodePreparation.Include(method);
            var instance = method.IsStatic ? Type.EmptyTypes : new[] { method.DeclaringType.IsValueType ? method.DeclaringType.MakeByRefType() : method.DeclaringType };
            var parameters = instance.Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
            if (parameters.Any(p => p.IsPointer || p.IsByRefLike || p.IsByRef && p.GetElementType().IsByRefLike)) continue;
            var eventIndex = callbacks ? invokes.IndexOf(call) : -1;
            var phase = eventIndex >= 0 ? (int)phases[eventIndex] : -1;
            var label = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " -> "
                + (eventIndex >= 0 ? phases[eventIndex].ToString() : method.DeclaringType?.FullName + "." + method.Name)
                + " @" + n;
            var site = GhostDeathTrace.Register(label);
            var wrapper = new DynamicMethod("OrbitDeathCall" + site, method.ReturnType, parameters, typeof(GhostDeathDiagnostics), true);
            var il = wrapper.GetILGenerator();
            var scope = il.DeclareLocal(typeof(GhostDeathTrace.Scope));
            var result = method.ReturnType == typeof(void) ? null : il.DeclareLocal(method.ReturnType);
            il.Emit(OpCodes.Ldc_I4, site); il.Emit(OpCodes.Ldc_I4, phase);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(GhostDeathTrace), nameof(GhostDeathTrace.Enter)));
            il.Emit(OpCodes.Stloc, scope);
            il.BeginExceptionBlock();
            if (method.Name == "Invoke" && method.ReturnType == typeof(void) && typeof(Delegate).IsAssignableFrom(method.DeclaringType))
                EmitSubscribers(il, method, parameters.Length);
            else
            {
                for (var p = 0; p < parameters.Length; p++) il.Emit(OpCodes.Ldarg, (short)p);
                il.Emit(call.opcode, method);
            }
            if (result != null) il.Emit(OpCodes.Stloc, result);
            il.BeginFinallyBlock();
            il.Emit(OpCodes.Ldloca, scope);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(GhostDeathTrace.Scope), nameof(GhostDeathTrace.Scope.Dispose)));
            il.EndExceptionBlock();
            if (result != null) il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);
            // Compile the diagnostic wrapper at startup, without running a damage/death operation.
            var delegateType = System.Linq.Expressions.Expression.GetDelegateType(parameters.Concat(new[] { method.ReturnType }).ToArray());
            var compiled = wrapper.CreateDelegate(delegateType);
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(compiled);
            call.opcode = OpCodes.Call; call.operand = wrapper;
        }
        return code;
    }

    private static void EmitSubscribers(ILGenerator il, MethodInfo invoke, int parameters)
    {
        var listeners = il.DeclareLocal(typeof(Delegate[]));
        var index = il.DeclareLocal(typeof(int));
        var callback = il.DeclareLocal(typeof(Delegate));
        var scope = il.DeclareLocal(typeof(GhostDeathTrace.Scope));
        var original = il.DefineLabel(); var done = il.DefineLabel();
        var loop = il.DefineLabel(); var test = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(GhostDeathTrace), nameof(GhostDeathTrace.Listeners)));
        il.Emit(OpCodes.Stloc, listeners); il.Emit(OpCodes.Ldloc, listeners); il.Emit(OpCodes.Brfalse, original);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, index); il.Emit(OpCodes.Br, test);
        il.MarkLabel(loop);
        il.Emit(OpCodes.Ldloc, listeners); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Stloc, callback);
        il.Emit(OpCodes.Ldloc, callback);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(GhostDeathTrace), nameof(GhostDeathTrace.EnterCallback))); il.Emit(OpCodes.Stloc, scope);
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldloc, callback); il.Emit(OpCodes.Castclass, invoke.DeclaringType);
        for (var p = 1; p < parameters; p++) il.Emit(OpCodes.Ldarg, (short)p);
        il.Emit(OpCodes.Callvirt, invoke);
        il.BeginFinallyBlock(); il.Emit(OpCodes.Ldloca, scope);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(GhostDeathTrace.Scope), nameof(GhostDeathTrace.Scope.Dispose)));
        il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);
        il.MarkLabel(test); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldloc, listeners); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Blt, loop);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(original);
        for (var p = 0; p < parameters; p++) il.Emit(OpCodes.Ldarg, (short)p);
        il.Emit(OpCodes.Callvirt, invoke);
        il.MarkLabel(done);
    }
}
