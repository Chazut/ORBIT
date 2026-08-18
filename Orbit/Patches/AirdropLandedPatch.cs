using System;
using System.Reflection;
using EFT.Airdrop;
using EFT.Interactive;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Fires when an airdrop's landing animation completes — we surface the fresh LootableContainer so the
/// waypoint system can register it as a runtime POI mid-raid.
/// </summary>
public class AirdropLandedPatch : ModulePatch
{
    public static Action<LootableContainer> OnAirdropLanded;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ClientAirDrop).GetMethod(nameof(ClientAirDrop.PlayLandingSound));
    }

    [PatchPostfix]
    public static void Postfix(ClientAirDrop __instance)
    {
        var lootableContainer = __instance._syncObject.GetComponentInChildren<LootableContainer>();
        OnAirdropLanded?.Invoke(lootableContainer);
    }
}
