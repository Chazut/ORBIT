using System.Threading;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using UnityEngine.Pool;

namespace Orbit.Inventory;

/// <summary>
/// Drop an item to the ground. Used to discard low-value contents of a
/// rig/backpack before swapping into it, and to strip-then-leave a
/// weapon's attachments.
/// </summary>
public class LootThrowAction : LootRoutineAction
{
    private static readonly ObjectPool<LootThrowAction> _pool = new(
        Create,
        null,
        a => a.Reset(),
        ListActionPool.LogOnDestroyInstance,
        true,
        32);

    public static LootThrowAction Create() => new();

    public static LootThrowAction Rent(Item item, float netWorthDelta = 0f, bool transferItems = true)
    {
        var throwAction = _pool.Get();
        throwAction.Item = item;
        throwAction.NetWorthDelta = netWorthDelta;
        throwAction.TransferItems = transferItems;
        return throwAction;
    }

    /// <summary>When true, the controller transfers the thrown item's
    /// nested contents back into the bot's inventory before the drop.</summary>
    public bool TransferItems { get; set; }

    public override Task<bool> ExecuteAsync(LootTransactionController controller, CancellationToken token)
        => controller.ThrowItemAsync(Item, token);

    public override void Return() => _pool.Release(this);

    protected override void Reset()
    {
        base.Reset();
        TransferItems = false;
    }
}
