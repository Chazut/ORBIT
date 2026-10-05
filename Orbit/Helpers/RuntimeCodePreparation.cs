using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

namespace Orbit.Helpers;

// Resolve managed entry points during loading only. No target method, constructor or event is invoked.
internal static class RuntimeCodePreparation
{
    private static readonly CodePreparationCache Cache = new();

    internal static void Include(MethodInfo method)
    {
        if (method?.DeclaringType?.Assembly == typeof(EFT.Player).Assembly) Cache.Include(method, 0);
    }

    internal static void IncludeCallback(MethodInfo method) => Cache.Include(method, 2);

    internal static void Prepare()
    {
        var result = Cache.Prepare();
        var detail = FormattableString.Invariant($"methods={result.Prepared} unavailable={result.Unavailable} elapsedMs={result.ElapsedMs:F2} cached={result.Cached} roots={result.Roots} callees={result.Callees} capped={result.Capped} unreadable={result.Unreadable}");
        Log.Info("PERF CODE PREPARE: " + detail);
        PerformanceJournal.Event("code-preparation", detail: detail);
    }
}

// Bounded breadth-first expansion keeps explicitly selected roots ahead of their helpers.
// Metadata caches contain no Player, bot component or event delegate references.
internal sealed class CodePreparationCache
{
    internal const int MaximumMethods = 384;
    private readonly Dictionary<MethodInfo, int> _pending = new();
    private readonly HashSet<MethodInfo> _attempted = new();
    private readonly Dictionary<MethodInfo, int> _expanded = new();
    private readonly HashSet<Assembly> _assemblies = new();
    private static readonly Dictionary<short, OpCode> Opcodes = CreateOpcodes();

    internal void Include(MethodInfo method, int depth)
    {
        if (!Eligible(method)) return;
        _assemblies.Add(method.DeclaringType.Assembly);
        if (!_pending.TryGetValue(method, out var previous) || depth > previous)
            _pending[method] = Math.Max(0, Math.Min(2, depth));
    }

    internal readonly struct Result(int prepared, int unavailable, int cached, int roots, int callees, int capped, int unreadable, double elapsedMs)
    {
        internal readonly int Prepared = prepared, Unavailable = unavailable, Cached = cached,
            Roots = roots, Callees = callees, Capped = capped, Unreadable = unreadable;
        internal readonly double ElapsedMs = elapsedMs;
    }

    internal Result Prepare()
    {
        var started = Stopwatch.GetTimestamp();
        var queue = new Queue<(MethodInfo Method, int Depth)>();
        var scheduled = new Dictionary<MethodInfo, int>();
        var capped = 0;
        foreach (var entry in _pending)
        {
            if (scheduled.Count >= MaximumMethods) { capped++; continue; }
            scheduled[entry.Key] = entry.Value; queue.Enqueue((entry.Key, entry.Value));
        }
        var roots = scheduled.Count;
        _pending.Clear();
        var prepared = 0; var unavailable = 0; var cached = 0; var unreadable = 0;
        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();
            if (_attempted.Add(method))
            {
                try
                {
                    // PrepareMethod/PrepareDelegate are empty in the shipped Mono mscorlib.
                    // GetFunctionPointer requests a compiled entry point without running the body.
                    if (method.MethodHandle.GetFunctionPointer() != IntPtr.Zero) prepared++;
                    else unavailable++;
                }
                catch (Exception) { unavailable++; }
            }
            else cached++;

            if (depth <= 0 || _expanded.TryGetValue(method, out var previous) && previous >= depth) continue;
            _expanded[method] = depth;
            try
            {
                foreach (var called in Calls(method))
                {
                    if (!Eligible(called) || !_assemblies.Contains(called.DeclaringType.Assembly)) continue;
                    if (scheduled.TryGetValue(called, out var oldDepth) && oldDepth >= depth - 1) continue;
                    if (!scheduled.ContainsKey(called) && scheduled.Count >= MaximumMethods) { capped++; continue; }
                    scheduled[called] = depth - 1; queue.Enqueue((called, depth - 1));
                }
            }
            catch (Exception) { unreadable++; }
        }
        return new(prepared, unavailable, cached, roots, scheduled.Count - roots, capped, unreadable,
            (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);
    }

    private static bool Eligible(MethodInfo method)
    {
        try
        {
            return method != null && method is not DynamicMethod && method.DeclaringType != null
                && !method.IsAbstract && !method.ContainsGenericParameters && !method.DeclaringType.Assembly.IsDynamic
                && (method.Attributes & MethodAttributes.PinvokeImpl) == 0
                && (method.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Native | MethodImplAttributes.Runtime)) == 0;
        }
        catch (Exception) { return false; }
    }

    private static Dictionary<short, OpCode> CreateOpcodes()
    {
        var result = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.FieldType == typeof(OpCode)) { var code = (OpCode)field.GetValue(null); result[code.Value] = code; }
        return result;
    }

    private static IEnumerable<MethodInfo> Calls(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null || il.Length > 65536) yield break;
        var typeArgs = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var index = 0; index < il.Length;)
        {
            short value = il[index++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[index++]));
            if (!Opcodes.TryGetValue(value, out var code)) yield break;
            if (code.OperandType == OperandType.InlineMethod
                && (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn))
            {
                MethodInfo target = null;
                try { target = method.Module.ResolveMethod(BitConverter.ToInt32(il, index), typeArgs, methodArgs) as MethodInfo; }
                catch (Exception) { }
                if (target != null) yield return target;
            }
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(il, index)),
                _ => 4
            };
            index = checked(index + size);
        }
    }
}
