using System.Reflection;
using EFT;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

// Release before reassignment/pooling, so a later owner never inherits a Ghost visual mask.
internal sealed class GhostWeaponParentPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(WeaponPrefab).GetMethod(nameof(WeaponPrefab.Parent));
    [PatchPrefix]
    public static void Prefix(WeaponPrefab __instance) => GhostWeaponVisuals.Release(__instance.gameObject);
    [PatchPostfix]
    public static void Postfix(WeaponPrefab __instance, IPlayer player)
    {
        if (player is Player owner && DormancySystem.IsDormantProfile(owner.ProfileId))
            GhostWeaponVisuals.Hide(owner, __instance.gameObject);
    }
}

internal sealed class GhostWeaponPoolPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(WeaponPrefab).GetMethod(nameof(WeaponPrefab.ReturnToPool));
    [PatchPrefix]
    public static void Prefix(WeaponPrefab __instance) => GhostWeaponVisuals.Release(__instance.gameObject);
}
