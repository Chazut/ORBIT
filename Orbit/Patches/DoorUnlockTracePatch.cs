using System.Reflection;
using EFT.Interactive;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

/// <summary>
/// Logs every WorldInteractiveObject.Unlock(). Two trace lines for the same id on one frame mean the latch
/// coroutine ran twice (a cosmetic double-unlock).
/// </summary>
public class DoorUnlockTracePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(WorldInteractiveObject).GetMethod(nameof(WorldInteractiveObject.Unlock));

    [PatchPostfix]
    public static void Patch(WorldInteractiveObject __instance)
    {
        if (__instance == null) return;
        Log.Debug($"DoorUnlockTrace: {__instance.Id} Unlock() (frame {Time.frameCount})");
    }
}
