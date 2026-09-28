using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

// Record the first activation, never PostActivate (which also runs on every Ghost wake).
public class GhostSpawnRegistrationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotOwner), nameof(BotOwner.PreActivate));
    [PatchPrefix]
    public static void Prefix(BotOwner __instance)
        => Singleton<OrbitManager>.Instance?.DormancySystem.RegisterSpawn(__instance);
}

public class GhostSpawnContactPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.PropertySetter(AccessTools.Field(typeof(BotOwner), nameof(BotOwner.Memory)).FieldType, "GoalEnemy");

    [PatchPrefix]
    public static bool Prefix(EnemyInfo value)
        => value == null || Singleton<OrbitManager>.Instance?.DormancySystem.DeferSpawnContact(value) != true;
}
