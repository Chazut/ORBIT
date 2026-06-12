using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Decides whether a body-armor or helmet item being looted from a corpse should be equipped into the bot's
/// ArmorVest or Headwear slot, replacing the current item when the candidate scores higher by the margin.
/// Scavs equip into empty slots only (no displacement); PMCs and PlayerScavs use the full score-and-displace
/// path. Plates inside body armor stay where they are — only the host is moved.
/// </summary>
public static class ArmorSwapper
{
    public enum Outcome
    {
        /// <summary>Caller should fall back to its default loot path.</summary>
        NotApplicable,
        /// <summary>Candidate moved into the gear slot; caller must skip default placement.</summary>
        Swapped,
        /// <summary>Candidate explicitly rejected; caller must skip default placement.</summary>
        Skipped,
    }

    public readonly struct WouldSwapResult
    {
        public readonly bool WouldSwap;
        public readonly float CandidateScore;
        /// <summary>
        /// Item currently equipped in the target slot that would be thrown to the corpse by the swap. Null
        /// when the slot is empty (no displacement). Callers can use this for logging or future strip logic.
        /// </summary>
        public readonly Item DisplacedItem;
        public WouldSwapResult(bool would, float score, Item displaced = null) { WouldSwap = would; CandidateScore = score; DisplacedItem = displaced; }
        public static WouldSwapResult No => new(false, 0f, null);
    }

    /// <summary>
    /// Synchronous pre-check: would <paramref name="candidate"/> trigger a swap into <paramref name="slotKind"/>
    /// if dispatched right now? Returns the candidate's score plus the item that would be displaced (if any)
    /// so callers can rank multiple slots against each other.
    /// </summary>
    public static WouldSwapResult WouldSwap(BotOwner bot, Item candidate, EquipmentSlot slotKind)
    {
        if (bot == null || candidate == null) return WouldSwapResult.No;
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        if (equipment == null) return WouldSwapResult.No;
        var slot = equipment.GetSlot(slotKind);
        if (slot == null) return WouldSwapResult.No;
        if (!slot.CheckCompatibility(candidate)) return WouldSwapResult.No;

        var candidateScore = ArmorScorer.Score(candidate);
        if (candidateScore <= 0f) return WouldSwapResult.No; // no ArmorComponent — not gear we care about

        var current = slot.ContainedItem;
        if (current == null) return new WouldSwapResult(true, candidateScore);

        const float margin = LootConfig.SwapMargin;
        var currentScore = ArmorScorer.Score(current);
        var wouldSwap = candidateScore > currentScore * margin;
        return new WouldSwapResult(wouldSwap, candidateScore, wouldSwap ? current : null);
    }

    /// <summary>
    /// Equip-only entry point: move <paramref name="candidate"/> into <paramref name="slotKind"/> if the slot
    /// is empty, otherwise skip. Used for container / loose-loot pickups where displacement is not
    /// appropriate — corpse loot uses <see cref="TryHandleAsync"/> for the full path.
    /// </summary>
    public static async Task<Outcome> TryEquipOnlyAsync(BotOwner bot, Item candidate, EquipmentSlot slotKind, CancellationToken ct)
    {
        if (bot == null || candidate == null) return Outcome.NotApplicable;
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        if (equipment == null) return Outcome.NotApplicable;
        var slot = equipment.GetSlot(slotKind);
        if (slot == null || slot.ContainedItem != null) return Outcome.Skipped;
        if (!slot.CheckCompatibility(candidate)) return Outcome.Skipped;

        var nick = bot.Profile?.Nickname ?? "(no-nick)";
        Log.Info($"ArmorSwap.Equip({nick}): {candidate.LocalizedName()} → {slotKind} (empty)");
        var ok = await WeaponSwapper.MoveIntoSlotAsync(bot, candidate, slot, nick, ct);
        return ok ? Outcome.Swapped : Outcome.Skipped;
    }

    /// <summary>
    /// Full swap path: evaluate against the current item in <paramref name="slotKind"/> and perform an atomic
    /// positional swap if the candidate beats the margin. Scavs short-circuit to equip-only — they don't
    /// displace gear.
    /// </summary>
    public static async Task<Outcome> TryHandleAsync(BotOwner bot, Item candidate, EquipmentSlot slotKind, CancellationToken ct)
    {
        if (bot == null || candidate == null) return Outcome.NotApplicable;
        var player = bot.GetPlayer;
        if (player?.InventoryController == null) return Outcome.NotApplicable;
        var profile = bot.Profile;
        if (profile == null) return Outcome.NotApplicable;

        var nick = profile.Nickname ?? "(no-nick)";
        var isBotScav = profile.Side == EPlayerSide.Savage && !profile.WillBeAPlayerScav();
        if (isBotScav)
            return await TryEquipOnlyAsync(bot, candidate, slotKind, ct);

        var equipment = player.Inventory?.Equipment;
        var slot = equipment?.GetSlot(slotKind);
        if (slot == null || !slot.CheckCompatibility(candidate)) return Outcome.Skipped;

        var candidateScore = ArmorScorer.Score(candidate, $"{nick}:CAND");
        if (candidateScore <= 0f) return Outcome.NotApplicable; // not an armored item

        var current = slot.ContainedItem;
        if (current == null)
        {
            Log.Info($"ArmorSwap({nick}): {slotKind} empty — equip {candidate.LocalizedName()} (score {candidateScore:F1})");
            var moved = await WeaponSwapper.MoveIntoSlotAsync(bot, candidate, slot, nick, ct);
            return moved ? Outcome.Swapped : Outcome.Skipped;
        }

        const float margin = LootConfig.SwapMargin;
        var currentScore = ArmorScorer.Score(current, $"{nick}:CURRENT");
        if (candidateScore <= currentScore * margin)
        {
            Log.Debug($"ArmorSwap({nick}): keep {current.LocalizedName()}({currentScore:F1}) — candidate {candidate.LocalizedName()}({candidateScore:F1}) below margin {margin:F2}");
            return Outcome.Skipped;
        }

        Log.Info($"ArmorSwap({nick}): {slotKind} SWAP {current.LocalizedName()}({currentScore:F1}) → {candidate.LocalizedName()}({candidateScore:F1}, margin {margin:F2}) via atomic Swap");
        var ok = await WeaponSwapper.SwapInPlaceAsync(bot, candidate, current, nick, ct);
        return ok ? Outcome.Swapped : Outcome.Skipped;
    }
}
