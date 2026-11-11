using System;
using System.Collections;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

// Issue #22, reported and investigated by Kaanori. DoorSystem owns registered
// bot/door pairs; native cleanup still owns all other interaction colliders.
internal static class DoorCollisionCleanup
{
    internal static readonly AccessTools.FieldRef<MovementContext, Collider[]> Colliders =
        AccessTools.FieldRefAccess<MovementContext, Collider[]>("_coroutineColliders");

    internal static void Restore(MovementContext context, Collider[] colliders)
        => Singleton<OrbitManager>.Instance?.DoorSystem?.ReconcileInteraction(
            context.CharacterController?.GetCollider(), colliders, "native-cleanup");

    internal static IEnumerator Wrap(MovementContext context, IEnumerator original)
    {
        try
        {
            while (true)
            {
                // Read at MoveNext, not when the coroutine is created: another
                // interaction may have replaced the native array during its wait.
                var affected = Colliders(context);
                bool more;
                try { more = original.MoveNext(); }
                finally { Restore(context, affected); }
                if (!more) yield break;
                yield return original.Current;
            }
        }
        finally { (original as IDisposable)?.Dispose(); }
    }
}

public sealed class DoorInteractionCollisionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(MovementContext).GetMethod(nameof(MovementContext.IgnoreInteractionCollision));

    [PatchPrefix]
    public static void Before(MovementContext __instance, bool ignore, out Collider[] __state)
        => __state = ignore ? null : DoorCollisionCleanup.Colliders(__instance);

    [PatchPostfix]
    public static void After(MovementContext __instance, Collider[] __state)
    {
        // The native method also restores the PREVIOUS array synchronously.
        if (__state != null) DoorCollisionCleanup.Restore(__instance, __state);
    }
}

public sealed class DoorInteractionCollisionEndPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(MovementContext).GetMethod(nameof(MovementContext.UnignoreCollision));

    [PatchPostfix]
    public static void After(MovementContext __instance, ref IEnumerator __result)
    {
        if (__result != null && Singleton<OrbitManager>.Instance?.DoorSystem != null)
            __result = DoorCollisionCleanup.Wrap(__instance, __result);
    }
}
