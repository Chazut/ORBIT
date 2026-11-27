using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diz.LanguageExtensions;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;

namespace Orbit.Looting;

public partial class OrbitLootHandler
{
    private static double CargoValue(Item root, bool requireKnown)
    {
        double value = 0;
        foreach (var item in root.GetAllItems())
        {
            float unit = ItemPriceLookup.GetPrice(item);
            if (requireKnown && (unit <= 0 || item.QuestItem || item.PinLockState != EItemPinLockState.Free))
                return double.PositiveInfinity;
            value += unit * (double)Math.Max(1, item.StackObjectsCount);
        }
        return value;
    }

    private static bool CouldFit(Grid grid, Item item)
    {
        var size = item.CalculateRotatedSize(ItemRotation.Horizontal);
        return grid.CheckCompatibility(item) && ((size.X <= grid.GridWidth && size.Y <= grid.GridHeight)
            || (size.Y <= grid.GridWidth && size.X <= grid.GridHeight));
    }

    private static GridItemAddress CargoAddress(Grid[] grids, BackpackLayout.Rect rect) =>
        grids[rect.Grid].CreateItemAddress(new LocationInGrid(rect.X, rect.Y,
            rect.Turned ? ItemRotation.Vertical : ItemRotation.Horizontal));

    private static bool CanDropWhole(Item item, InventoryController ic) =>
        ic.CanThrow(item) && !(ic is IAntiRMTItemController limits && limits.GetItemsOverDiscardLimit(item).Any());

    private void CheckBackpackCombat(CancellationToken ct)
    {
        var memory = _bot.Memory;
        if (memory != null && (memory.HaveEnemy || memory.IsUnderFire))
            throw new OperationCanceledException(ct);
    }

    // The native throw can finish after combat cancels the sort. Account for a confirmed
    // drop on its original inventory even when the looting task has already returned.
    private async Task<bool> DropBackpackItemAsync(InventoryController ic, Item item, float worth)
    {
        var done = new TaskCompletionSource<bool>();
        ic.ThrowItem(item, downDirection: true, callback: r => done.TrySetResult(r.Succeed));
        if (!await done.Task || !HasLiveOwner || !ReferenceEquals(_player.InventoryController, ic) || item.Owner == ic)
            return false;
        Stats.InventoryValue -= worth;
        Stats.TotalGained = Stats.InventoryValue - Stats.SpawnValue;
        return true;
    }

    private async Task<OperationResult<IItemOperationResult>> FindCargoPlaceAsync(Item incoming, InventoryController ic, CancellationToken ct)
    {
        // A player can take a loose item during the kneel or move an item while a crate is searched.
        if (incoming?.CurrentAddress == null || ReferenceEquals(incoming.Owner, ic) || PackNStrapCompat.IsProtected(incoming))
            return new StringError("Loot source changed");
        if (CurrentTargetKind == LootKind.Item
            ? CurrentTarget is not LootItem world || world.Item != incoming || incoming.Owner is InventoryController
            : _currentSourceRoot == null || !incoming.IsChildOf(_currentSourceRoot))
            return new StringError("Loot left its source");
        var direct = RaidLootPlacement.Find(incoming, ic);
        if (direct.Succeeded) return direct;
        if (Log.DebugEnabled)
            Log.Debug($"Backpack({Nick}): no direct pickup for {incoming.LocalizedName()}, {DescribeFreeSpace(ic)}");
        var bagSlot = ic.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack);
        if (bagSlot?.ContainedItem is not CompoundItem bag || incoming.CurrentAddress == null
            || incoming.PinLockState != EItemPinLockState.Free || incoming.QuestItem || incoming.Owner == ic
            || !bag.Grids.Any(g => CouldFit(g, incoming))) return direct;

        var source = incoming.CurrentAddress;
        var owner = incoming.Owner;
        bool StillAvailable() => ReferenceEquals(bagSlot.ContainedItem, bag) && incoming.CurrentAddress != null
            && source.Equals(incoming.CurrentAddress) && ReferenceEquals(incoming.Owner, owner)
            && !PackNStrapCompat.IsProtected(incoming);

        var grids = bag.Grids;
        var items = new List<Item>();
        var entries = new List<BackpackLayout.Cargo>();
        for (int g = 0; g < grids.Length; g++)
        {
            var grid = grids[g];
            foreach (var resident in grid.Items.ToArray())
            {
                await LootWorkBudget.Checkpoint(ct);
                if (!StillAvailable() || resident.CurrentAddress is not GridItemAddress position || position.Grid != grid) return direct;
                var pos = position.LocationInGrid;
                var size = resident.CalculateRotatedSize(pos.r);
                items.Add(resident);
                entries.Add(new BackpackLayout.Cargo {
                    Original = new BackpackLayout.Rect(g, pos.x, pos.y, size.X, size.Y, pos.r == ItemRotation.Vertical),
                    Movable = resident.PinLockState == EItemPinLockState.Free,
                    Allowed = Array.ConvertAll(grids, dest => dest.CheckCompatibility(resident)),
                    DropCost = CanDropWhole(resident, ic) ? CargoValue(resident, true) : double.PositiveInfinity
                });
            }
        }
        var layout = new BackpackLayout(Array.ConvertAll(grids, g => (g.GridWidth, g.GridHeight)), entries);
        var shape = incoming.CalculateRotatedSize(ItemRotation.Horizontal);
        var value = CargoValue(incoming, false);
        BackpackLayout.Plan best = null;
        for (int g = 0; g < grids.Length; g++)
        {
            if (!CouldFit(grids[g], incoming)) continue;
            foreach (var rect in layout.Positions(g, shape.X, shape.Y))
            {
                await LootWorkBudget.Checkpoint(ct);
                best = layout.Consider(rect, value, best);
            }
        }
        CheckSession(ct);
        CheckBackpackCombat(ct);
        if (!StillAvailable()) return direct;
        direct = RaidLootPlacement.Find(incoming, ic);
        if (direct.Succeeded) return direct;
        // A full sort can move items outside the desired rectangle, including across grids.
        // Only submit it if EFT accepts the exact pickup in the sorted layout.
        if ((best == null || best.Drops.Count > 0) &&
            grids.Sum(g => g.GridWidth * g.GridHeight) - entries.Sum(e => e.Original.W * e.Original.H) >= shape.X * shape.Y)
        {
            EnsureInventoryAvailable(ct);
            var sorted = ItemManipulator.Sort(bag, ic, false);
            if (sorted.Succeeded)
            {
                bool fits;
                try { fits = RaidLootPlacement.Find(incoming, ic).Succeeded; }
                finally { sorted.Value.RollBack(); }
                if (fits)
                {
                    var transaction = LootActorGuard.TrackTransaction(_bot, ic.TryRunNetworkTransaction(sorted, null));
                    if (!(await LootActorGuard.AwaitTransaction(transaction, ct)).Succeed) return direct;
                    CheckSession(ct);
                    CheckBackpackCombat(ct);
                    Log.Debug($"Backpack({Nick}): sorted {bag.LocalizedName()} for {incoming.LocalizedName()}");
                    return StillAvailable() ? RaidLootPlacement.Find(incoming, ic) : direct;
                }
            }
        }
        if (best == null) return direct;
        bool AtOriginal(int i) => items[i].CurrentAddress?.Equals(CargoAddress(grids, entries[i].Original)) == true;
        if (best.Moves.Any(m => !AtOriginal(m.Index)) || best.Drops.Any(i => !AtOriginal(i))) return direct;

        // A physical drop needs a body. Use the same wake/retry as other inventory operations.
        if (best.Drops.Count != 0 && DormantMode)
        {
            NeedsAwakeInventory = true;
            ReportInventoryFailure(ct, true);
            throw new LootInventoryBusyException();
        }
        using var lease = _bodyGate.Enter(best.Drops.Count != 0);
        foreach (var move in best.Moves)
        {
            EnsureInventoryAvailable(ct);
            if (!StillAvailable() || !AtOriginal(move.Index)) return direct;
            var op = ItemManipulator.Move(items[move.Index], CargoAddress(grids, move.To), ic, true);
            if (!op.Succeeded || op.Value.ItemsDestroyRequired) return direct;
            var transaction = LootActorGuard.TrackTransaction(_bot, ic.TryRunNetworkTransaction(op, null));
            if (!(await LootActorGuard.AwaitTransaction(transaction, ct)).Succeed) return direct;
            CheckSession(ct);
            CheckBackpackCombat(ct);
            ReportInventoryProgress(ct);
        }
        var destination = CargoAddress(grids, best.Target);
        OperationResult<IItemOperationResult> FinalPlace()
        {
            if (!StillAvailable()) return direct;
            var op = ItemManipulator.Move(incoming, destination, ic, true);
            return op.Succeeded && !op.Value.ItemsDestroyRequired
                ? op.Cast<MoveResult, IItemOperationResult>() : direct;
        }

        // Verify the exact pickup (including EFT and mod restrictions) before ejecting anything.
        // Like EFT's simulated bag sort, this has no await or network event and is rolled back in reverse.
        bool CanFinishAfterDrops(int first, double alreadyDropped)
        {
            var removed = new List<ContainerRemoveResult>();
            double remainingCost = 0;
            try
            {
                for (int n = first; n < best.Drops.Count; n++)
                {
                    int i = best.Drops[n];
                    var item = items[i];
                    if (!AtOriginal(i) || !CanDropWhole(item, ic)) return false;
                    remainingCost += CargoValue(item, true);
                    var result = grids[entries[i].Original.Grid].Remove(item, false);
                    if (!result.Succeeded) return false;
                    removed.Add(result.Value);
                }
                return remainingCost + alreadyDropped < CargoValue(incoming, false) && FinalPlace().Succeeded;
            }
            finally { for (int n = removed.Count - 1; n >= 0; n--) removed[n].RollBack(); }
        }

        double spent = 0;
        for (int n = 0; n < best.Drops.Count; n++)
        {
            if (n > 0)
                await Task.Delay(UnityEngine.Random.Range(250, 451), ct);
            EnsureInventoryAvailable(ct);
            CheckBackpackCombat(ct);
            if (!StillAvailable()) return direct;
            if (n > 0)
            {
                var available = RaidLootPlacement.Find(incoming, ic);
                if (available.Succeeded) return available;
            }
            if (!CanFinishAfterDrops(n, spent)) return direct;
            var dropped = items[best.Drops[n]];
            double cost = CargoValue(dropped, true);
            float recordedWorth = dropped.GetAllItems().Sum(ItemPriceLookup.GetPrice);
            var transaction = LootActorGuard.TrackTransaction(_bot, DropBackpackItemAsync(ic, dropped, recordedWorth));
            if (!await LootActorGuard.AwaitTransaction(transaction, ct)) return direct;
            spent += cost;
            CheckSession(ct);
            CheckBackpackCombat(ct);
            ReportInventoryProgress(ct);
            Log.Debug($"Backpack({Nick}): dropped {dropped.LocalizedName()} for {incoming.LocalizedName()}");
        }
        var final = FinalPlace();
        if (final.Succeeded)
            Log.Debug($"Backpack({Nick}): made room for {incoming.LocalizedName()}, moved={best.Moves.Count}, dropped={best.Drops.Count}, displacedValue={best.Loss:N0}");
        return final;
    }
}
