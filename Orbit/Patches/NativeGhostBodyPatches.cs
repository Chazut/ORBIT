using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Orbit.Systems;

namespace Orbit.Patches;

// Some native layers start these operations inside GetDecision, before the action guard runs.
internal static class NativeGhostBodyPatches
{
    private const string PatchId = "orbit.native-ghost.body";
    private static readonly Dictionary<MethodBase, Func<object, BotOwner>> Owners = new();
    internal static bool Ready { get; private set; }

    internal static void Enable()
    {
        if (Ready) return;
        var harmony = new Harmony(PatchId);
        try
        {
            Bind(harmony, typeof(BotReload), "TryReload", "Reload", "ReloadMagazine", "ReloadAmmo");
            Bind(harmony, typeof(BotWeaponSelector), "TryChangeToSlot");
            Bind(harmony, typeof(BotGrenadeController), "DoThrow");
            Bind(harmony, typeof(BotUnderbarrelLauncherController), "TryEnableReloadDisable", "TryEnable", "TryDisable", "TryReload");
            Bind(harmony, typeof(BotDoorOpener), "TryPassCurrentDoor", "RunEnteringDoorSequence",
                "WaitForDoorOpen", "ManualUpdate", "InteractionWithDoor");
            harmony.Patch(AccessTools.Method(typeof(BotDoorOpener), "Interact", new[] { typeof(Door), typeof(EInteractionType) }),
                prefix: new HarmonyMethod(typeof(NativeGhostBodyPatches), nameof(DoorInteractPrefix)));
            harmony.Patch(AccessTools.Method(typeof(BotDoorOpener), "UpdateDoorInteractionStatus", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(NativeGhostBodyPatches), nameof(DoorStatusPrefix)));
            Bind(harmony, typeof(BotLay), "TryLay");
            Bind(harmony, typeof(BotFirstAid), "TryApplyToCurrentPart", "ApplyToSelf");
            Bind(harmony, typeof(BotSurgicalKit), "ApplyToCurrentPart");
            Bind(harmony, typeof(BotStimulators), "StartApplyToTarget", "TryApply");
            Bind(harmony, typeof(PatrollingData), "ComeToPoint");
            Bind(harmony, typeof(PatrollingAlternative), "UpdateNodeByBrain");
            harmony.Patch(AccessTools.Method(typeof(PatrollingAlternative), "UpdateNodeByBrain"),
                transpiler: new HarmonyMethod(typeof(NativeGhostBodyPatches), nameof(GuardPatrolUpdate)));
            Bind(harmony, typeof(PatrolTakeItemsNode), "UpdateNodeByBrain");
            Bind(harmony, typeof(PatrolDropItemsNode), "UpdateNodeByBrain");
            Ready = true;
            Log.Info($"NATIVE GHOST: body guards ready ({Owners.Count + 2} entry points)");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            Owners.Clear();
            Log.Warning($"NATIVE GHOST: body guards unavailable, native sleep disabled: {e.Message}");
        }
    }

    private static void Bind(Harmony harmony, Type type, params string[] names)
    {
        var owner = AccessTools.Field(type, "_owner");
        if (owner?.FieldType != typeof(BotOwner)) throw new MissingFieldException(type.FullName, "_owner");
        var instance = Expression.Parameter(typeof(object), "instance");
        var getter = Expression.Lambda<Func<object, BotOwner>>(
            Expression.Field(Expression.Convert(instance, owner.DeclaringType), owner), instance).Compile();
        foreach (var name in names)
        {
            // Include engine overrides, so a specialized weapon selector cannot bypass the guard.
            var methods = type.Assembly.GetTypes().Where(type.IsAssignableFrom)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name == name && !m.IsAbstract).ToArray();
            if (methods.Length == 0) throw new MissingMethodException(type.FullName, name);
            foreach (var method in methods)
            {
                if (method.ReturnType != typeof(void) && method.ReturnType != typeof(bool))
                    throw new InvalidOperationException($"Unexpected body operation signature: {method}");
                Owners[method] = getter;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeGhostBodyPatches),
                    method.ReturnType == typeof(bool) ? nameof(BoolPrefix) : nameof(VoidPrefix)));
            }
        }
    }

    private static bool VoidPrefix(object __instance, MethodBase __originalMethod)
    {
        if (!NativeGhostSystem.HasSleepers) return true;
        if (__instance is BotDoorOpener opener)
            return !NativeGhostSystem.HandleDoorOperation(opener,
                __originalMethod.Name == "ManualUpdate" ? null : opener._currentDoorLink?.Door,
                __originalMethod.Name != "ManualUpdate" && opener._currentDoorLink == null, out _);
        var bot = Owners[__originalMethod](__instance);
        if (__instance is PatrollingData) return !NativeGhostPatrol.DeferArrival(bot);
        if (__instance is PatrollingAlternative) return !NativeGhostPatrol.DeferUpdate(bot);
        return !NativeGhostSystem.DeferBodyOperation(bot,
            __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
    }

    private static IEnumerable<CodeInstruction> GuardPatrolUpdate(IEnumerable<CodeInstruction> instructions)
    {
        var update = AccessTools.Method(typeof(AReserveWayAction), "ManualUpdate", new[] { typeof(BotOwner) });
        var guarded = AccessTools.Method(typeof(NativeGhostPatrol), nameof(NativeGhostPatrol.ManualUpdate));
        var result = instructions.ToList();
        var replaced = 0;
        foreach (var instruction in result)
        {
            if (!instruction.Calls(update)) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = guarded;
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Native patrol update shape changed");
        return result;
    }

    private static bool DoorInteractPrefix(BotDoorOpener __instance, Door __0, EInteractionType __1)
        => !NativeGhostSystem.HasSleepers
            || !NativeGhostSystem.HandleDoorOperation(__instance, __0, __1 != EInteractionType.Open, out _);

    private static bool DoorStatusPrefix(BotDoorOpener __instance, ref DoorInteractionStatus __result)
    {
        if (!NativeGhostSystem.HasSleepers || !NativeGhostSystem.HandleDoorOperation(__instance, null, false, out var waiting)) return true;
        __result = waiting ? DoorInteractionStatus.OpeningDoor : DoorInteractionStatus.CanRun;
        return false;
    }

    private static bool BoolPrefix(object __instance, MethodBase __originalMethod, ref bool __result)
    {
        if (VoidPrefix(__instance, __originalMethod)) return true;
        __result = false;
        return false;
    }
}
