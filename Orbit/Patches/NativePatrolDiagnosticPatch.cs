using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

// Observe the original results. Never call the chooser again or change its decision.
public class NativePatrolArrivalDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(PatrolMoveSimple), nameof(PatrolMoveSimple.IsCome), Type.EmptyTypes);

    [PatchPostfix]
    public static void Postfix(BotOwner ____owner, bool __result)
        => NativePatrolDiagnostics.ArrivalChecked(____owner, __result);
}

public class NativeGlukharChoiceDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(PatrolPointChooserBossGluhar), nameof(PatrolPointChooserBossGluhar.FindNextPoint));

    [PatchPostfix]
    public static void Postfix(BotOwner ____owner, PatrolPointContainer __result)
        => NativePatrolDiagnostics.PointChosen(____owner, __result);
}
