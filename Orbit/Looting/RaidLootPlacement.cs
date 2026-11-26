using System.Collections.Generic;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;

namespace Orbit.Looting;

internal static class RaidLootPlacement
{
    internal static bool InSecureSlot(IContainer container, InventoryController inventory)
    {
        var secure = inventory.Inventory.Equipment.GetSlot(EquipmentSlot.SecuredContainer);
        if (secure == null) return false;
        while (container != null && container is not IItemOwner)
        {
            if (ReferenceEquals(container, secure)) return true;
            if (ReferenceEquals(container.ParentItem, inventory.Inventory.Equipment)) break;
            container = container.ParentItem?.CurrentAddress?.Container;
        }
        return false;
    }

    internal static bool GoesIntoSecureSlot(IOperationResult op, InventoryController inventory)
    {
        if (op is SwapResult swap)
            return InSecureSlot(swap.To?.Container, inventory) || InSecureSlot(swap.To2?.Container, inventory);
        var target = op switch
        {
            MoveResult move => move.To,
            MergeResult merge => merge.TargetItem?.CurrentAddress,
            TransferResult transfer => transfer.TargetItem?.CurrentAddress,
            _ => null
        };
        return InSecureSlot(target?.Container, inventory);
    }

    internal static OperationResult<IItemOperationResult> Find(Item item, InventoryController inventory)
    {
        var equipment = inventory.Inventory.Equipment;
        var native = ItemManipulator.QuickFindAppropriatePlace(item, inventory, new[] { equipment },
            ItemManipulator.EMoveItemOrder.PickUp, true);
        if (!native.Succeeded || !GoesIntoSecureSlot(native.Value, inventory)) return native;

        // Preserve EFT's equip and stack preferences. A secure destination can be chosen
        // even when a later pocket or backpack grid is still available.
        var targets = new List<IContainer>();
        foreach (var container in equipment.GetPrioritizedContainersForLoot(item))
            if (!InSecureSlot(container, inventory)) targets.Add(container);
        if (ItemManipulator.TryFindMergeableItem(targets, item, out var stack, 1))
        {
            var merged = ItemManipulator.TransferOrMerge(item, stack, inventory, true);
            if (merged.Succeeded) return merged.Cast<ITransferOrMergeResult, IItemOperationResult>();
        }
        foreach (var target in targets)
        {
            if (!target.TryFindLocationForItem(item, out var location) || InSecureSlot(location.Container, inventory)) continue;
            var move = ItemManipulator.Move(item, location, inventory, true);
            if (move.Succeeded && !move.Value.ItemsDestroyRequired) return move.Cast<MoveResult, IItemOperationResult>();
        }
        return new NoFreeSpaceError(item);
    }
}
