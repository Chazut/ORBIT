using System.Reflection;
using Comfort.Common;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

internal static class BypassGate
{
    public static bool ShouldBypassForOrbitBot(BaseLogicLayerAbstractClass layer)
    {
        var roster = Singleton<BotRoster>.Instance;
        return roster != null && roster.IsOrbitActive(layer.BotOwner_0);
    }
}

/// <summary>Disables BSG's AssaultEnemyFar for ORBIT bots — it overrides our cell dispatch at long range.</summary>
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

/// <summary>Disables BSG's Exfiltration layer for ORBIT bots — ExtractAction handles exfil routing.</summary>
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

/// <summary>Disables BSG's PtrlBirdEye for ORBIT bots — it breaks Goons squad cohesion.</summary>
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
