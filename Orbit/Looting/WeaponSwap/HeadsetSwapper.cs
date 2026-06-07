using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Decides whether an audio headset being looted should be equipped into the bot's Earpiece slot, replacing
/// the current item when the candidate scores higher by the margin. Scoring is handbook-price only — see
/// <see cref="HeadsetScorer"/>. No items to transfer (single atomic slot, no grids), so the path is just a
/// simple equip-or-swap mirroring helmet logic.
/// </summary>
public static class HeadsetSwapper
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
        var slot = equipment.GetSlot(EquipmentSlot.Earpiece);
        if (slot == null || !slot.CheckCompatibility(candidate)) return WouldSwapResult.No;

        var candidateScore = HeadsetScorer.Score(candidate);
        if (candidateScore <= 0f) return WouldSwapResult.No;

        var current = slot.ContainedItem;
        if (current == null) return new WouldSwapResult(true, candidateScore);

        var margin = LootConfig.HeadsetSwapMargin?.Value ?? 1.10f;
        var currentScore = HeadsetScorer.Score(current);
        var wouldSwap = candidateScore > currentScore * margin;
        return new WouldSwapResult(wouldSwap, candidateScore, wouldSwap ? current : null);
    }

    public static async Task<Outcome> TryEquipOnlyAsync(BotOwner bot, Item candidate, CancellationToken ct)
    {
        if (bot == null || candidate == null) return Outcome.NotApplicable;
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        var slot = equipment?.GetSlot(EquipmentSlot.Earpiece);
        if (slot == null || slot.ContainedItem != null) return Outcome.Skipped;
        if (!slot.CheckCompatibility(candidate)) return Outcome.Skipped;
        var nick = bot.Profile?.Nickname ?? "(no-nick)";
        Log.Info($"HeadsetSwap.Equip({nick}): {candidate.LocalizedName()} → Earpiece (empty)");
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
        var slot = equipment?.GetSlot(EquipmentSlot.Earpiece);
        if (slot == null || !slot.CheckCompatibility(candidate)) return Outcome.Skipped;

        var candidateScore = HeadsetScorer.Score(candidate, $"{nick}:CAND");
        if (candidateScore <= 0f) return Outcome.NotApplicable;

        var current = slot.ContainedItem;
        if (current == null)
        {
            Log.Info($"HeadsetSwap({nick}): Earpiece empty — equip {candidate.LocalizedName()} (score {candidateScore:F1})");
            var moved = await WeaponSwapper.MoveIntoSlotAsync(bot, candidate, slot, nick, ct);
            return moved ? Outcome.Swapped : Outcome.Skipped;
        }

        var margin = LootConfig.HeadsetSwapMargin?.Value ?? 1.10f;
        var currentScore = HeadsetScorer.Score(current, $"{nick}:CURRENT");
        if (candidateScore <= currentScore * margin)
        {
            Log.Debug($"HeadsetSwap({nick}): keep {current.LocalizedName()}({currentScore:F1}) — candidate {candidate.LocalizedName()}({candidateScore:F1}) below margin {margin:F2}");
            return Outcome.Skipped;
        }

        Log.Info($"HeadsetSwap({nick}): SWAP {current.LocalizedName()}({currentScore:F1}) → {candidate.LocalizedName()}({candidateScore:F1}, margin {margin:F2})");
        var ok = await WeaponSwapper.SwapInPlaceAsync(bot, candidate, current, nick, ct);
        return ok ? Outcome.Swapped : Outcome.Skipped;
    }
}
