using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Patches;

// Unpause restores patrol bookkeeping but also submits a native route. That route can
// invoke BSG's failed-path teleport while ORBIT still owns this bot's movement.
internal static class LootPatrolResumePatch
{
    [ThreadStatic] private static BotOwner _resuming;
    internal static bool Ready { get; private set; }

    internal static void Enable()
    {
        if (Ready) return;
        var harmony = new Harmony("orbit.loot.patrol-resume");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(PatrollingData), nameof(PatrollingData.Unpause)),
                transpiler: new HarmonyMethod(typeof(LootPatrolResumePatch), nameof(GuardRoute)));
            Ready = true;
            Log.Info("LOOT PATROL: scoped resume guard ready");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            Log.Warning($"LOOT PATROL: resume guard unavailable; loot cleanup will keep native patrol paused: {e.Message}");
        }
    }

    internal static void Resume(BotOwner bot)
    {
        if (!Ready || bot == null) return;
        var previous = _resuming;
        _resuming = bot;
        try { bot.PatrollingData?.Unpause(); }
        finally { _resuming = previous; }
    }

    private static IEnumerable<CodeInstruction> GuardRoute(IEnumerable<CodeInstruction> instructions)
    {
        var target = AccessTools.Method(typeof(BotOwner), nameof(BotOwner.GoToPoint),
            new[] { typeof(Vector3), typeof(bool), typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) });
        var code = instructions.ToList();
        if (target == null || code.Count(instruction => instruction.Calls(target)) != 1)
            throw new InvalidOperationException("PatrollingData.Unpause route call shape changed");
        foreach (var instruction in code)
        {
            if (instruction.Calls(target))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(LootPatrolResumePatch), nameof(ResumeRoute));
            }
            yield return instruction;
        }
    }

    private static NavMeshPathStatus ResumeRoute(BotOwner bot, Vector3 position, bool slowAtTheEnd,
        float reachDist, bool getUpWithCheck, bool mustHaveWay, bool mustGetUp, bool onlyShortTrie, bool force)
    {
        if (ReferenceEquals(_resuming, bot))
        {
            Log.Debug($"LOOT PATROL: {bot.ProfileId} resumed patrol state without submitting a native route");
            return NavMeshPathStatus.PathInvalid;
        }
        return bot.GoToPoint(position, slowAtTheEnd, reachDist, getUpWithCheck, mustHaveWay, mustGetUp, onlyShortTrie, force);
    }
}
