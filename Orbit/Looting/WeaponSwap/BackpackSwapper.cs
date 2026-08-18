using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Swap the backpack (Backpack slot) for a bigger one. Pure capacity play — unlike rigs there is no
/// armored/simple type split, every bag competes with every bag. The candidate may still hold leftover
/// corpse junk that the value gate refused during phase 1, so the capacity guard works on FREE cells, not
/// total cells. Scavs only equip into empty slots.
/// </summary>
public static class BackpackSwapper
{
    public enum Outcome { NotApplicable, Swapped, Skipped }

    public readonly struct WouldSwapResult
    {
        public readonly bool WouldSwap;
        public readonly float CandidateScore;
        public readonly Item DisplacedItem;
        public WouldSwapResult(bool would, float score, Item displaced = null) { WouldSwap = would; CandidateScore = score; DisplacedItem = displaced; }
        public static WouldSwapResult No => new(false, 0f, null);
    }

    public static WouldSwapResult WouldSwap(BotOwner bot, Item candidate)
    {
        if (bot == null || candidate == null) return WouldSwapResult.No;
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        if (equipment == null) return WouldSwapResult.No;
        var slot = equipment.GetSlot(EquipmentSlot.Backpack);
        if (slot == null || !slot.CheckCompatibility(candidate)) return WouldSwapResult.No;

        var current = slot.ContainedItem;
        if (current == null) return new WouldSwapResult(true, BackpackScorer.Score(candidate));

        var candidateScore = BackpackScorer.Score(candidate);
        var currentScore = BackpackScorer.Score(current);
        const float margin = LootConfig.SwapMargin;
        var wouldSwap = candidateScore > currentScore * margin;
        if (!wouldSwap) return new WouldSwapResult(false, candidateScore);

        if (FreeCells(candidate) < RigScorer.UsedCells(current)) return new WouldSwapResult(false, candidateScore);

        return new WouldSwapResult(true, candidateScore, current);
    }

    public static async Task<Outcome> TryEquipOnlyAsync(BotOwner bot, Item candidate, CancellationToken ct)
    {
        if (bot == null || candidate == null) return Outcome.NotApplicable;
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        var slot = equipment?.GetSlot(EquipmentSlot.Backpack);
        if (slot == null || slot.ContainedItem != null) return Outcome.Skipped;
        if (!slot.CheckCompatibility(candidate)) return Outcome.Skipped;
        var nick = bot.Profile?.Nickname ?? "(no-nick)";
        Log.Info($"BackpackSwap.Equip({nick}): {candidate.LocalizedName()} → Backpack (empty)");
        var ok = await WeaponSwapper.MoveIntoSlotAsync(bot, candidate, slot, nick, ct);
        return ok ? Outcome.Swapped : Outcome.Skipped;
    }

    public static async Task<Outcome> TryHandleAsync(BotOwner bot, Item candidate, CancellationToken ct)
    {
        if (bot == null || candidate == null) return Outcome.NotApplicable;
        var profile = bot.Profile;
        if (profile == null) return Outcome.NotApplicable;
        var nick = profile.Nickname ?? "(no-nick)";
        var isBotScav = profile.Side == EPlayerSide.Savage && !profile.WillBeAPlayerScav();
        if (isBotScav) return await TryEquipOnlyAsync(bot, candidate, ct);

        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        var slot = equipment?.GetSlot(EquipmentSlot.Backpack);
        if (slot == null || !slot.CheckCompatibility(candidate)) return Outcome.Skipped;

        var current = slot.ContainedItem;
        if (current == null)
        {
            var candScore = BackpackScorer.Score(candidate, $"{nick}:CAND");
            Log.Info($"BackpackSwap({nick}): Backpack empty — equip {candidate.LocalizedName()} (score {candScore:F1})");
            var moved = await WeaponSwapper.MoveIntoSlotAsync(bot, candidate, slot, nick, ct);
            return moved ? Outcome.Swapped : Outcome.Skipped;
        }

        var candidateScore = BackpackScorer.Score(candidate, $"{nick}:CAND");
        var currentScore = BackpackScorer.Score(current, $"{nick}:CURRENT");
        const float margin = LootConfig.SwapMargin;
        if (candidateScore <= currentScore * margin)
        {
            Log.Debug($"BackpackSwap({nick}): keep {current.LocalizedName()}({currentScore:F1}) — candidate {candidate.LocalizedName()}({candidateScore:F1}) below margin {margin:F2}");
            return Outcome.Skipped;
        }

        var candidateFree = FreeCells(candidate);
        var currentUsed = RigScorer.UsedCells(current);
        if (candidateFree < currentUsed)
        {
            Log.Info($"BackpackSwap({nick}): SKIP {candidate.LocalizedName()} — candidate free space ({candidateFree} cells) < current carry ({currentUsed} cells), items would be lost");
            return Outcome.Skipped;
        }

        // Move items from current bag → candidate bag BEFORE the atomic swap so the bot keeps their carry.
        // After the Swap, the candidate (now in our Backpack slot) holds our items; the emptied current bag
        // goes to the candidate's source address (typically the corpse). Leftover value-gated junk inside
        // the candidate rides along into our inventory — accepted, it came with the bag.
        if (!await TransferBackpackContentsAsync(bot, current, candidate, nick, ct))
        {
            Log.Info($"BackpackSwap({nick}): SKIP {candidate.LocalizedName()} — could not transfer all items from {current.LocalizedName()}");
            return Outcome.Skipped;
        }

        Log.Info($"BackpackSwap({nick}): SWAP {current.LocalizedName()}({currentScore:F1}) → {candidate.LocalizedName()}({candidateScore:F1}, margin {margin:F2})");
        var ok = await WeaponSwapper.SwapInPlaceAsync(bot, candidate, current, nick, ct);
        return ok ? Outcome.Swapped : Outcome.Skipped;
    }

    /// <summary>
    /// Cells not currently occupied in this bag's grids. The corpse bag keeps whatever the phase 1 value
    /// gate refused, so total capacity overstates what our carry can move into.
    /// </summary>
    private static int FreeCells(Item backpack)
        => RigScorer.TotalCells(backpack) - RigScorer.UsedCells(backpack);

    /// <summary>
    /// Move every item out of <paramref name="oldBag"/>'s grids into <paramref name="newBag"/>'s grids using
    /// QFAP. Returns false when any item can't be placed — the caller then aborts the swap so nothing is
    /// lost. Done BEFORE the atomic Swap so the new bag (with the bot's items already inside) ends up in the
    /// bot's Backpack slot after the exchange.
    /// </summary>
    private static async Task<bool> TransferBackpackContentsAsync(BotOwner bot, Item oldBag, Item newBag, string nick, CancellationToken ct)
    {
        if (oldBag is not CompoundItem oldCompound || oldCompound.Grids == null) return true;
        var ic = bot.GetPlayer?.InventoryController;
        if (ic == null) return false;
        var newGrids = newBag is CompoundItem newCompound ? new List<CompoundItem> { newCompound } : null;
        if (newGrids == null) return false;

        var itemsToMove = new List<Item>();
        for (var g = 0; g < oldCompound.Grids.Length; g++)
        {
            var grid = oldCompound.Grids[g];
            if (grid?.Items == null) continue;
            foreach (var item in grid.Items)
                if (item != null) itemsToMove.Add(item);
        }
        if (itemsToMove.Count == 0) return true;

        Log.Debug($"BackpackSwap.Transfer({nick}): moving {itemsToMove.Count} item(s) from {oldBag.LocalizedName()} → {newBag.LocalizedName()}");
        foreach (var item in itemsToMove)
        {
            ct.ThrowIfCancellationRequested();
            var place = ItemManipulator.QuickFindAppropriatePlace(
                item, ic, newGrids,
                ItemManipulator.EMoveItemOrder.PickUp, true);
            if (!place.Succeeded)
            {
                Log.Debug($"BackpackSwap.Transfer({nick}): QFAP failed for {item.LocalizedName()} — aborting transfer");
                return false;
            }
            var ok = await WeaponSwapper.RunGuardedTransactionAsync(bot, place, $"BackpackTransfer({item.LocalizedName()})", nick, ct);
            if (!ok)
            {
                Log.Debug($"BackpackSwap.Transfer({nick}): network tx failed for {item.LocalizedName()} — aborting transfer");
                return false;
            }
        }
        return true;
    }
}
