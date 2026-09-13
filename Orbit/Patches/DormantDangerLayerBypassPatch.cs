using System.Reflection;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// BSG's AvoidDanger layer (priority 80) fires when a bot has a grenade, a mine or the BTR within reach
/// (BotBewareBTR.ShallRunAway inside AVOID_BTR_RADIUS), and BigBrain still sweeps BSG layers on dormant
/// bots. A sleeper it hijacks cannot run anywhere (inactive body, BotState NonActive): ORBIT's layer is
/// pushed out for as long as the danger lasts, the ghost walk stops and the bot freezes where it stands.
/// Woods raid: the BTR drove past a sleeping PMC at 2m and parked 25m away, the bot never moved again
/// (AdeknieJadek, 15 minutes). While a bot is dormant the layer stays off; once it wakes the layer works
/// exactly as before.
/// </summary>
internal static class DormantLayerGate
{
    public static bool OwnerIsDormant(BaseLogicLayerSimple layer)
    {
        try
        {
            var profileId = layer?._owner?.GetPlayer?.ProfileId;
            return profileId != null && DormancySystem.IsDormantProfile(profileId);
        }
        catch
        {
            return false;
        }
    }
}

public class DormantAvoidDangerBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(AvoidDangerLayer), nameof(AvoidDangerLayer.ShallUseNow));

    [PatchPrefix]
    public static bool Prefix(AvoidDangerLayer __instance, ref bool __result)
    {
        if (!DormantLayerGate.OwnerIsDormant(__instance)) return true;
        __result = false;
        return false;
    }
}

/// <summary>Same gate for the Boar variant of the layer (Kaban's guards run their own copy).</summary>
public class DormantBoarAvoidDangerBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BoarAvoidDangerLayer), nameof(BoarAvoidDangerLayer.ShallUseNow));

    [PatchPrefix]
    public static bool Prefix(BoarAvoidDangerLayer __instance, ref bool __result)
    {
        if (!DormantLayerGate.OwnerIsDormant(__instance)) return true;
        __result = false;
        return false;
    }
}
