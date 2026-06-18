using System.Reflection;
using EFT.Interactive;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

/// <summary>
/// TEMP diagnostic (1.2.0-pre — remove once the door-unlock fix is validated). Logs every
/// WorldInteractiveObject.Unlock() call with the object id + frame. The carver-decoupling fix drives a
/// key-in-lock animation via the Unlock interaction AND keeps an explicit door.Unlock() floor; if BOTH fire
/// for a single arrival the latch coroutine runs twice (cosmetic double-click that is near-impossible to spot
/// in freecam). Two trace lines for the same id on the same frame = the double, easy to grep / tile.
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
