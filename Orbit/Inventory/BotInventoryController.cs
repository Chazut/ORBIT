using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Orbit.Core;
using Orbit.Helpers;
using UnityEngine;
using UnityEngine.Pool;

namespace Orbit.Inventory;

/// <summary>
/// Per-weapon snapshot used by the swap-decision pricer to detect
/// "would this incoming gun be a meaningful upgrade?".
/// </summary>
public class GearValue
{
    public readonly ValuePair Primary = new(string.Empty, 0f);
    public readonly ValuePair Secondary = new(string.Empty, 0f);
    public readonly ValuePair Holster = new(string.Empty, 0f);
}

/// <summary>Id-tagged price pair, mutable in place to avoid allocs.</summary>
public class ValuePair(string id, float value)
{
    public string Id = id;
    public float Value = value;

    public void UpdatePair(string id, float value)
    {
        Id = id;
        Value = value;
    }

    public void UpdatePair(ValuePair pair)
    {
        Id = pair.Id;
        Value = pair.Value;
    }
}

/// <summary>
/// Per-bot loot accounting. <see cref="Looted"/> is the in-raid delta
/// (NetWorth minus the snapshot taken at bot spawn). Surfaced to the
/// extract trigger and to the debug overlay.
/// </summary>
public class BotStats
{
    public readonly GearValue WeaponValues = new();

    public float NetWorth;
    public float InitialNetWorth;
    public int AvailableGridSpaces;
    public int TotalGridSpaces;

    public float Looted => NetWorth - InitialNetWorth;

    public void AddNetValue(float itemPrice)
    {
        NetWorth += itemPrice;
        Log.Debug($"BotStats.AddNetValue: +{itemPrice:N0}₽ → NetWorth={NetWorth:N0}₽ (Looted={Looted:N0}₽)");
    }

    public void SubtractNetValue(float itemPrice) => NetWorth -= itemPrice;

    public void ApplyNetValueDelta(float itemPrice) => NetWorth += itemPrice;

    public void StatsDebugPanel(StringBuilder debugPanel)
    {
        var freeSpaceColor =
            AvailableGridSpaces <= 2 ? Color.red
            : AvailableGridSpaces < TotalGridSpaces / 2 ? Color.yellow
            : Color.green;

        debugPanel.AppendLabeledValue("Total Looted Value", $" {Looted:n0}₽", Color.white, Color.white);
        debugPanel.AppendLabeledValue("Total Net Worth", $" {NetWorth:n0}₽", Color.white, Color.white);
        debugPanel.AppendLabeledValue("Available Space", $" {AvailableGridSpaces} slots", Color.white, freeSpaceColor);
        debugPanel.AppendLabeledValue("Primary Value", $" {WeaponValues.Primary.Value:n0}₽", Color.white, Color.white);
        debugPanel.AppendLabeledValue("Secondary Value", $" {WeaponValues.Secondary.Value:n0}₽", Color.white, Color.white);
        debugPanel.AppendLabeledValue("Holster Value", $" {WeaponValues.Holster.Value:n0}₽", Color.white, Color.white);
    }
}

/// <summary>
/// The actual inventory engine the loot brain delegates to. Walks
/// incoming pickup candidates, decides equip / swap / pickup / throw
/// per item, runs the rented <see cref="LootRoutineAction"/>s through
/// the transaction controller, updates the bot's stats. Also drives the
/// weapon-selector hand-off so the freshly looted gun becomes the
/// bot's active weapon.
/// </summary>
public class BotInventoryController
{
    private readonly BotLog _log;
    private readonly LootTransactionController _transactionController;
    private readonly BotOwner _botOwner;
    private readonly InventoryController _botInventoryController;
    private readonly LootBrain _lootBrain;
    private readonly ItemValuator _itemValuator;

    public readonly BotStats Stats = new();

    public ArmorComponent CurrentArmorVest
    {
        get
        {
            var chest = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.ArmorVest).ContainedItem;
            return chest?.GetItemComponent<ArmorComponent>();
        }
    }

    public ArmorComponent CurrentArmorRig
    {
        get
        {
            var tacVest = (SearchableItemItemClass)
                _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.TacticalVest).ContainedItem;
            return tacVest?.GetItemComponent<ArmorComponent>();
        }
    }

    public ArmorComponent CurrentHeadArmor
    {
        get
        {
            var helmet = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Headwear).ContainedItem;
            return helmet?.GetItemComponent<ArmorComponent>();
        }
    }

    public ArmorComponent CurrentTorsoArmor => CurrentArmorRig ?? CurrentArmorVest;
    public int CurrentTorsoArmorClass => CurrentTorsoArmor?.ArmorClass ?? 0;
    public int CurrentHeadArmorClass => CurrentHeadArmor?.ArmorClass ?? 0;

    /// <summary>Rouble value of the item currently being evaluated.</summary>
    public float CurrentItemPrice;

    public bool ShouldSort = true;

    public BotInventoryController(BotOwner botOwner, LootBrain lootBrain)
    {
        _log = new BotLog(botOwner);

        try
        {
            _lootBrain = lootBrain;
            _itemValuator = LootConfig.ItemValuator;

            _botInventoryController = botOwner.GetPlayer.InventoryController;
            _botOwner = botOwner;
            _transactionController = new LootTransactionController(_botInventoryController, _log);

            CalculateGearValue();
            CalculateInitialNetWorth();
            UpdateGridStats();
        }
        catch (Exception e)
        {
            _log.LogError(e);
        }
    }

    /// <summary>
    /// Snapshot the bot's currently-equipped weapons' values, used as
    /// the comparison baseline for swap decisions ("is this incoming
    /// gun actually an upgrade?").
    /// </summary>
    public void CalculateGearValue()
    {
        if (_log.DebugEnabled) _log.LogDebug("Calculating gear value...");

        var primary = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon).ContainedItem;
        var secondary = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon).ContainedItem;
        var holster = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Holster).ContainedItem;

        UpdateSlotValue(primary, Stats.WeaponValues.Primary);
        UpdateSlotValue(secondary, Stats.WeaponValues.Secondary);
        UpdateSlotValue(holster, Stats.WeaponValues.Holster);
    }

    private void UpdateSlotValue(Item slotItem, ValuePair valuePair)
    {
        if (slotItem != null)
        {
            if (valuePair.Id != slotItem.Id)
            {
                var value = _itemValuator.GetItemPrice(slotItem, _log);
                valuePair.UpdatePair(slotItem.Id, value);
            }
        }
        else if (!string.IsNullOrEmpty(valuePair.Id))
        {
            valuePair.UpdatePair(string.Empty, 0f);
        }
    }

    /// <summary>
    /// Sum the value of every item the bot spawned with. Becomes the
    /// baseline for the in-raid Looted delta.
    /// </summary>
    public void CalculateInitialNetWorth()
    {
        Stats.NetWorth = 0f;
        foreach (var slot in _botInventoryController.Inventory.Equipment.CachedSlots)
        {
            var containedItem = slot.ContainedItem;
            if (containedItem == null) continue;

            if (containedItem is SearchableItemItemClass searchableItem)
            {
                foreach (var nestedItem in searchableItem.GetFirstLevelItems())
                    Stats.NetWorth += _itemValuator.GetItemPrice(nestedItem, _log);
            }
            else
            {
                Stats.NetWorth += _itemValuator.GetItemPrice(containedItem, _log);
            }
        }
        Stats.InitialNetWorth = Stats.NetWorth;
    }

    /// <summary>
    /// Refresh the "have I got room?" counters based on the bot's
    /// currently-equipped containers.
    /// </summary>
    public void UpdateGridStats()
    {
        var tacVest = (SearchableItemItemClass)
            _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.TacticalVest).ContainedItem;
        var backpack = (SearchableItemItemClass)_botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem;
        var pockets = (SearchableItemItemClass)_botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Pockets).ContainedItem;

        var freePockets = LootHelpers.GetAvailableGridSlots(pockets?.Grids);
        var freeTacVest = LootHelpers.GetAvailableGridSlots(tacVest?.Grids);
        var freeBackpack = LootHelpers.GetAvailableGridSlots(backpack?.Grids);

        Stats.AvailableGridSpaces = freeBackpack + freePockets + freeTacVest;
        Stats.TotalGridSpaces = (tacVest?.Grids?.Length ?? 0) + (backpack?.Grids?.Length ?? 0) + (pockets?.Grids?.Length ?? 0);
    }

    /// <summary>
    /// Main entry: walk every candidate item from a corpse / container /
    /// loose-loot pickup and either equip, swap, pickup-into-container,
    /// strip, or skip each one in turn.
    /// </summary>
    public async Task<bool> TryAddItemsToBotAsync(List<Item> items, CancellationToken token = default)
    {
        var lootingActions = ListActionPool.Rent();
        try
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();

                if (item.Name == null)
                {
                    if (_log.DebugEnabled) _log.LogDebug("Item is NULL");
                    continue;
                }

                if (LootConfig.UseExamineTime)
                    await SimulateExamineTimeAsync(item, token);

                var itemName = item.Name.Localized();
                var itemSize = item.GetItemSize();
                CurrentItemPrice = _itemValuator.GetItemPrice(item, _log);

                if (_log.DebugEnabled)
                {
                    var itemValue = itemSize > 1
                        ? $"{CurrentItemPrice:N0}₽ {CurrentItemPrice / itemSize:N0}₽/slot"
                        : $"{CurrentItemPrice:N0}₽";
                    _log.LogDebug($"Loot found: {itemName} ({itemValue})");
                }

                // Skip magazines the bot can't use in any equipped weapon.
                if (item is MagazineItemClass mag && !IsUsableMag(mag))
                {
                    if (_log.DebugEnabled) _log.LogDebug($"Cannot use mag: {itemName}. Skipping");
                    continue;
                }

                // Equip/swap decision chain.
                ListActionPool.Reset(lootingActions);
                var canEquipGear = GetEquipAction(item, lootingActions);
                if (canEquipGear)
                {
                    if (_log.DebugEnabled) _log.LogDebug($"Found equip action for: {itemName}");

                    foreach (var action in lootingActions)
                    {
                        var actionResult = await action.ExecuteAsync(_transactionController, token);
                        if (actionResult)
                        {
                            Stats.ApplyNetValueDelta(action.NetWorthDelta);
                        }
                        else
                        {
                            break;
                        }

                        // Post-action work: handle the now-discarded item
                        // (mags strip, value transfer, etc.).
                        if (action is LootSwapAction swapAction)
                        {
                            if (swapAction.TransferItems)
                            {
                                if (swapAction.ToSwap is Weapon thrownWeapon)
                                {
                                    // Swapped weapon — drop mags we don't need, strip mods.
                                    await ThrowUselessMagsAsync(thrownWeapon, token);
                                    if (LootConfig.CanStripAttachments)
                                        await StripWeaponAsync(thrownWeapon, token);
                                }
                                else
                                {
                                    // Swapped container — throw cheap items
                                    // out to make room for what was in the
                                    // old container, then loot it.
                                    await ThrowUndervaluedItemsAsync(swapAction.Item, token);
                                    await LootNestedItemsAsync(swapAction.ToSwap, token);
                                }
                            }
                        }
                        else if (action is LootThrowAction throwAction)
                        {
                            if (throwAction.TransferItems)
                            {
                                var thrownItem = throwAction.Item;

                                _lootBrain.IgnoreLoot(thrownItem.Id);

                                if (thrownItem is Weapon thrownWeapon)
                                {
                                    await ThrowUselessMagsAsync(thrownWeapon, token);
                                    if (LootConfig.CanStripAttachments)
                                        await StripWeaponAsync(thrownWeapon, token);
                                }
                                else
                                {
                                    await LootNestedItemsAsync(thrownItem, token);
                                }
                            }
                        }
                    }

                    // We looted a weapon, refresh the value snapshot.
                    if (item is Weapon)
                        CalculateGearValue();

                    if (_log.DebugEnabled) _log.LogDebug($"Finished equip action for: {itemName}");

                    continue;
                }

                // Equip directly when slot is empty.
                if (AllowedToEquip(item) && await _transactionController.TryEquipItemAsync(item, token))
                {
                    Stats.AddNetValue(CurrentItemPrice);
                    continue;
                }

                // Pickup nested items first when looting a rig — gives a
                // chance to transfer ammo to the bot's active rig before
                // the whole container goes into a backpack.
                if (item is SearchableItemItemClass searchableItem)
                {
                    var success = await LootNestedItemsAsync(searchableItem, token);
                    if (!success) return false;
                }

                // Pickup into a free grid slot.
                if (AllowedToPickup(item, itemSize) && await _transactionController.TryPickupItemAsync(item, token))
                {
                    Stats.AddNetValue(CurrentItemPrice);
                    UpdateGridStats();
                }
                else if (item is Weapon weapon && LootConfig.CanStripAttachments)
                {
                    // Can't take the weapon whole — at least grab the mods.
                    var successful = await StripWeaponAsync(weapon, token);
                    if (!successful) return false;
                }
            }
        }
        finally
        {
            ListActionPool.Return(lootingActions);
        }

        return true;
    }

    /// <summary>
    /// Per-item think delay before a pickup commits. Driven by the item's
    /// own ExamineTime divided by the bot's Attention skill — high-
    /// attention bots act faster, simulating both player skill and
    /// item familiarity.
    /// </summary>
    public Task SimulateExamineTimeAsync(Item item, CancellationToken token = default)
        => LootTransactionController.SimulatePlayerDelayAsync(
            item.ExamineTime * 1000f / (1f + _botOwner.Profile.Skills.AttentionExamineValue),
            token);

    /// <summary>
    /// Refresh the bot's known-weapon list and tell BSG's weapon
    /// selector to draw the best available. Aggressively null-guarded
    /// because loot transactions are async and the bot may have died
    /// mid-loot, leaving half of its component graph disposed.
    /// </summary>
    public void UpdateActiveWeapon()
    {
        if (_botOwner == null) return;
        var inv = _botOwner.InventoryController;
        if (inv == null) return;
        if (inv.IsChangingWeaponNonLinq())
        {
            var player = _botOwner.GetPlayer;
            var hands = player?.HandsController;
            hands?.FastForwardCurrentState();
        }

        if (_log.DebugEnabled) _log.LogDebug("Updating weapons");

        var weaponManager = _botOwner.WeaponManager;
        var weaponSelector = weaponManager?.Selector;
        if (weaponSelector == null) return;
        weaponSelector.UpdateWeaponsList();
        weaponSelector.SetSlotItem(OnWeaponTaken, true);
    }

    private void RefillAndReload()
    {
        _botOwner.WeaponManager.Reload?.TryFillMagazines();
        _botOwner.WeaponManager.Reload?.TryReload();
    }

    /// <summary>
    /// Walk slot categories (container first, then helmet, etc.) looking
    /// for a swap-worthy slot. Container slots win first so the bot
    /// doesn't fill a rig it's about to drop.
    /// </summary>
    public bool GetEquipAction(Item lootItem, List<LootRoutineAction> lootingActions)
    {
        if (!AllowedToEquip(lootItem)) return false;

        if (lootItem.Template is WeaponTemplate && !BotTypeUtils.IsBoss(_botOwner.Profile.Info.Settings.Role))
        {
            GetWeaponEquipAction(lootItem as Weapon, lootingActions);
            return lootingActions.Count > 0;
        }

        var helmet = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Headwear).ContainedItem;
        var earpiece = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Earpiece).ContainedItem;
        var faceCover = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.FaceCover).ContainedItem;
        var eyewear = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Eyewear).ContainedItem;
        var chest = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.ArmorVest).ContainedItem;
        var armBand = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.ArmBand).ContainedItem;
        var tacVest = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.TacticalVest).ContainedItem;
        var backpack = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem;

        if (EquipmentClassifier.IsBackpack(lootItem) && ShouldSwapGear(backpack, lootItem))
            GetSwapAction(lootItem, backpack, lootingActions, true);
        else if (EquipmentClassifier.IsHelmet(lootItem) && ShouldSwapGear(helmet, lootItem))
            GetSwapAction(lootItem, helmet, lootingActions, true);
        else if (EquipmentClassifier.IsEarpiece(lootItem) && ShouldSwapGear(earpiece, lootItem))
            GetSwapAction(lootItem, earpiece, lootingActions, false);
        else if (EquipmentClassifier.IsFaceCover(lootItem) && ShouldSwapGear(faceCover, lootItem))
            GetSwapAction(lootItem, faceCover, lootingActions, false);
        else if (EquipmentClassifier.IsEyewear(lootItem) && ShouldSwapGear(eyewear, lootItem))
            GetSwapAction(lootItem, eyewear, lootingActions, false);
        else if (EquipmentClassifier.IsArmband(lootItem) && ShouldSwapGear(armBand, lootItem))
            GetSwapAction(lootItem, armBand, lootingActions, true);
        else if (EquipmentClassifier.IsChestArmor(lootItem) && ShouldSwapGear(chest, lootItem))
            GetSwapAction(lootItem, chest, lootingActions, true);
        else if (EquipmentClassifier.IsTacticalRig(lootItem) && ShouldSwapGear(tacVest, lootItem))
        {
            // Special case: incoming armoured rig with an equipped chest
            // armour — if the rig's plates outclass the chest, drop the
            // chest first, THEN swap into the rig.
            if (chest is not null && EquipmentClassifier.IsArmoredRig(lootItem))
            {
                if (GetArmorDifference(chest, lootItem) > 0)
                {
                    if (_log.DebugEnabled) _log.LogDebug("Trying to drop chest armor then loot armored rig");

                    var chestValue = _itemValuator.GetItemPrice(chest, _log);
                    var throwAction = LootThrowAction.Rent(chest, -chestValue);
                    lootingActions.Add(throwAction);
                    GetSwapAction(lootItem, tacVest, lootingActions, true);
                }
            }
            else
            {
                GetSwapAction(lootItem, tacVest, lootingActions, true);
            }
        }

        return lootingActions.Count > 0;
    }

    public bool IsUsableMag(MagazineItemClass mag)
        => mag != null && HasAcceptableMagazineSlot(_botInventoryController.Inventory.Equipment, mag);

    public bool IsUsableAmmo(AmmoItemClass ammo)
        => ammo != null && HasAcceptableAmmoSlot(_botInventoryController.Inventory.Equipment, ammo);

    private static readonly EquipmentSlot[] _weaponSlots =
    {
        EquipmentSlot.FirstPrimaryWeapon,
        EquipmentSlot.SecondPrimaryWeapon,
        EquipmentSlot.Holster,
    };

    private static bool HasAcceptableMagazineSlot(InventoryEquipment equipment, MagazineItemClass mag)
    {
        foreach (var weaponSlot in _weaponSlots)
        {
            var slot = equipment.GetSlot(weaponSlot);
            if (slot?.ContainedItem is not Weapon weapon) continue;

            var magazineSlot = weapon.GetMagazineSlot();
            if (magazineSlot != null && magazineSlot.CanAccept(mag))
                return true;
        }
        return false;
    }

    private static bool HasAcceptableAmmoSlot(InventoryEquipment equipment, AmmoItemClass ammo)
    {
        foreach (var weaponSlot in _weaponSlots)
        {
            var slot = equipment.GetSlot(weaponSlot);
            if (slot?.ContainedItem is not Weapon weapon) continue;

            foreach (var chamber in weapon.Chambers)
            {
                if (chamber.CanAccept(ammo))
                    return true;
            }
        }
        return false;
    }

    private readonly List<MagazineItemClass> _throwUselessMagsScratch = [];

    /// <summary>
    /// Drop every magazine in the bot's rig that no equipped weapon can
    /// use. Keeps up to 2 "shared" mags (the thrown weapon AND a kept
    /// weapon can use them) for the kept weapon, mirroring real-player
    /// behaviour of stashing a couple of cross-compat mags in side
    /// pockets.
    /// </summary>
    public async Task ThrowUselessMagsAsync(Weapon thrownWeapon, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var primary = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon).ContainedItem as Weapon;
        var secondary = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon).ContainedItem as Weapon;
        var holster = _botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Holster).ContainedItem as Weapon;
        var thrownMagSlot = thrownWeapon?.GetMagazineSlot();
        var primaryMagSlot = primary?.GetMagazineSlot();
        var secondaryMagSlot = secondary?.GetMagazineSlot();
        var holsterMagSlot = holster?.GetMagazineSlot();

        _throwUselessMagsScratch.Clear();
        _botInventoryController.GetReachableItemsOfTypeNonAlloc(_throwUselessMagsScratch);

        if (_log.DebugEnabled) _log.LogDebug("Cleaning up old mags...");

        var reservedCount = 0;
        foreach (var mag in _throwUselessMagsScratch)
        {
            var fitsInThrown = thrownMagSlot?.CanAccept(mag) == true;
            var fitsInPrimary = primaryMagSlot?.CanAccept(mag) == true;
            var fitsInSecondary = secondaryMagSlot?.CanAccept(mag) == true;
            var fitsInHolster = holsterMagSlot?.CanAccept(mag) == true;

            var fitsInEquipped = fitsInPrimary || fitsInSecondary || fitsInHolster;
            var isSharedMag = fitsInThrown && fitsInEquipped;
            if (isSharedMag && reservedCount < 2)
            {
                if (_log.DebugEnabled) _log.LogDebug($"Reserving shared mag {mag.Name.Localized()}");
                reservedCount++;
            }
            else if (!fitsInEquipped || reservedCount >= 2)
            {
                if (_log.DebugEnabled) _log.LogDebug($"Removing useless mag {mag.Name.Localized()}");

                await LootTransactionController.SimulatePlayerDelayAsync(token: token);

                if (!await _transactionController.ThrowItemAsync(mag, token))
                    continue;

                var magPrice = _itemValuator.GetItemPrice(mag, _log);
                if (_log.DebugEnabled) _log.LogDebug($"Thrown {mag.ShortName.Localized()} (-{magPrice:N0}₽)");
                Stats.SubtractNetValue(magPrice);
                _lootBrain.IgnoreLoot(mag.Id);
            }
        }

        if (_log.DebugEnabled) _log.LogDebug("Cleaning up old mags...done");
    }

    /// <summary>
    /// Weapon equip decision tree: pistol → holster, longarm →
    /// primary then secondary, with swap-on-better-value at each step.
    /// </summary>
    public void GetWeaponEquipAction(Weapon lootWeapon, List<LootRoutineAction> lootingActions)
    {
        var primary = (Weapon)_botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon).ContainedItem;
        var secondary = (Weapon)_botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon).ContainedItem;
        var holster = (Weapon)_botInventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Holster).ContainedItem;

        var isPistol = lootWeapon.WeapClass.Equals("pistol");
        var lootValue = CurrentItemPrice;

        if (isPistol)
        {
            if (holster == null)
            {
                if (_log.DebugEnabled) _log.LogDebug($"Trying to equip {lootWeapon.Name.Localized()} (₽{lootValue}) to holster");
                lootingActions.Add(LootMoveAction.Rent(lootWeapon, null, lootValue));
            }
            else
            {
                var holsterValue = Stats.WeaponValues.Holster.Value;
                if (lootValue > holsterValue)
                {
                    if (_log.DebugEnabled)
                        _log.LogDebug($"Trying to swap {holster.Name.Localized()} (₽{holsterValue}) with {lootWeapon.Name.Localized()} (₽{lootValue}) in holster");

                    lootingActions.Add(LootSwapAction.Rent(lootWeapon, holster, lootValue - holsterValue, true));
                }
            }
        }
        else
        {
            var primaryValue = Stats.WeaponValues.Primary.Value;
            var isBetterThanPrimary = lootValue > primaryValue;

            var secondaryValue = Stats.WeaponValues.Secondary.Value;
            var isBetterThanSecondary = lootValue > secondaryValue;

            // No primary → straight equip.
            if (primary == null)
            {
                if (_log.DebugEnabled) _log.LogDebug($"Trying to equip {lootWeapon.Name.Localized()} (₽{lootValue}) to primary");
                lootingActions.Add(LootMoveAction.Rent(lootWeapon, null, lootValue));
            }
            else
            {
                if (isBetterThanPrimary)
                {
                    if (secondary == null)
                    {
                        // Better than primary, no secondary → equip to
                        // secondary then swap with primary (so primary
                        // becomes secondary, loot becomes primary).
                        if (_log.DebugEnabled)
                            _log.LogDebug($"Trying to equip {lootWeapon.Name.Localized()} (₽{lootValue}) to secondary slot then swapping it with {primary.Name.Localized()} (₽{primaryValue})");

                        lootingActions.Add(LootMoveAction.Rent(lootWeapon, null, lootValue));
                        lootingActions.Add(LootSwapAction.Rent(lootWeapon, primary, 0f, false));
                    }
                    else if (isBetterThanSecondary)
                    {
                        // Better than both → swap with secondary (drop
                        // it) then promote into primary.
                        if (_log.DebugEnabled)
                            _log.LogDebug($"Trying to swap {lootWeapon.Name.Localized()} (₽{lootValue}) with secondary {secondary.Name.Localized()} (₽{secondaryValue}) then swapping loot weapon with primary {primary.Name.Localized()} (₽{primaryValue})");

                        lootingActions.Add(LootSwapAction.Rent(lootWeapon, secondary, lootValue - secondaryValue, true));
                        lootingActions.Add(LootSwapAction.Rent(lootWeapon, primary, 0f, false));
                    }
                }
                else if (secondary == null)
                {
                    if (_log.DebugEnabled) _log.LogDebug($"Trying to equip {lootWeapon.Name.Localized()} (₽{lootValue}) to secondary");
                    lootingActions.Add(LootMoveAction.Rent(lootWeapon, null, lootValue));
                }
                else if (isBetterThanSecondary)
                {
                    if (_log.DebugEnabled)
                        _log.LogDebug($"Trying to swap {secondary.Name.Localized()} (₽{secondaryValue}) with secondary {lootWeapon.Name.Localized()} (₽{lootValue})");

                    lootingActions.Add(LootSwapAction.Rent(lootWeapon, secondary, lootValue - secondaryValue, true));
                }
            }
        }
    }

    /// <summary>
    /// Swap criteria:
    ///   1. Loot has a higher armour class than equipped.
    ///   2. Loot is a container bigger than equipped, AND armour class
    ///      is the same.
    ///   3. Loot is more valuable, AND armour class is the same.
    /// Bosses never swap — their loadouts are scripted.
    /// </summary>
    public bool ShouldSwapGear(Item equipped, Item itemToLoot)
    {
        if (equipped == null) return false;

        if (BotTypeUtils.IsBoss(_botOwner.Profile.Info.Settings.Role))
            return false;

        if (equipped.Parent.Container is Slot equippedSlot && equippedSlot.HasBlockingItem(itemToLoot, out var conflictingItem))
        {
            if (_log.DebugEnabled)
                _log.LogDebug($"Cannot swap {itemToLoot.Name.Localized()} with {equipped.Name.Localized()} because of conflicting item {conflictingItem.Name.Localized()}");
            return false;
        }

        var armorDifference = GetArmorDifference(equipped, itemToLoot);
        if (armorDifference > 0)
        {
            if (_log.DebugEnabled)
                _log.LogDebug($"Found better armor {itemToLoot.Name.Localized()} versus {equipped.Name.Localized()}. Difference: {armorDifference}");
            return true;
        }

        var foundBiggerContainer = false;

        if (equipped.IsContainer)
        {
            var equippedSize = (equipped as SearchableItemItemClass).GetContainerSize();
            var itemToLootSize = (itemToLoot as SearchableItemItemClass).GetContainerSize();
            foundBiggerContainer = itemToLootSize > equippedSize;
        }

        if (armorDifference == 0 && foundBiggerContainer)
        {
            if (_log.DebugEnabled)
                _log.LogDebug($"Found bigger container {itemToLoot.Name.Localized()} versus {equipped.Name.Localized()}");
            return true;
        }

        if (armorDifference == 0 && LootIsMoreValuable(equipped))
        {
            if (_log.DebugEnabled)
                _log.LogDebug($"Found more valuable gear {itemToLoot.Name.Localized()} versus {equipped.Name.Localized()}");
            return true;
        }

        return false;
    }

    /// <summary>True when the loot armour outclasses what's
    /// currently equipped (helmet vs head, anything else vs torso).</summary>
    public bool IsBetterArmorThanEquipped(ArmoredEquipmentItemClass newArmor)
    {
        var equippedArmor = EquipmentClassifier.IsHelmet(newArmor) ? CurrentHeadArmor : CurrentTorsoArmor;
        return GetArmorDifference(equippedArmor?.Item, newArmor) > 0;
    }

    private bool LootIsMoreValuable(Item equippedItem)
        => CurrentItemPrice > LootConfig.ItemValuator.GetItemPrice(equippedItem, _log);

    /// <summary>
    /// Per-armour-piece comparison: walks both items' base armour AND any
    /// armour plates inserted into their slots, and returns the highest-
    /// class delta. Positive = loot is better.
    /// </summary>
    public static int GetArmorDifference(Item equippedItem, Item itemToLoot)
    {
        var currentArmorClass = equippedItem?.GetItemComponent<ArmorComponent>()?.ArmorClass ?? 0;
        if (equippedItem is ArmoredEquipmentItemClass equippedArmorItem)
        {
            foreach (var slot in equippedArmorItem.Slots)
            {
                if (slot is not GClass3125 { ContainedItem: ArmorPlateItemClass armorPlate })
                    continue;

                var armorComponent = armorPlate.Armor;
                if (armorComponent != null)
                {
                    var armorClass = armorComponent.ArmorClass;
                    if (armorClass > currentArmorClass)
                        currentArmorClass = armorClass;
                }
            }
        }

        var newArmorClass = itemToLoot.GetItemComponent<ArmorComponent>()?.ArmorClass ?? 0;
        if (itemToLoot is ArmoredEquipmentItemClass newArmorItem)
        {
            foreach (var slot in newArmorItem.Slots)
            {
                if (slot is not GClass3125 { ContainedItem: ArmorPlateItemClass armorPlate })
                    continue;

                var armorComponent = armorPlate.Armor;
                if (armorComponent != null)
                {
                    var armorClass = armorComponent.ArmorClass;
                    if (armorClass > newArmorClass)
                        newArmorClass = armorClass;
                }
            }
        }

        return newArmorClass - currentArmorClass;
    }

    /// <summary>
    /// Recursively walk a compound item's first-level children and try
    /// to loot each one. Used for container nesting (loot the items
    /// inside the rig before equipping the rig itself).
    /// </summary>
    public async Task<bool> LootNestedItemsAsync(Item item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        // Not restricted to SearchableItemItemClass — loot slots of
        // thrown/swapped helmets too, they often have valuable plates.
        if (item is not CompoundItem parentItem)
            return true;

        var items = ListPool<Item>.Get();
        try
        {
            foreach (var nestedItem in parentItem.GetFirstLevelItems())
            {
                var isItemLocked = nestedItem.CurrentAddress?.Container is Slot slot && slot.Locked;

                if (nestedItem.Id != parentItem.Id && !nestedItem.QuestItem && !isItemLocked)
                    items.Add(nestedItem);
            }

            if (items.Count > 0)
            {
                if (_log.DebugEnabled)
                    _log.LogDebug($"Looting {items.Count} items from {parentItem.Name.Localized()}");

                await LootTransactionController.SimulatePlayerDelayAsync(LootBrain.LootingStartDelay, token);
                return await TryAddItemsToBotAsync(items, token);
            }

            if (_log.DebugEnabled) _log.LogDebug($"No nested items found to loot in {parentItem.Name}");
            return true;
        }
        finally
        {
            ListPool<Item>.Release(items);
        }
    }

    /// <summary>
    /// When swapping into a container, drop its lowest-value contents to
    /// make room for what was in the old container. Respects the per-
    /// faction mini-loot threshold (PMC ≠ scav) and per-SAIN-archetype
    /// thresholds for PMCs.
    /// </summary>
    public async Task ThrowUndervaluedItemsAsync(Item item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (item is not SearchableItemItemClass parentItem)
            return;

        var itemsToThrow = DictionaryPool<Item, float>.Get();
        try
        {
            var botType = _botOwner.Profile.Info.Settings.Role;
            var isPmc = botType.IsPMC();

            foreach (var nestedItem in parentItem.GetFirstLevelItems())
            {
                if (
                    nestedItem.Id == parentItem.Id
                    || nestedItem.QuestItem
                    || (nestedItem.CurrentAddress?.Container is Slot slot && slot.Locked)
                    || (nestedItem is MagazineItemClass mag && IsUsableMag(mag))
                    || (nestedItem is AmmoItemClass ammo && IsUsableAmmo(ammo))
                    || nestedItem is MedsItemClass)
                {
                    continue;
                }

                var value = _itemValuator.GetItemPrice(nestedItem, _log);
                var minimumValue = ResolveMinimumLootValue(isPmc);
                var isUnderValued = value < minimumValue;
                if (!isUnderValued) continue;

                itemsToThrow.Add(nestedItem, value);
            }

            if (itemsToThrow.Count > 0)
            {
                if (_log.InfoEnabled)
                    _log.LogInfo($"Throwing {itemsToThrow.Count} undervalued items from {parentItem.Name.Localized()}");

                foreach (var (toThrow, value) in itemsToThrow)
                {
                    await LootTransactionController.SimulatePlayerDelayAsync(token: token);

                    if (!await _transactionController.ThrowItemAsync(toThrow, token))
                        continue;

                    if (_log.DebugEnabled) _log.LogDebug($"Thrown {toThrow.Name.Localized()} (-{value:N0}₽)");
                    Stats.SubtractNetValue(value);
                    _lootBrain.IgnoreLoot(toThrow.Id);
                }

                return;
            }

            if (_log.DebugEnabled) _log.LogDebug($"No undervalued items found to throw in {parentItem.Name}");
        }
        finally
        {
            DictionaryPool<Item, float>.Release(itemsToThrow);
        }
    }

    /// <summary>Strip and loot every removable mod from a weapon.</summary>
    public async Task<bool> StripWeaponAsync(Weapon weapon, CancellationToken token = default)
    {
        var itemsToAdd = ListPool<Item>.Get();
        try
        {
            foreach (var weaponSlot in weapon.Slots)
            {
                if (weaponSlot.Required) continue;

                foreach (var weaponMod in weaponSlot.Items)
                {
                    if (weaponMod is Mod mod && mod.RaidModdable)
                        itemsToAdd.Add(weaponMod);
                }
            }

            if (itemsToAdd.Count > 0)
            {
                if (_log.InfoEnabled) _log.LogInfo($"Trying to strip attachments of weapon: {weapon.Name.Localized()}");

                var success = await TryAddItemsToBotAsync(itemsToAdd, token);
                if (!success) return false;
            }

            if (_log.DebugEnabled) _log.LogDebug($"No attachments to strip for weapon: {weapon.Name.Localized()}");
            return true;
        }
        finally
        {
            ListPool<Item>.Release(itemsToAdd);
        }
    }

    /// <summary>
    /// True when this item's per-slot price meets the bot's faction +
    /// (for PMCs) archetype value floor / ceiling. PMC personalities
    /// override the global PMC floor — a Rat picks ₽6k clutter, a
    /// GigaChad ignores anything below ₽24k.
    /// </summary>
    public bool IsValuableEnough(float itemPrice)
    {
        var botType = _botOwner.Profile.Info.Settings.Role;
        var isPmc = botType.IsPMC();

        var min = ResolveMinimumLootValue(isPmc);
        var max = isPmc ? LootConfig.PMCMaxLootThreshold : LootConfig.ScavMaxLootThreshold.Value;

        return itemPrice >= min && (max == 0f || itemPrice <= max);
    }

    private float ResolveMinimumLootValue(bool isPmc)
    {
        if (!isPmc) return LootConfig.ScavMinLootThreshold.Value;

        var roster = Singleton<BotRoster>.Instance;
        var agent = roster?.GetAgent(_botOwner);
        return agent?.Squad?.Personality != null
            ? agent.Squad.Personality.MiniLootValueThreshold
            : LootConfig.PMCMinLootThreshold;
    }

    /// <summary>True when this item type is permitted in any equipment
    /// slot for the bot's faction.</summary>
    public bool AllowedToEquip(Item lootItem)
    {
        var eligiblePmcGear = (EquipmentType)LootConfig.PMCGearToEquip;
        var eligibleScavGear = (EquipmentType)LootConfig.ScavGearToEquip;

        var botType = _botOwner.Profile.Info.Settings.Role;
        var isPmc = botType.IsPMC();
        return isPmc ? eligiblePmcGear.IsItemEligible(lootItem) : eligibleScavGear.IsItemEligible(lootItem);
    }

    /// <summary>True when this item type is permitted as inventory
    /// content (slot rules ignored, value floor enforced).</summary>
    public bool AllowedToPickup(Item lootItem, int itemSize = 1)
    {
        var botType = _botOwner.Profile.Info.Settings.Role;
        var isPmc = botType.IsPMC();
        var pickupNotRestricted = isPmc
            ? LootConfig.PMCGearToPickup.IsItemEligible(lootItem, true)
            : LootConfig.ScavGearToPickup.IsItemEligible(lootItem, true);
        var isMoney = lootItem.Template is MoneyTemplateClass;

        // Usable mags, ammo, and money are always loot-eligible.
        // Everything else has to clear faction restriction AND
        // (dogtag-exempt) the value floor.
        return IsUsableMag(lootItem as MagazineItemClass)
            || IsUsableAmmo(lootItem as AmmoItemClass)
            || isMoney
            || (
                pickupNotRestricted
                && (EquipmentClassifier.IsDogtag(lootItem) || IsValuableEnough(CurrentItemPrice / itemSize)));
    }

    /// <summary>Emit a swap action with the per-item value delta filled
    /// in, optionally chaining transfer-of-contents on success.</summary>
    public void GetSwapAction(Item toEquip, Item toSwap, List<LootRoutineAction> lootingActions, bool transferItems = false)
    {
        var toEquipValue = CurrentItemPrice;
        var toSwapValue = _itemValuator.GetItemPrice(toSwap, _log);
        if (_log.DebugEnabled)
            _log.LogDebug($"Trying to equip {toEquip.Name.Localized()} (₽{toEquipValue:N0}) and swap with {toSwap.Name.Localized()} (₽{toSwapValue:N0}){(transferItems ? $" then loot {toSwap.Name.Localized()}" : string.Empty)}");

        lootingActions.Add(LootSwapAction.Rent(toEquip, toSwap, toEquipValue - toSwapValue, transferItems));
    }

    /// <summary>
    /// Weapon-selector callback. Hands BSG the new weapon, falls back to
    /// fast-forwarding the controller state on stall, retries via the
    /// AI task manager when the selector reports a soft error.
    /// </summary>
    private void OnWeaponTaken(Result<IHandsController> hands)
    {
        var weaponSelector = _botOwner.WeaponManager.Selector;
        weaponSelector.IsChanging = false;
        var allFine = false;

        if (hands.Succeed)
            _botOwner.WeaponManager.UpdateHandsController(hands.Value, out allFine);

        if (_botOwner.BotState != EBotState.Active)
        {
            if (_botOwner.BotState == EBotState.PreActive)
                return;
        }
        else
        {
            if (allFine)
            {
                RefillAndReload();
                weaponSelector.ErrorCounter = 0;

                if (_log.DebugEnabled) _log.LogDebug($"Current weapon is {hands.Value.Item.ToFullString()}");
                return;
            }
            if (++weaponSelector.ErrorCounter >= 20)
            {
                if (_log.DebugEnabled) _log.LogWarning("Unable to Selector.TakeMainWeapon");
                return;
            }
        }

        // Not active / not preactive / not allFine — fast-forward and retry.
        _botOwner.GetPlayer.HandsController.FastForwardCurrentState();
        _botOwner.AITaskManager.RegisterDelayedTask(_botOwner, 0.5f, UpdateActiveWeapon);
    }
}
