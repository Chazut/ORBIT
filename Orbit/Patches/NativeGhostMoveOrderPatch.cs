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
    public static bool Prefix(BotMover __instance, Vector3[] __0)
        => NativeGhostSystem.AllowWayOrder(__instance, __0);

    [PatchPostfix]
    public static void Postfix(BotMover __instance, Vector3[] __0, float __1)
        => NativeGhostSystem.RetainWayOrder(__instance, __0, __1);
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
