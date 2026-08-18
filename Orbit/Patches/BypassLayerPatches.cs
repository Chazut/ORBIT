using System.Reflection;
using Comfort.Common;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

internal static class BypassGate
{
    public static bool ShouldBypassForOrbitBot(BaseLogicLayerSimple layer)
    {
        var roster = Singleton<BotRoster>.Instance;
        return roster != null && roster.IsOrbitActive(layer._owner);
    }
}

/// <summary>Disables BSG's AssaultEnemyFar for ORBIT bots — it overrides our cell dispatch at long range.</summary>
public class AssaultEnemyFarBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(AssaultEnemyFarLayer), nameof(AssaultEnemyFarLayer.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(AssaultEnemyFarLayer __instance, ref bool __result)
    {
        if (!BypassGate.ShouldBypassForOrbitBot(__instance)) return true;
        __result = false;
        return false;
    }
}

/// <summary>Disables BSG's Exfiltration layer for ORBIT bots — ExtractAction handles exfil routing.</summary>
public class ExfilLayerBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ExfiltrationLayer), nameof(ExfiltrationLayer.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(ExfiltrationLayer __instance, ref bool __result)
    {
        if (!BypassGate.ShouldBypassForOrbitBot(__instance)) return true;
        __result = false;
        return false;
    }
}

/// <summary>Disables BSG's PtrlBirdEye for ORBIT bots — it breaks Goons squad cohesion.</summary>
public class PtrlBirdEyeBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BirdEyePatrolLayer), nameof(BirdEyePatrolLayer.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(BirdEyePatrolLayer __instance, ref bool __result)
    {
        if (!BypassGate.ShouldBypassForOrbitBot(__instance)) return true;
        __result = false;
        return false;
    }
}
