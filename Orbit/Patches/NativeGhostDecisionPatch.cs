using System.Reflection;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

public class NativeGhostDecisionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(AICoreStrategy<BotLogicDecision>), "Update");

    [PatchPostfix]
    public static void Postfix(AICoreStrategy<BotLogicDecision> __instance,
        ref AICoreActionResult<BotLogicDecision, CoreActionResultParams>? __result)
        => NativeGhostSystem.GuardDecision(__instance, ref __result);
}
