using System.Reflection;
using Comfort.Common;
using Orbit.Core;
using Orbit.Systems;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

// Guard the destination selection itself: skipping Teleport would still let SetPlayerToNavMesh
// feed the rejected destination into SetPosition and the next movement tick.
public class OrbitMoverHandoffFallbackPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(BotMover).GetMethod(nameof(BotMover.FindBetterPosition));

    internal static OrbitMovementRecovery Pending(BotMover mover)
    {
        var bot = mover?._owner;
        if (bot == null || bot.IsDead) return null;
        var agent = Singleton<BotRoster>.Instance?.GetAgent(bot);
        return agent != null && ReferenceEquals(agent.Bot, bot) && !agent.IsActive && !agent.IsDormant
            && agent.Stuck.Recovery.HandoffPending ? agent.Stuck.Recovery : null;
    }

    [PatchPrefix]
    public static bool Prefix(BotMover __instance, ref Vector3 __result)
    {
        var recovery = Pending(__instance);
        if (recovery == null) return true;
        __result = recovery.HandoffFallback(__instance);
        return false;
    }
}

public class OrbitMoverHandoffLinkedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(BotMover).GetMethod(nameof(BotMover.SetPlayerToNavMesh));

    [PatchPostfix]
    public static void Postfix(BotMover __instance, EBotLinkResult __result)
        => OrbitMoverHandoffFallbackPatch.Pending(__instance)?.ConfirmNativeLink(__instance, __result);
}
