using System;
using System.Reflection;
using EFT.Interactive;
using SPT.Reflection.Patching;

namespace Orbit.Inventory.Patches;

/// <summary>
/// Fires when an airdrop's landing animation completes — we surface the
/// fresh LootableContainer so the waypoint system can register it as a
/// runtime POI mid-raid.
/// </summary>
public class AirdropLandedPatch : ModulePatch
{
    public static Action<LootableContainer> OnAirdropLanded;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(AirdropLogicClass).GetMethod(nameof(AirdropLogicClass.method_0));
    }

    [PatchPostfix]
    public static void Postfix(AirdropLogicClass __instance)
    {
        var lootableContainer = __instance.AirdropSynchronizableObject_0.GetComponentInChildren<LootableContainer>();
        OnAirdropLanded?.Invoke(lootableContainer);
    }
}
