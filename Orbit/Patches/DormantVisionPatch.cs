using System.Reflection;
using EFT;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Awake bots must never "see" a dormant one: the sleeper's GameObject is inactive (frozen pose, no
/// colliders), so a successful CheckLookEnemy would have every nearby bot raycasting and aiming at an
/// unhittable ghost. Mirrors Questing Bots' CheckLookEnemyPatch: while the target sleeps, force it
/// invisible for the checking enemy and skip the original entirely.
/// </summary>
public class DormantVisionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(EnemyInfo).GetMethod(nameof(EnemyInfo.CheckLookEnemy), BindingFlags.Public | BindingFlags.Instance);
    }

    [PatchPrefix]
    public static bool Prefix(EnemyInfo __instance)
    {
        var person = __instance?.Person;
        if (person == null || !DormancySystem.IsDormantProfile(person.ProfileId))
            return true;

        __instance.SetVisible(false);
        DormancySystem.VisionBlocks++;
        return false;
    }
}
