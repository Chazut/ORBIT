using System.Threading;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using UnityEngine.Pool;

namespace Orbit.Inventory;

/// <summary>
/// Swap an item with an equipped one. Falls back to throw + equip if the
/// direct swap fails (BSG occasionally rejects edge cases — drop the
/// outgoing piece to the ground then equip the incoming one).
/// </summary>
public class LootSwapAction : LootRoutineAction
{
    private static readonly ObjectPool<LootSwapAction> _pool = new(
        Create,
        null,
        a => a.Reset(),
        ListActionPool.LogOnDestroyInstance,
        true,
        2,
        32);

    public static LootSwapAction Create() => new();

    public static LootSwapAction Rent(Item item, Item toSwap, float netWorthDelta = 0f, bool transferItems = false)
    {
        var swapAction = _pool.Get();
        swapAction.Item = item;
        swapAction.ToSwap = toSwap;
        swapAction.NetWorthDelta = netWorthDelta;
        swapAction.TransferItems = transferItems;
        return swapAction;
    }

    /// <summary>Currently-equipped item being swapped out.</summary>
    public Item ToSwap { get; set; }

    /// <summary>When true, the controller transfers items from
    /// <see cref="ToSwap"/> back into the bot's inventory after the
    /// swap (used when swapping a rig/backpack so its contents aren't
    /// orphaned).</summary>
    public bool TransferItems { get; set; }

    public override async Task<bool> ExecuteAsync(LootTransactionController controller, CancellationToken token)
    {
        if (await controller.SwapItemsAsync(Item, ToSwap, token))
            return true;

        // Swap failed. Try simulating throw+equip: check if the incoming
        // item could go in ToSwap's address, then roll back the
        // simulation. Only commit the real throw+equip if both legs
        // would succeed.
        var toSwapAddress = ToSwap.CurrentAddress;
        var inventoryController = ToSwap.Owner as InventoryController;
        var removeResult = InteractionsHandlerClass.Remove(ToSwap, inventoryController, false);
        var moveResult = InteractionsHandlerClass.Move(Item, toSwapAddress, inventoryController, false);

        moveResult.Value?.RollBack();
        removeResult.Value?.RollBack();

        if (moveResult.Failed)
            return false;

        return await controller.ThrowItemAsync(ToSwap, token) && await controller.TryEquipItemAsync(Item, token);
    }

    public override void Return() => _pool.Release(this);

    protected override void Reset()
    {
        base.Reset();
        ToSwap = null;
        TransferItems = false;
    }
}
