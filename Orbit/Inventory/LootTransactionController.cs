using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using InventoryControllerResultStruct = GStruct153;

namespace Orbit.Inventory;

/// <summary>
/// Wraps BSG's InventoryController to expose async move / swap / merge /
/// throw operations with built-in player-delay simulation, network-
/// transaction timeout handling, and BotLog-prefixed traces. Used by the
/// inventory controller's per-item picker chain and by the spare-ammo
/// preload path.
/// </summary>
public class LootTransactionController(InventoryController inventoryController, BotLog log)
{
    private const int NetworkTransactionTimeout = 5000;

    /// <summary>
    /// Pre-loads spare ammo for the weapon being looted into the bot's
    /// secure container so the bot's reload logic actually has rounds to
    /// reach for. 10 max-stack inserts; bails if the secure container
    /// already holds matching ammo. Fika-incompatible (per BSG event flow).
    /// </summary>
    public bool AddExtraAmmo(Weapon weapon)
    {
        try
        {
            var secureContainer = (SearchableItemItemClass)
                inventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.SecuredContainer).ContainedItem;

            var container = secureContainer.Grids.FirstOrDefault();

            // Resolve the ammo template from a magazine round if available,
            // otherwise spawn a fresh instance from the weapon's current
            // template.
            var ammoToAdd =
                weapon.GetCurrentMagazine()?.FirstRealAmmo()
                ?? Singleton<ItemFactoryClass>.Instance.CreateItem(MongoID.Generate(), weapon.CurrentAmmoTemplate._id, null);

            // Don't double-up if the secure container already has rounds
            // of the right calibre.
            var alreadyHasAmmo = false;

            foreach (var item in secureContainer.GetAllItems())
            {
                if (item is AmmoItemClass bullet && bullet.Caliber.Equals(((AmmoItemClass)ammoToAdd).Caliber))
                {
                    alreadyHasAmmo = true;
                    break;
                }
            }

            if (!alreadyHasAmmo)
            {
                if (log.DebugEnabled) log.LogDebug("Trying to add ammo");

                var ammoAdded = 0;

                for (var i = 0; i < 10; i++)
                {
                    var ammo = ammoToAdd.CloneItem();
                    ammo.StackObjectsCount = ammo.StackMaxSize;

                    var location = container.FindFreeSpace(ammo);

                    if (location != null)
                    {
                        var result = container.AddItemWithoutRestrictions(ammo, location);
                        if (result.Succeeded)
                        {
                            ammoAdded += ammo.StackObjectsCount;
                        }
                        else if (log.ErrorEnabled)
                        {
                            log.LogError($"Failed to add {ammo.Name.Localized()} to secure container");
                        }
                    }
                    else if (log.ErrorEnabled)
                    {
                        log.LogError($"Cannot find location in secure container for {ammo.Name.Localized()}");
                    }
                }

                if (ammoAdded > 0 && log.DebugEnabled)
                    log.LogDebug($"Successfully added {ammoAdded} round of {ammoToAdd.Name.Localized()}");
            }
            else if (log.DebugEnabled)
            {
                log.LogDebug($"Already has ammo for {weapon.Name.Localized()}");
            }
        }
        catch (Exception e)
        {
            log.LogError(e);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Find an open slot to equip this item to. If a slot is found,
    /// issues the move action. Returns false when no slot is available.
    /// </summary>
    public Task<bool> TryEquipItemAsync(Item item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        var ableToEquip = inventoryController.FindSlotToPickUp(item);
        if (ableToEquip == null)
        {
            if (log.DebugEnabled) log.LogDebug($"Could not find a place to equip: {item.Name.Localized()}");
            return Task.FromResult(false);
        }

        if (log.InfoEnabled) log.LogInfo($"Equipping: {item.Name.Localized()} [place: {ableToEquip.Container.ID.Localized()}]");

        return MoveItemAsync(item, ableToEquip, token);
    }

    /// <summary>
    /// Find a valid grid for the item, walking every currently-equipped
    /// container. If the item is stackable and a matching stack exists,
    /// emit a merge instead of a move. Returns false when no place exists.
    /// </summary>
    public Task<bool> TryPickupItemAsync(Item item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        var mergeableItem = inventoryController.FindItemToMerge(item);

        if (mergeableItem != null)
        {
            if (log.DebugEnabled) log.LogDebug($"Merging: {item.Name.Localized()} [with: {mergeableItem.Name.Localized()}]");
            return MergeItemAsync(item, mergeableItem, token);
        }

        var gridAddress = inventoryController.FindGridToPickUp(item);

        if (
            gridAddress != null
            && !string.Equals(gridAddress.GetRootItem()?.Parent?.Container?.ID, "securedcontainer", StringComparison.OrdinalIgnoreCase))
        {
            if (log.InfoEnabled)
                log.LogInfo($"Picking up: {item.Name.Localized()} [place: {gridAddress.GetRootItem()?.Name.Localized()}]");

            return MoveItemAsync(item, gridAddress, token);
        }

        if (log.DebugEnabled) log.LogDebug($"Could not find a place to pickup: {item.Name.Localized()}");

        return Task.FromResult(false);
    }

    /// <summary>
    /// Moves an item to a specified item address. Null address → try
    /// equip then pickup.
    /// </summary>
    public async Task<bool> MoveItemAsync(Item item, ItemAddress location, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (location == null)
            return await TryEquipItemAsync(item, token) || await TryPickupItemAsync(item, token);

        if (log.DebugEnabled)
            log.LogDebug($"Moving {item.Name.Localized()} to: {location.Container.ID.Localized()} [{location.GetRootItem()?.Name.Localized()}]...");

        await SimulatePlayerDelayAsync(token: token);

        var moveResult = InteractionsHandlerClass.Move(item, location, inventoryController, true);
        if (moveResult.Failed)
        {
            if (log.ErrorEnabled)
                log.LogWarning($"Failed to move {item.Name.Localized()} to {location.Container.ID.Localized()} [{location.GetRootItem()?.Name.Localized()}]. Error: {moveResult.Error}");
            return false;
        }

        var moveNetworkResult = await TryRunNetworkTransactionWithTimeoutAsync(moveResult, null, token);
        if (moveNetworkResult.Failed)
        {
            if (log.ErrorEnabled)
                log.LogError($"Failed to move {item.Name.Localized()} to {location.Container.ID.Localized()} [{location.GetRootItem()?.Name.Localized()}]. Network Error: {moveNetworkResult.Error}");
            return false;
        }

        if (log.InfoEnabled)
            log.LogInfo($"Moving {item.Name.Localized()} to: {location.Container.ID.Localized()} [{location.GetRootItem()?.Name.Localized()}]...done");

        return true;
    }

    /// <summary>
    /// Swap two items. <paramref name="item"/> is the incoming pickup,
    /// <paramref name="toSwap"/> is the equipped piece being discarded.
    /// </summary>
    public async Task<bool> SwapItemsAsync(Item item, Item toSwap, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (log.DebugEnabled) log.LogDebug($"Swapping {item.Name.Localized()} with {toSwap.Name.Localized()}...");

        await SimulatePlayerDelayAsync(token: token);

        var swapResult = InteractionsHandlerClass.Swap(item, toSwap.CurrentAddress, toSwap, item.CurrentAddress, inventoryController, true);
        if (swapResult.Failed)
        {
            if (log.WarningEnabled)
                log.LogWarning($"Failed to swap {item.Name.Localized()} with {toSwap.Name.Localized()}. Error: {swapResult.Error}");
            return false;
        }

        var swapNetworkResult = await TryRunNetworkTransactionWithTimeoutAsync(swapResult, null, token);
        if (swapNetworkResult.Failed)
        {
            if (log.ErrorEnabled)
                log.LogError($"Failed to swap {item.Name.Localized()} with {toSwap.Name.Localized()}. Network Error: {swapNetworkResult.Error}");
            return false;
        }

        if (log.InfoEnabled) log.LogInfo($"Swapping {item.Name.Localized()} with {toSwap.Name.Localized()}...done");

        return true;
    }

    /// <summary>Merge two stacks of the same item.</summary>
    public async Task<bool> MergeItemAsync(Item toMove, Item toItem, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (toItem == null)
        {
            log.LogWarning($"Cannot merge item {toMove} to NULL target item!");
            return false;
        }

        if (log.DebugEnabled)
            log.LogDebug($"Merging {toMove.Name?.Localized()} (Stack Size: {toMove.StackObjectsCount}) with: {toItem.Name.Localized()} (Stack Size: {toItem.StackObjectsCount})...");

        var mergeResult = InteractionsHandlerClass.Merge(toMove, toItem, inventoryController, true);
        if (mergeResult.Failed)
        {
            if (log.ErrorEnabled)
                log.LogError($"Failed to merge {toMove.Name.Localized()} (Stack Size: {toMove.StackObjectsCount}) with: {toItem.Name.Localized()} (Stack Size: {toItem.StackObjectsCount}). Error: {mergeResult.Error}");
            return false;
        }

        await SimulatePlayerDelayAsync(token: token);
        var mergeNetworkResult = await TryRunNetworkTransactionWithTimeoutAsync(mergeResult, null, token);
        if (mergeNetworkResult.Failed)
        {
            if (log.ErrorEnabled)
                log.LogError($"Failed to merge {toMove.Name.Localized()} (Stack Size: {toMove.StackObjectsCount}) with: {toItem.Name.Localized()} (Stack Size: {toItem.StackObjectsCount}). Network Error: {mergeNetworkResult.Error}");
            return false;
        }

        if (log.InfoEnabled)
            log.LogInfo($"Merging {toMove.Name?.Localized()} (Stack Size: {toMove.StackObjectsCount}) with: {toItem.Name.Localized()} (Stack Size: {toItem.StackObjectsCount})...done");

        return true;
    }

    /// <summary>Drop an item to the ground.</summary>
    public async Task<bool> ThrowItemAsync(Item toThrow, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (log.DebugEnabled) log.LogDebug($"Throwing item: {toThrow.Name.Localized()}...");

        await SimulatePlayerDelayAsync(token: token);

        var promise = new TaskCompletionSource<IResult>();
        inventoryController.ThrowItem(toThrow, false, promise.SetResult);

        var throwResult = await promise.Task;
        if (throwResult.Failed)
        {
            if (log.WarningEnabled)
                log.LogWarning($"Failed to throw item: {toThrow.Name.Localized()}. Error: {throwResult.Error}");
            return false;
        }

        if (log.InfoEnabled) log.LogInfo($"Throwing item: {toThrow.Name.Localized()}...done");

        return true;
    }

    /// <summary>
    /// Wrap <see cref="InventoryController.TryRunNetworkTransaction"/>
    /// with a timeout. The active-weapon shuffle (GClass2053 /
    /// RemoveWeaponOperation) occasionally hangs forever — fast-forward
    /// the player's operation queue so we don't deadlock.
    /// </summary>
    public async Task<IResult> TryRunNetworkTransactionWithTimeoutAsync(
        InventoryControllerResultStruct operationResult,
        Callback callback = null,
        CancellationToken token = default)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(NetworkTransactionTimeout));

        var networkTask = inventoryController.TryRunNetworkTransaction(operationResult, callback);

        await Task.WhenAny(networkTask, Task.Delay(Timeout.Infinite, timeoutSource.Token), Task.Delay(Timeout.Infinite, token));

        if (timeoutSource.Token.IsCancellationRequested)
        {
            var playerInvCont = (Player.PlayerInventoryController)inventoryController;
            if (log.WarningEnabled)
                log.LogWarning("Timed out on network transaction, trying to fast forward...");
            playerInvCont.Player_0.FastForwardCurrentOperations();
        }
        else
        {
            token.ThrowIfCancellationRequested();
        }

        return await networkTask;
    }

    /// <summary>
    /// Simulate the per-decision think delay players experience while
    /// looting. Default delay comes from
    /// <see cref="LootConfig.TransactionDelay"/>; callers override for
    /// the longer container-open kneel delay.
    /// </summary>
    public static Task SimulatePlayerDelayAsync(double delay = -1f, CancellationToken token = default)
    {
        if (delay == -1D)
            delay = LootConfig.TransactionDelay;

        return Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken: token);
    }
}
