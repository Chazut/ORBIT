namespace Orbit.Looting;

public partial class OrbitLootHandler
{
    internal void CheckInventoryForSession(System.Threading.CancellationToken ct)
    {
        if (OwnsSession(ct) && LootTaskRunning) EnsureInventoryAvailable(ct);
    }

    internal void EnsureInventoryAvailable(System.Threading.CancellationToken ct)
    {
        CheckSession(ct);
        var inventory = _player.InventoryController;
        bool blocked = inventory.IsInventoryBlocked();
        if (!blocked)
            foreach (var operation in inventory.SelectEvents<EFT.InventoryLogic.ItemEventArgs>()) { blocked = true; break; }
        if (!blocked) return;
        NeedsAwakeInventory |= DormantMode;
        ReportInventoryFailure(ct, true);
        throw new LootInventoryBusyException();
    }

}
