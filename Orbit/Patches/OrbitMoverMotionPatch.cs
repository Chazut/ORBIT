using System.Reflection;
using Comfort.Common;
using Orbit.Core;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

// ManualFixedUpdate is not the only native movement entry point: the animator invokes this
// callback too. Its stale route must not repair/teleport a body currently driven by ORBIT.
public class OrbitMoverMotionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(BotMoverImpostor).GetMethod(nameof(BotMoverImpostor.OnMotionApplied));

    [PatchPrefix]
    public static bool Prefix(BotMoverImpostor __instance, CollisionFlags flags)
    {
        var bot = __instance._owner;
        var agent = Singleton<BotRoster>.Instance?.GetAgent(bot);
        if (agent == null || !ReferenceEquals(agent.Bot, bot) || !agent.IsActive) return true;
        // Preserve the callback's collision bookkeeping and its normal no-native-path state.
        __instance._isImpostorWorks = false;
        __instance._lastFlags = flags;
        return false;
    }
}
