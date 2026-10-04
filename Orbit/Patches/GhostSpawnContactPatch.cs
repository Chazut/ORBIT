using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

// Record preparation separately from the first Active state. PostActivate also runs on wakes.
public class GhostSpawnRegistrationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotOwner), nameof(BotOwner.PreActivate));
    [PatchPrefix]
    public static void Prefix(BotOwner __instance)
    {
        Orbit.Helpers.PerformanceJournal.Event("spawn-preactivate", __instance?.ProfileId);
        Singleton<OrbitManager>.Instance?.DormancySystem.RegisterSpawn(__instance);
    }
}

public class GhostSpawnActivationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.PropertySetter(typeof(BotOwner), nameof(BotOwner.BotState));

    [PatchPrefix]
    public static void Prefix(BotOwner __instance, EBotState value)
    {
        if (value == EBotState.Active)
        {
            Orbit.Helpers.PerformanceJournal.Event("body-active", __instance?.ProfileId);
            Singleton<OrbitManager>.Instance?.DormancySystem.RegisterFirstActivation(__instance);
        }
    }
}

public class GhostSpawnContactPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.PropertySetter(AccessTools.Field(typeof(BotOwner), nameof(BotOwner.Memory)).FieldType, "GoalEnemy");

    [PatchPrefix]
    public static bool Prefix(EnemyInfo value)
        => value == null || Singleton<OrbitManager>.Instance?.DormancySystem.DeferSpawnContact(value) != true;
}

// Also cover direct visibility writes before SetVisible marks the contact seen and
// synchronously calls CalcGoalForBot. SAIN normally reaches the CheckLookEnemy guard.
public class GhostSpawnFirstSightPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(EnemyInfo), nameof(EnemyInfo.SetVisible), new[] { typeof(bool) });

    [PatchPrefix]
    public static bool Prefix(EnemyInfo __instance, bool value)
    {
        if (!value || Singleton<OrbitManager>.Instance?.DormancySystem
                .DeferSpawnContact(__instance, firstSight: true, source: "first-sight") != true) return true;
        __instance.SetCanShoot(false);
        return false;
    }
}
