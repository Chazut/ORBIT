using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Patches;

// The native path controller retains only the last corner, which can be short of the requested goal.
// Keep the original order while using the navmesh directly for an inactive body's movement.
public class NativeGhostMoveOrderPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotMover), nameof(BotMover.GoToPoint), new[]
            { typeof(Vector3), typeof(bool), typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(bool) });

    [PatchPrefix]
    public static bool Prefix(BotMover __instance, Vector3 __0, float __2, ref NavMeshPathStatus __result)
    {
        if (!NativeGhostSystem.TryMoveOrder(__instance, __0, __2, out var status)) return true;
        __result = status;
        return false;
    }

    [PatchPostfix]
    public static void Postfix(BotMover __instance, Vector3 __0, NavMeshPathStatus __result)
        => NativeGhostSystem.RecordMoveOrder(__instance, __0, __result, "go-to-point");
}

public class NativeGhostPointOrderPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotMover), nameof(BotMover.GoToPoint), new[]
            { typeof(CustomNavigationPoint), typeof(bool), typeof(bool) });

    [PatchPrefix]
    public static bool Prefix(BotMover __instance, CustomNavigationPoint __0, ref NavMeshPathStatus __result)
    {
        if (__0 == null || !NativeGhostSystem.TryMoveOrder(__instance, __0.Position, 0.5f, out var status)) return true;
        __result = status;
        return false;
    }

    [PatchPostfix]
    public static void Postfix(BotMover __instance, CustomNavigationPoint __0, NavMeshPathStatus __result)
    {
        if (__0 != null) NativeGhostSystem.RecordMoveOrder(__instance, __0.Position, __result, "go-to-cover");
    }
}

public class NativeGhostWayOrderPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotMover), nameof(BotMover.GoToByWay), new[] { typeof(Vector3[]), typeof(float) });

    [PatchPrefix]
    public static bool Prefix(BotMover __instance, Vector3[] __0, float __1)
        => NativeGhostSystem.AllowWayOrder(__instance, __0, __1);

    [PatchPostfix]
    public static void Postfix(BotMover __instance, Vector3[] __0, float __1)
        => NativeGhostSystem.RetainWayOrder(__instance, __0, __1);
}

// The original goal is lost when native zigzag navigation hands the mover only its corners.
// Keep new routes scoped to this call; repeated goals use the existing controller and its backoff.
public class NativeGhostZigzagGoalPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotRun), nameof(BotRun.CalcPathToMoveZigZagAndGo),
            new[] { typeof(Vector3), typeof(BotOwner), typeof(EZigZAgType) });

    [PatchPrefix]
    private static bool Prefix(Vector3 __0, BotOwner __1, ref bool __result, out NativeGhostOrders.GoalScope __state)
    {
        __state = null;
        if (NativeGhostSystem.TryRepeatGoal(__1?.Mover, __0, out var success))
        {
            __result = success;
            return false;
        }
        __state = NativeGhostOrders.BeginGoal(__1?.Mover, __0);
        return true;
    }

    [PatchPostfix]
    private static void Postfix(Vector3 __0, BotOwner __1, ref bool __result)
    {
        // Native zigzag can fail before issuing any corners. Retain that goal as well so
        // subsequent point/cover fallbacks share its bounded retry state.
        if (!__result && __1 != null && NativeGhostSystem.TryMoveOrder(__1.Mover, __0, -1f, out var status))
            __result = status == NavMeshPathStatus.PathComplete;
    }

    [PatchFinalizer]
    private static Exception Finalizer(Exception __exception, NativeGhostOrders.GoalScope __state)
    {
        NativeGhostOrders.EndGoal(__state);
        return __exception;
    }
}

// Tactical movement tightens its arrival radius after issuing GoToPoint.
// Keep the retained order in sync so it cannot complete before the native action does.
public class NativeGhostReachDistancePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotMover), nameof(BotMover.SetReachDist), new[] { typeof(float) });

    [PatchPrefix]
    public static void Prefix(BotMover __instance, float __0)
        => NativeGhostSystem.SetReachDistance(__instance, __0);
}
