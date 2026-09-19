using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Throttles the brain tick of dormant bots. BSG's AICoreController updates every bot's AICoreAgent every
/// frame regardless of BotState (an agent is only unregistered when the brain is disposed, i.e. at death),
/// and BigBrain's prefix on AICoreAgent.Update replaces that tick with its layer sweep: every custom
/// layer's IsActive, then the active action's update. ORBIT needs that tick to keep running on ghosts
/// (ghost looting and objective progress ride the BigBrain actions in DormantMode), just not at 60 Hz.
///
/// A second prefix on AICoreAgent.Update cannot short-circuit BigBrain's (Harmony runs every prefix), so
/// this patches BigBrain's prefix itself and lets a dormant agent through on one frame out of
/// <see cref="DormancySystem.DormantBrainTickDivisor"/>, staggered per agent so the survivors of the
/// skip never bunch up on the same frame.
/// </summary>
public class DormantBrainThrottlePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var bigBrainPatch = AccessTools.TypeByName("DrakiaXYZ.BigBrain.Patches.BotAgentUpdatePatch");
        return bigBrainPatch == null ? null : AccessTools.Method(bigBrainPatch, "PatchPrefix");
    }

    // The target is a static method whose first parameter is the agent (BigBrain names it __instance,
    // which Harmony would treat as an injection here), so it is read by position instead.
    [PatchPrefix]
    public static bool Prefix(AICoreAgent<BotLogicDecision> __0, ref bool __result)
    {
        if (NativeGhostSystem.ScheduleBrain(__0, out var skip))
        {
            if (!skip) return true;
            __result = false;
            return false;
        }
        if (!DormancySystem.ShouldSkipBrainTick(__0)) return true;
        __result = false; // same verdict BigBrain's prefix returns: the original Update never runs
        return false;
    }

    [PatchFinalizer]
    public static System.Exception Finalizer(AICoreAgent<BotLogicDecision> __0, System.Exception __exception)
        => NativeGhostSystem.HandleBrainException(__0, __exception) ? null : __exception;
}
