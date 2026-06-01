using System.Reflection;
using Comfort.Common;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Returns true when the given BSG layer instance belongs to a bot that
/// ORBIT currently manages, so the bypass should fire. For every other
/// bot (vanilla scavs / goons running BSG behaviour, faction-mod bots
/// not taken over, etc.) the original BSG layer is left alone.
/// </summary>
internal static class BypassGate
{
    public static bool ShouldBypassForOrbitBot(BaseLogicLayerAbstractClass layer)
    {
        var roster = Singleton<BotRoster>.Instance;
        return roster != null && roster.IsOrbitActive(layer.BotOwner_0);
    }
}

/// <summary>
/// Disables BSG's "AssaultEnemyFar" layer for ORBIT-managed bots — it
/// kicks in at long range and hijacks scavs away from our cell dispatch.
/// Non-ORBIT bots keep the original behaviour.
/// </summary>
public class AssaultEnemyFarBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GClass45), nameof(GClass45.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(GClass45 __instance, ref bool __result)
    {
        if (!BypassGate.ShouldBypassForOrbitBot(__instance)) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// Disables BSG's "Exfiltration" layer for ORBIT-managed bots — it runs
/// at priority 79, hijacks the brain mid-tick, and frequently leaves
/// bots stuck around exfil triggers. ExtractAction handles exfil routing
/// instead. Non-ORBIT bots keep the original behaviour.
/// </summary>
public class ExfilLayerBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GClass75), nameof(GClass75.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(GClass75 __instance, ref bool __result)
    {
        if (!BypassGate.ShouldBypassForOrbitBot(__instance)) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// Disables BSG's "PtrlBirdEye" layer for ORBIT-managed bots — it splits
/// Bird Eye away from the rest of the Goons during long-range scanning,
/// breaking squad cohesion. Non-ORBIT bots keep the original behaviour.
/// </summary>
public class PtrlBirdEyeBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GClass79), nameof(GClass79.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(GClass79 __instance, ref bool __result)
    {
        if (!BypassGate.ShouldBypassForOrbitBot(__instance)) return true;
        __result = false;
        return false;
    }
}
