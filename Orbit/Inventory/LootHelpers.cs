using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Inventory;

/// <summary>
/// Inventory-grid math, slot-priority resolution, container interaction,
/// and root-item lookup helpers that every layer of the loot pipeline
/// uses. Extension methods on BSG types; no per-instance state.
/// </summary>
public static class LootHelpers
{
    public const int RESERVED_SLOT_COUNT = 2;
    public static readonly int LowPolyMask = LayerMask.GetMask("LowPolyCollider");
    public static readonly int LootMask = LayerMask.GetMask("Interactive", "Loot", "Deadbody");
    public static readonly AccessTools.FieldRef<Player, Corpse> _playerCorpseField
        = AccessTools.FieldRefAccess<Player, Corpse>("Corpse");

    private static readonly EquipmentSlot[] WeaponSlots =
    {
        EquipmentSlot.Holster,
        EquipmentSlot.FirstPrimaryWeapon,
        EquipmentSlot.SecondPrimaryWeapon,
    };

    private static readonly EquipmentSlot[] StorageSlots =
    {
        EquipmentSlot.Backpack,
        EquipmentSlot.ArmorVest,
        EquipmentSlot.TacticalVest,
        EquipmentSlot.Pockets,
    };

    private static readonly EquipmentSlot[] OtherSlots =
    {
        EquipmentSlot.ArmBand,
        EquipmentSlot.Headwear,
        EquipmentSlot.Earpiece,
        EquipmentSlot.Dogtag,
        EquipmentSlot.Scabbard,
        EquipmentSlot.FaceCover,
        EquipmentSlot.Eyewear,
    };

    /// <summary>Total cell count across every grid in this container.</summary>
    public static int GetContainerSize(this SearchableItemItemClass container)
    {
        var grids = container.Grids;
        var gridSize = 0;

        foreach (var grid in grids)
            gridSize += grid.GridHeight * grid.GridWidth;

        return gridSize;
    }

    /// <summary>True for keys that go away after one use (e.g. Unknown Key).</summary>
    public static bool IsSingleUseKey(this Item item)
    {
        var key = item.GetItemComponent<KeyComponent>();
        return key != null && key.Template.MaximumNumberOfUsage == 1;
    }

    /// <summary>
    /// Open/close a container or door. Uses BSG's vmethod_0 + vmethod_1
    /// which is the only Fika-compatible interaction path. The vmethod_0
    /// is required for Door instances specifically (network-replicated
    /// state change).
    /// </summary>
    public static void InteractContainer(
        WorldInteractiveObject worldInteractiveObject,
        BotOwner botOwner,
        EInteractionType action,
        BotLog log)
    {
        if (worldInteractiveObject == null)
        {
            if (log.DebugEnabled)
                log.LogWarning($"Interacting [{action}] with WorldInteractiveObject but is NULL");
            return;
        }

        var interactionResult = new InteractionResult(action);
        if (worldInteractiveObject is Door)
        {
            // vmethod_0 is the Fika-replicated state change. vmethod_1
            // below also runs to settle the local-side animation.
            botOwner.GetPlayer.vmethod_0(worldInteractiveObject, interactionResult, null);
        }

        botOwner.GetPlayer.vmethod_1(worldInteractiveObject, interactionResult);
    }

    /// <summary>Empty grid-cell count summed across all grids.</summary>
    public static int GetAvailableGridSlots(StashGridClass[] grids)
    {
        if (grids is null) return 0;

        var freeSpaces = 0;

        foreach (var grid in grids)
        {
            var gridSize = grid.GridHeight * grid.GridWidth;
            var containedItemSize = grid.GetSizeOfContainedItems();
            freeSpaces += gridSize - containedItemSize;
        }

        return freeSpaces;
    }

    /// <summary>Sum of all items' cell footprints in this grid.</summary>
    public static int GetSizeOfContainedItems(this StashGridClass grid)
    {
        var containedItemSize = 0;
        foreach (var item in grid.Items)
            containedItemSize += item.GetItemSize();
        return containedItemSize;
    }

    /// <summary>Cell footprint of an item (width × height).</summary>
    public static int GetItemSize(this Item item)
    {
        var dimensions = item.CalculateCellSize();
        return dimensions.X * dimensions.Y;
    }

    /// <summary>
    /// For a stackable item, walks the bot's inventory looking for a
    /// matching stack we could merge into (skips ammo cartridges, weapon
    /// chambers, and the secure container). Returns null when no merge
    /// candidate exists or the item isn't stackable.
    /// </summary>
    public static Item FindItemToMerge(this InventoryController controller, Item item)
    {
        if (item.StackMaxSize <= 1)
            return null;

        foreach (var foundItem in controller.Inventory.GetAllItemByTemplate(item.TemplateId))
        {
            if (foundItem == null) continue;

            var rootItem = foundItem.GetRootItem();

            // Skip cartridges / weapon chambers (StackSlot/Slot containers).
            if (foundItem.Parent.Container is StackSlot or Slot)
                continue;

            if (rootItem.Parent.Container.ID.Equals("securedcontainer", StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.StackObjectsCount + foundItem.StackObjectsCount <= foundItem.StackMaxSize)
                return foundItem;
        }

        return null;
    }

    /// <summary>
    /// Slot loot priority for corpse looting. A bot that already has a
    /// backpack/rig grabs the weapons off the corpse first (they're the
    /// most valuable bulk pickup); otherwise it loots equipment first so
    /// the next pass has containers to put weapons into.
    /// </summary>
    public static void GetPriorityItems(
        this InventoryEquipment corpseEquipment,
        InventoryEquipment botEquipment,
        List<Item> preallocatedList)
    {
        var hasBackpack = botEquipment.GetSlot(EquipmentSlot.Backpack).ContainedItem != null;
        var hasTacVest = botEquipment.GetSlot(EquipmentSlot.TacticalVest).ContainedItem != null;

        if (hasBackpack || hasTacVest)
        {
            GetItemInSlotsNonAlloc(corpseEquipment, botEquipment, preallocatedList, WeaponSlots);
            GetItemInSlotsNonAlloc(corpseEquipment, botEquipment, preallocatedList, StorageSlots);
        }
        else
        {
            GetItemInSlotsNonAlloc(corpseEquipment, botEquipment, preallocatedList, StorageSlots);
            GetItemInSlotsNonAlloc(corpseEquipment, botEquipment, preallocatedList, WeaponSlots);
        }

        GetItemInSlotsNonAlloc(corpseEquipment, botEquipment, preallocatedList, OtherSlots);
    }

    private static void GetItemInSlotsNonAlloc(
        InventoryEquipment equipment,
        InventoryEquipment botEquipment,
        List<Item> preallocatedList,
        EquipmentSlot[] slots)
    {
        var equipmentOwner = equipment.Parent.GetOwner();
        var botOwner = botEquipment.Parent.GetOwner();
        foreach (var slotName in slots)
        {
            var slot = equipment.GetSlot(slotName);
            var item = slot.ContainedItem;
            if (item == null) continue;

            // Skip BSG-tagged unlootable items (quest-bound, secured-from-
            // corpse-strip). Pockets are always included so we can grab
            // ammo / consumables.
            var unlootableComponent = item.GetItemComponent<UnlootableComponent>();
            if (
                unlootableComponent != null
                && equipmentOwner != botOwner
                && unlootableComponent.IsUnlootableFrom(item.Parent.Container)
                && item is not PocketsItemClass)
            {
                continue;
            }

            preallocatedList.Add(item);
        }
    }

    /// <summary>Root item of a lootable interactable, or null.</summary>
    public static Item GetRootItem(this InteractableObject interactableObject)
        => interactableObject switch
        {
            LootableContainer container => container.ItemOwner?.RootItem,
            LootItem lootItem => lootItem.ItemOwner?.RootItem,
            _ => null,
        };

    /// <summary>Root item id of a lootable interactable, or null.</summary>
    public static string GetRootItemId(this InteractableObject interactableObject)
        => interactableObject switch
        {
            LootableContainer container => container.ItemOwner?.RootItem.Id,
            LootItem lootItem => lootItem.ItemOwner?.RootItem.Id,
            _ => null,
        };

    /// <summary>Human-readable name of a lootable interactable.</summary>
    public static string GetLootName(this InteractableObject interactableObject)
        => interactableObject switch
        {
            LootableContainer container => container.ItemOwner?.RootItem.Name.Localized(),
            Corpse corpse => corpse.name,
            LootItem lootItem => lootItem.ItemOwner?.RootItem.Name.Localized(),
            _ => "-",
        };

    /// <summary>
    /// Detects whether moving an item into <paramref name="slot"/> is
    /// blocked by another currently-equipped item (e.g. helmet + face
    /// mask collision). Chest / rig armour is explicitly NOT counted as
    /// a blocker so the bot can still swap to a better rig.
    /// </summary>
    public static bool HasBlockingItem(this Slot slot, Item incomingItem, out Item conflictingItem)
    {
        conflictingItem = null;

        var conflictingSlots = slot.ConflictingSlots;
        if (conflictingSlots is null) return false;

        if (!incomingItem.TryGetItemComponent<SlotBlockerComponent>(out var slotBlocker))
            return false;

        var slotNames = slotBlocker.ConflictingSlotNames;
        for (var i = 0; i < slotNames.Length; i++)
        {
            if (
                conflictingSlots.TryGetValue(slotNames[i], out var conflictingSlot)
                && conflictingSlot != slot
                && conflictingSlot.ContainedItem is { } conflictItem
                && conflictItem is not ArmorItemClass and not VestItemClass)
            {
                conflictingItem = conflictItem;
                return true;
            }
        }

        return false;
    }
}
