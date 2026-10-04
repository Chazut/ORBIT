using System;
using System.Collections.Generic;
using System.Diagnostics;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Helpers;

internal static class GhostWakeActivationDiagnostics
{
    private struct Pending
    {
        internal long Started;
        internal double TotalMs, MaxMs;
        internal int Calls, PeakFrame;
    }
    private static readonly Dictionary<BotOwner, Pending> PendingBots = new();
    internal static void Reset() => PendingBots.Clear();
    internal static void Forget(BotOwner bot) { if (bot != null) PendingBots.Remove(bot); }
    internal static void Track(BotOwner bot)
    {
        if (bot != null && Plugin.PerfLogging is { Value: true })
            PendingBots[bot] = new Pending { Started = Stopwatch.GetTimestamp() };
    }

    internal static void Enable()
    {
        try
        {
            new Harmony("orbit.ghost-wake.activation-timing").Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual)),
                prefix: new HarmonyMethod(typeof(GhostWakeActivationDiagnostics), nameof(Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(GhostWakeActivationDiagnostics), nameof(Finalizer)) { priority = Priority.Last });
        }
        catch (Exception e) { Log.Warning("Ghost activation timing unavailable: " + e.Message); }
    }

    private static void Prefix(BotOwner __instance, out long __state)
    {
        __state = 0;
        if (PendingBots.Count == 0 || !PendingBots.ContainsKey(__instance)) return;
        if (Plugin.PerfLogging is not { Value: true } || __instance.IsDead) { Forget(__instance); return; }
        if (__instance.BotState != EBotState.PreActive)
        {
            if (__instance.BotState != EBotState.NonActive) Forget(__instance);
            return;
        }
        __state = Stopwatch.GetTimestamp();
    }

    private static Exception Finalizer(BotOwner __instance, long __state, Exception __exception)
    {
        if (__state == 0 || !PendingBots.TryGetValue(__instance, out var pending)) return __exception;
        var now = Stopwatch.GetTimestamp();
        var ms = (now - __state) * 1000d / Stopwatch.Frequency;
        TransitionPerformance.RecordExternal(TransitionPhase.GhostActivation, now - __state, Time.frameCount);
        pending.Calls++; pending.TotalMs += ms;
        if (ms >= pending.MaxMs) { pending.MaxMs = ms; pending.PeakFrame = Time.frameCount; }
        var ageMs = (now - pending.Started) * 1000d / Stopwatch.Frequency;
        if (__instance.BotState == EBotState.PreActive && __exception == null && ageMs < 5000)
        {
            PendingBots[__instance] = pending;
            return __exception;
        }
        PendingBots.Remove(__instance);
        DiagnosticCapture.WakeActivation(__instance.ProfileId, __instance.BotState.ToString(), pending.Calls,
            pending.TotalMs, pending.MaxMs, pending.PeakFrame, ageMs);
        return __exception;
    }
}
