using System;
using System.Reflection;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

internal sealed class SniperContactRangePatch : ModulePatch
{
    private static bool _failed;
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(EnemyInfo), nameof(EnemyInfo.GetAdditionalSensorDistance));

    [PatchPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(EnemyInfo __instance, ref float __result)
    {
        if (_failed) return;
        try { SniperContactRange.Extend(__instance, ref __result); }
        catch (Exception error)
        {
            _failed = true;
            Log.Warning($"SNIPER TRACK: range integration disabled, ordinary vision retained: {error.Message}");
        }
    }
}
