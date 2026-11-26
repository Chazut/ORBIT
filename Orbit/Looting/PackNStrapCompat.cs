using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;

namespace Orbit.Looting;

internal static class PackNStrapCompat
{
    private const string PluginId = "com.wtt.packnstrap";

    // The optional mod equips its searchable belt in ArmBand. Inspect the existing item type
    // rather than resolving optional types by scanning all loaded assemblies.
    internal static Item CorpseBelt(InventoryEquipment equipment)
    {
        if (!Chainloader.PluginInfos.ContainsKey(PluginId)) return null;
        var item = equipment?.GetSlot(EquipmentSlot.ArmBand)?.ContainedItem;
        if (item is not CompoundItem) return null;
        for (var type = item.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "PackNStrap.Core.Items.CustomBeltItemClass") return item;
        return null;
    }

    internal static bool CanSearchBelt(CorpseItemController owner)
    {
        if (ServerConfig.Loot.LootPlayerBeltContents) return true;
        // The raid keeps this lookup after death, including players on a Fika host.
        var id = owner?.KilledProfileID;
        return !string.IsNullOrEmpty(id) && Singleton<GameWorld>.Instance?.GetEverExistedPlayerByID(id)?.IsAI == true;
    }

    internal static Item ProtectedBelt(Item root)
    {
        if (root is not InventoryEquipment equipment) return null;
        var belt = CorpseBelt(equipment);
        return belt != null && !CanSearchBelt(equipment.Owner as CorpseItemController) ? belt : null;
    }

    // A co-op player can move a queued item into the belt during the bot's search delay.
    internal static bool TakesProtectedItem(IOperationResult operation)
    {
        if (!Chainloader.PluginInfos.ContainsKey(PluginId)) return false;
        if (operation is SwapResult swap) return IsProtected(swap.Item) || IsProtected(swap.Item2);
        return operation is IItemOperationResult pickup && IsProtected(pickup.Item);
    }

    internal static bool IsProtected(Item item)
    {
        if (item?.Owner is not CorpseItemController owner || owner.RootItem is not InventoryEquipment equipment)
            return false;
        var belt = equipment.GetSlot(EquipmentSlot.ArmBand)?.ContainedItem;
        if (belt == null || !(ReferenceEquals(item, belt) || item.IsChildOf(belt))) return false;
        return CorpseBelt(equipment) != null && (ReferenceEquals(item, belt) || !CanSearchBelt(owner));
    }
}
