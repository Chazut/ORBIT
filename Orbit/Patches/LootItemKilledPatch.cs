using System.Reflection;
using Comfort.Common;
using EFT.Interactive;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Drops the LooseLoot waypoint of an item the moment its world object is killed, whoever took it.
/// <see cref="InventoryChangePatch"/> only sees pickups that reach Player.OnItemAddedOrRemoved, and Fika's
/// ObservedPlayer overrides that method with an empty body: an item grabbed by another player in a co-op
/// raid kept its waypoint in the grid. BSG then returns the LootItem to the asset pool, and the pool restores
/// the component's original state (Item back to null), so every bot sent to the spot found a live GameObject
/// with nothing in it and re-ran the loot session every frame (Nekofur's Customs raid: three bots on one
/// key, tens of thousands of "lootItem.Item is null" warnings). Kill runs on every removal path: pickup by
/// anyone, another mod destroying loot, raid teardown (harmless, the manager is gone by then).
/// </summary>
public class LootItemKilledPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LootItem).GetMethod(nameof(LootItem.Kill), BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPrefix]
    public static void Prefix(LootItem __instance)
    {
        var itemId = __instance?.ItemId;
        if (string.IsNullOrEmpty(itemId)) return;

        var manager = Singleton<OrbitManager>.Instance;
        if (manager?.WaypointSystem == null) return;

        if (manager.WaypointSystem.RemoveLooseLootByItemId(itemId))
            Log.Debug($"LootItemKilled: {__instance.name} left the world, LooseLoot waypoint dropped");
    }
}
