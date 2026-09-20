using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>Preserves ISB's external scheduler and mission eligibility during native Ghost sleep.</summary>
internal static class NativeGhostIsb
{
    private const string PatchId = "orbit.native-ghost.isb";
    private static bool _resolved;
    private static Type _agentType;
    private static Func<object, BotOwner> _owner;
    private static Func<object, bool> _mission, _running, _hasDestination;
    private static Func<object, Vector3> _destination;
    internal static bool Ready { get; private set; }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        _agentType = AccessTools.TypeByName("ISBSpecialForces.Behavior.BlackDivision.ISBTacticalAgent");
        if (_agentType == null) return;
        var harmony = new Harmony(PatchId);
        try
        {
            _owner = Getter<BotOwner>("Bot");
            _hasDestination = Getter<bool>("HasDestination");
            _destination = Getter<Vector3>("Destination");
            _running = Getter<bool>("_running");
            _mission = Getter<bool>("CanRunMission");
            var camera = AccessTools.TypeByName("ISBSpecialForces.Components.ISBCameraHuntController");
            var alive = AccessTools.PropertyGetter(_agentType, "Alive");
            var eligible = AccessTools.Method(camera, "EligibleHunter", new[] { typeof(BotOwner) });
            var disable = AccessTools.Method(_agentType, "OnDisable", Type.EmptyTypes);
            if (alive == null || eligible == null || disable == null) throw new MissingMethodException("ISB lifecycle contract");
            harmony.Patch(alive, transpiler: new HarmonyMethod(typeof(NativeGhostIsb), nameof(PreserveActiveChecks)));
            harmony.Patch(eligible, transpiler: new HarmonyMethod(typeof(NativeGhostIsb), nameof(PreserveActiveChecks)));
            harmony.Patch(disable, prefix: new HarmonyMethod(typeof(NativeGhostIsb), nameof(BeforeDisable)));
            Ready = true;
            Log.Info("NATIVE GHOST: ISB lifecycle bridge ready (tactics, hunt and camera hunt)");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            Log.Warning($"NATIVE GHOST: ISB lifecycle bridge unavailable: {e.Message}");
        }
    }

    private static Func<object, T> Getter<T>(string name)
    {
        var parameter = Expression.Parameter(typeof(object), "instance");
        var instance = Expression.Convert(parameter, _agentType);
        var field = AccessTools.Field(_agentType, name);
        var method = AccessTools.PropertyGetter(_agentType, name) ?? AccessTools.Method(_agentType, name, Type.EmptyTypes);
        Expression value = field != null ? Expression.Field(instance, field)
            : method != null ? Expression.Call(instance, method) : throw new MissingMemberException(_agentType.FullName, name);
        return Expression.Lambda<Func<object, T>>(value, parameter).Compile();
    }

    private static IEnumerable<CodeInstruction> PreserveActiveChecks(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var active = AccessTools.PropertyGetter(typeof(Behaviour), nameof(Behaviour.isActiveAndEnabled));
        var matches = code.Where(c => c.Calls(active)).ToArray();
        var expected = __originalMethod.Name == "get_Alive" ? 2 : 1;
        if (matches.Length != expected) throw new InvalidOperationException($"ISB {__originalMethod.Name}: expected {expected} active checks, found {matches.Length}");
        foreach (var instruction in matches)
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(NativeGhostIsb), nameof(ActiveForMission));
        }
        return code;
    }

    // All other original health, role, manager, contact and mission checks remain in place.
    private static bool ActiveForMission(Behaviour component)
    {
        if (component.isActiveAndEnabled) return true;
        if (!Ready || !component.enabled) return false;
        if (component is BotOwner bot) return NativeGhostSystem.RetainsNativeState(bot);
        return _agentType.IsInstanceOfType(component) && _owner(component) is { } owner
            && ReferenceEquals(owner.GetComponent(_agentType), component) && NativeGhostSystem.RetainsNativeState(owner);
    }

    private static bool BeforeDisable(object __instance)
        => !Ready || __instance is not Behaviour { enabled: true } || _owner(__instance) is not { } bot
            || !ReferenceEquals(bot.GetComponent(_agentType), __instance) || !NativeGhostSystem.RetainsNativeState(bot);

    internal static object Agent(BotOwner bot)
    {
        Resolve();
        return _agentType == null ? null : bot.GetComponent(_agentType);
    }

    internal static bool Valid(BotOwner bot, object agent)
        => Ready && agent is Behaviour { enabled: true } && ReferenceEquals(Agent(bot), agent) && ReferenceEquals(_owner(agent), bot);

    internal static string BodyReason(BotOwner bot)
    {
        var agent = Agent(bot);
        return agent != null && !Valid(bot, agent) ? "isb-lifecycle-bridge" : null;
    }

    internal static bool CanRetainEnemy(BotOwner bot)
    {
        if (!NativeGhostPartisan.IsDistantMemory(bot)) return false;
        var action = bot.Brain?.LastDecision;
        if (!action.HasValue) return false;
        var name = NativeGhostSystem.ActionName(action.Value);
        if (name != "ISBSpecialForces.Behavior.BlackDivision.ISBTacticalMove"
            && name is not ("MoreBotsAPI.Behavior.Actions.HuntTargetAction" or
                "MoreBotsAPI.Behavior.Actions.HuntRegroupAction" or "MoreBotsAPI.Behavior.Actions.SearchForTargetAction")) return false;
        var agent = Agent(bot);
        try { return agent != null && Valid(bot, agent) && _mission(agent); }
        catch { return false; }
    }

    internal static void ReissueOrder(BotOwner bot, object agent)
    {
        // Replay the destination already chosen by ISB without restarting its movement deadline.
        if (!NativeGhostSystem.OwnsInactiveMovement(bot) || !Valid(bot, agent) || !_running(agent) || !_hasDestination(agent)
            || !bot.Brain.LastDecision.HasValue
            || NativeGhostSystem.ActionName(bot.Brain.LastDecision.Value) != "ISBSpecialForces.Behavior.BlackDivision.ISBTacticalMove") return;
        bot.Mover.GoToPoint(_destination(agent), true, -1f, false, true, true, false);
    }
}
