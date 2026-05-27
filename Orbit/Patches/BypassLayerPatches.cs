using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Disables BSG's "AssaultEnemyFar" layer — it kicks in at long range and
/// hijacks scavs away from our cell dispatch.
/// </summary>
public class AssaultEnemyFarBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GClass45), nameof(GClass45.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(ref bool __result)
    {
        __result = false;
        return false;
    }
}

/// <summary>
/// Disables BSG's "Exfiltration" layer — it runs at priority 79, hijacks
/// the brain mid-tick, and frequently leaves bots stuck around exfil
/// triggers. ExtractAction handles exfil routing instead.
/// </summary>
public class ExfilLayerBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GClass75), nameof(GClass75.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(ref bool __result)
    {
        __result = false;
        return false;
    }
}

/// <summary>
/// Disables BSG's "PtrlBirdEye" layer — it splits Bird Eye away from the
/// rest of the Goons during long-range scanning, breaking squad cohesion.
/// </summary>
public class PtrlBirdEyeBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GClass79), nameof(GClass79.ShallUseNow));
    }

    [PatchPrefix]
    public static bool Patch(ref bool __result)
    {
        __result = false;
        return false;
    }
}
