using System.Threading;
using System.Threading.Tasks;
using EFT.InventoryLogic;

namespace Orbit.Inventory;

/// <summary>
/// Abstract base for the small inventory state-machine primitives the
/// loot controller uses to mutate the bot's gear: move into a slot,
/// swap with an equipped item, drop to ground. Subclasses are object-
/// pooled (see <see cref="ListActionPool"/>) so a typical loot pass
/// allocates zero new actions.
/// </summary>
public abstract class LootRoutineAction
{
    /// <summary>Item the action operates on.</summary>
    public Item Item { get; set; }

    /// <summary>Net-worth delta applied to the bot's stats on success.</summary>
    public float NetWorthDelta { get; set; }

    public abstract Task<bool> ExecuteAsync(LootTransactionController controller, CancellationToken token);

    public abstract void Return();

    protected virtual void Reset()
    {
        Item = null;
        NetWorthDelta = 0;
    }
}
