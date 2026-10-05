using BepInEx.Bootstrap;
using EFT.InventoryLogic;

namespace Orbit.Looting;

internal static class PackNStrapCompat
{
    // The optional mod equips its searchable belt in ArmBand. Inspect the existing item type
    // rather than resolving optional types by scanning all loaded assemblies.
    internal static Item CorpseBelt(InventoryEquipment equipment)
    {
        if (!Chainloader.PluginInfos.ContainsKey("com.wtt.packnstrap")) return null;
        var item = equipment?.GetSlot(EquipmentSlot.ArmBand)?.ContainedItem;
        if (item is not CompoundItem) return null;
        for (var type = item.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "PackNStrap.Core.Items.CustomBeltItemClass") return item;
        return null;
    }
}
