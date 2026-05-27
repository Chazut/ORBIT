using System.Threading;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using UnityEngine.Pool;

namespace Orbit.Inventory;

/// <summary>Move an item to a specific address, or equip it if no
/// address is supplied.</summary>
public class LootMoveAction : LootRoutineAction
{
    private static readonly ObjectPool<LootMoveAction> _pool = new(
        Create,
        null,
        a => a.Reset(),
        ListActionPool.LogOnDestroyInstance,
        true,
        2,
        32);

    public static LootMoveAction Create() => new();

    public static LootMoveAction Rent(Item item, ItemAddress place = null, float netWorthDelta = 0f)
    {
        var moveAction = _pool.Get();
        moveAction.Item = item;
        moveAction.Place = place;
        moveAction.NetWorthDelta = netWorthDelta;
        return moveAction;
    }

    /// <summary>Target address. Null → equip.</summary>
    public ItemAddress Place { get; set; }

    public override Task<bool> ExecuteAsync(LootTransactionController controller, CancellationToken token)
        => controller.MoveItemAsync(Item, Place, token);

    public override void Return() => _pool.Release(this);

    protected override void Reset()
    {
        base.Reset();
        Place = null;
    }
}
