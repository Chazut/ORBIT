using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Swap a body rig (TacticalVest slot) for a better one of the same type — simple rig vs simple rig, armored
/// rig vs armored rig. Cross-type transitions (simple + body armor ↔ armored rig) are out of scope: too much
/// state to track around dropping the body armor, redistributing items between containers of different
/// sizes, and overflowing the backpack for marginal benefit. Scavs only equip into empty slots.
/// </summary>
public static class RigSwapper
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
        var slot = equipment.GetSlot(EquipmentSlot.TacticalVest);
        if (slot == null || !slot.CheckCompatibility(candidate)) return WouldSwapResult.No;

        var current = slot.ContainedItem;
        if (current == null) return new WouldSwapResult(true, RigScorer.Score(candidate));

        // Same-type guard: a simple rig may only displace a simple rig; an armored rig may only displace an
        // armored rig.
        if (RigScorer.IsArmoredRig(candidate) != RigScorer.IsArmoredRig(current)) return WouldSwapResult.No;

        var candidateScore = RigScorer.Score(candidate);
        var currentScore = RigScorer.Score(current);
        const float margin = LootConfig.SwapMargin;
        var wouldSwap = candidateScore > currentScore * margin;
        if (!wouldSwap) return new WouldSwapResult(false, candidateScore);

        // Capacity guard: never swap into a rig that can't physically hold the bot's current carry — items
        // would be lost in the atomic Swap that follows.
        var candidateCapacity = RigScorer.TotalCells(candidate);
        var currentUsed = RigScorer.UsedCells(current);
        if (candidateCapacity < currentUsed) return new WouldSwapResult(false, candidateScore);

        return new WouldSwapResult(true, candidateScore, current);
    }

    public static async Task<Outcome> TryEquipOnlyAsync(BotOwner bot, Item candidate, CancellationToken ct)
    {
        if (bot == null || candidate == null) return Outcome.NotApplicable;
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        var slot = equipment?.GetSlot(EquipmentSlot.TacticalVest);
        if (slot == null || slot.ContainedItem != null) return Outcome.Skipped;
        if (!slot.CheckCompatibility(candidate)) return Outcome.Skipped;
        var nick = bot.Profile?.Nickname ?? "(no-nick)";
        Log.Info($"RigSwap.Equip({nick}): {candidate.LocalizedName()} → TacticalVest (empty)");
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
        var slot = equipment?.GetSlot(EquipmentSlot.TacticalVest);
        if (slot == null || !slot.CheckCompatibility(candidate)) return Outcome.Skipped;

        var current = slot.ContainedItem;
        if (current == null)
        {
            var candScore = RigScorer.Score(candidate, $"{nick}:CAND");
            Log.Info($"RigSwap({nick}): TacticalVest empty — equip {candidate.LocalizedName()} (score {candScore:F1})");
            var moved = await WeaponSwapper.MoveIntoSlotAsync(bot, candidate, slot, nick, ct);
            return moved ? Outcome.Swapped : Outcome.Skipped;
        }

        if (RigScorer.IsArmoredRig(candidate) != RigScorer.IsArmoredRig(current))
        {
            Log.Debug($"RigSwap({nick}): cross-type swap blocked — current={(RigScorer.IsArmoredRig(current) ? "armored" : "simple")}, candidate={(RigScorer.IsArmoredRig(candidate) ? "armored" : "simple")}");
            return Outcome.Skipped;
        }

        var candidateScore = RigScorer.Score(candidate, $"{nick}:CAND");
        var currentScore = RigScorer.Score(current, $"{nick}:CURRENT");
        const float margin = LootConfig.SwapMargin;
        if (candidateScore <= currentScore * margin)
        {
            Log.Debug($"RigSwap({nick}): keep {current.LocalizedName()}({currentScore:F1}) — candidate {candidate.LocalizedName()}({candidateScore:F1}) below margin {margin:F2}");
            return Outcome.Skipped;
        }

        var candidateCapacity = RigScorer.TotalCells(candidate);
        var currentUsed = RigScorer.UsedCells(current);
        if (candidateCapacity < currentUsed)
        {
            Log.Info($"RigSwap({nick}): SKIP {candidate.LocalizedName()} — candidate capacity ({candidateCapacity} cells) < current carry ({currentUsed} cells), items would be lost");
            return Outcome.Skipped;
        }

        // Move items from current rig → candidate rig BEFORE the atomic swap so the bot keeps their carry.
        // After the Swap, the candidate (now in our TacticalVest) holds our items; the emptied current rig
        // goes to the candidate's source address (typically the corpse).
        if (!await TransferRigContentsAsync(bot, current, candidate, nick, ct))
        {
            Log.Info($"RigSwap({nick}): SKIP {candidate.LocalizedName()} — could not transfer all items from {current.LocalizedName()}");
            return Outcome.Skipped;
        }

        Log.Info($"RigSwap({nick}): SWAP {current.LocalizedName()}({currentScore:F1}) → {candidate.LocalizedName()}({candidateScore:F1}, margin {margin:F2})");
        var ok = await WeaponSwapper.SwapInPlaceAsync(bot, candidate, current, nick, ct);
        return ok ? Outcome.Swapped : Outcome.Skipped;
    }

    /// <summary>
    /// Move every item out of <paramref name="oldRig"/>'s grids and into <paramref name="newRig"/>'s grids
    /// using QFAP. Returns false when any item can't be placed — the caller then aborts the swap so nothing
    /// is lost. We do this BEFORE the atomic Swap so the new rig (with the bot's items already inside) ends
    /// up in the bot's TacticalVest after the exchange.
    /// </summary>
    private static async Task<bool> TransferRigContentsAsync(BotOwner bot, Item oldRig, Item newRig, string nick, CancellationToken ct)
    {
        if (oldRig is not CompoundItem oldCompound || oldCompound.Grids == null) return true;
        var ic = bot.GetPlayer?.InventoryController;
        if (ic == null) return false;
        var newGrids = newRig is CompoundItem newCompound ? new List<CompoundItem> { newCompound } : null;
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

        Log.Debug($"RigSwap.Transfer({nick}): moving {itemsToMove.Count} item(s) from {oldRig.LocalizedName()} → {newRig.LocalizedName()}");
        foreach (var item in itemsToMove)
        {
            ct.ThrowIfCancellationRequested();
            var place = InteractionsHandlerClass.QuickFindAppropriatePlace(
                item, ic, newGrids,
                InteractionsHandlerClass.EMoveItemOrder.PickUp, true);
            if (!place.Succeeded)
            {
                Log.Debug($"RigSwap.Transfer({nick}): QFAP failed for {item.LocalizedName()} — aborting transfer");
                return false;
            }
            var ok = await WeaponSwapper.RunGuardedTransactionAsync(bot, place, $"RigTransfer({item.LocalizedName()})", nick, ct);
            if (!ok)
            {
                Log.Debug($"RigSwap.Transfer({nick}): network tx failed for {item.LocalizedName()} — aborting transfer");
                return false;
            }
        }
        return true;
    }
}
