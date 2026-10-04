using System;
using System.Reflection;
using EFT;
using EFT.Airdrop;
using EFT.Interactive;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

/// <summary>Announces the host's actual release, with a ground estimate instead of the airborne crate height.</summary>
public class AirdropReleasedPatch : ModulePatch
{
    internal static event Action<LootableContainer, Vector3> OnAirdropReleased;

    protected override MethodBase GetTargetMethod() => typeof(ServerPlane).GetMethod(nameof(ServerPlane.AirdropReached));

    [PatchPostfix]
    private static void Postfix(ServerPlane __instance)
    {
        if (OnAirdropReleased == null || __instance?._airdrop == null) return;
        try
        {
            var container = __instance._airdrop.GetComponentInChildren<LootableContainer>();
            if (container == null) return;
            // One ground ray per drop. Landing later replaces this estimate with the actual position.
            var origin = __instance._airdrop.transform.position + Vector3.down * 5f;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 2000f,
                    LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
            {
                Log.Warning("AMBUSH: airdrop release has no ground estimate; waiting for landing");
                return;
            }
            OnAirdropReleased?.Invoke(container, hit.point);
        }
        catch (Exception ex) { Log.Warning($"AMBUSH: airdrop release notification failed: {ex.Message}"); }
    }
}
