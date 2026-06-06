using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Orbit.Core;
using Orbit.Helpers;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Decides whether a Weapon being looted from a corpse / container / loose item should be equipped into one
/// of the bot's three weapon slots (FirstPrimary / SecondPrimary / Holster), displacing a worse weapon if
/// needed. Scavs are restricted to equipping into empty slots (no swap). PMCs and PlayerScavs use the full
/// score-and-displace path.
/// </summary>
public static class WeaponSwapper
{
    public enum Outcome
    {
        /// <summary>Caller should fall back to its default loot path.</summary>
        NotApplicable,
        /// <summary>Candidate moved into a weapon slot; caller must skip default placement.</summary>
        Swapped,
        /// <summary>Candidate explicitly rejected; caller must skip default placement (no stash).</summary>
        Skipped,
    }

    public readonly struct WouldSwapResult
    {
        public readonly bool WouldSwap;
        public readonly float CandidateScore;
        /// <summary>
        /// Weapon currently in the bot's loadout that would be thrown to the corpse by the swap (primary2 in
        /// full-rotate / primary2 swap, holster current in holster swap). Null when no weapon is displaced
        /// (promote into empty slot2, equip into empty slot, scav path). Callers can strip its mods before
        /// firing the swap so valuable attachments land in the bot's bag instead of being lost with the
        /// frame.
        /// </summary>
        public readonly Weapon DisplacedWeapon;
        public WouldSwapResult(bool would, float score, Weapon displaced = null) { WouldSwap = would; CandidateScore = score; DisplacedWeapon = displaced; }
        public static WouldSwapResult No => new(false, 0f, null);
    }

    /// <summary>
    /// Synchronous pre-check: would <paramref name="candidate"/> trigger a swap if fed to <see
    /// cref="TryHandleAsync"/>, or would it be rejected by margin / fit / no-downgrade rules? Lets callers
    /// strip mods off rejected weapons before the swap fires. Returns the candidate's score so callers can
    /// rank multiple would-swap weapons against each other.
    /// </summary>
    public static WouldSwapResult WouldSwap(BotOwner bot, Weapon candidate, Item rootSource)
    {
        if (bot == null || candidate == null) return WouldSwapResult.No;
        var player = bot.GetPlayer;
        if (player?.InventoryController == null) return WouldSwapResult.No;
        var profile = bot.Profile;
        if (profile == null) return WouldSwapResult.No;
        var equipment = player.Inventory?.Equipment;
        if (equipment == null) return WouldSwapResult.No;

        var primary1Slot = equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon);
        var primary2Slot = equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon);
        var holsterSlot = equipment.GetSlot(EquipmentSlot.Holster);

        var candidateFitsHolster = holsterSlot != null && holsterSlot.CheckCompatibility(candidate);
        var candidateFitsPrimary = (primary1Slot != null && primary1Slot.CheckCompatibility(candidate))
                                || (primary2Slot != null && primary2Slot.CheckCompatibility(candidate));
        if (!candidateFitsPrimary && !candidateFitsHolster) return WouldSwapResult.No;

        var mapId = Singleton<OrbitManager>.Instance?.MapId;
        var weights = MapWeaponWeights.Resolve(mapId);
        var sourceItems = new List<Item>(WeaponScorer.Walk(rootSource));
        var candidateScore = WeaponScorer.Score(candidate, sourceItems, weights);

        var isBotScav = profile.Side == EPlayerSide.Savage && !profile.WillBeAPlayerScav();
        if (isBotScav)
        {
            // Scavs only equip into empty slots. Iterate primary1 → primary2 → holster, return true on first
            // empty fitting slot.
            foreach (var sk in WeaponSlotsPrimaryFirst)
            {
                var s = equipment.GetSlot(sk);
                if (s == null || s.ContainedItem != null) continue;
                if (s.CheckCompatibility(candidate)) return new WouldSwapResult(true, candidateScore);
            }
            return WouldSwapResult.No;
        }

        var margin = LootConfig.WeaponSwapMargin?.Value ?? 1.10f;
        var inventoryItems = CollectBotInventoryAmmoPool(bot);

        var candidateAmmo = WeaponScorer.CollectUsableAmmo(candidate, sourceItems);
        if (candidateAmmo.BestPenetration < 20 && BotHasGoodPenWeapon(bot, inventoryItems))
            return WouldSwapResult.No;

        if (!candidateFitsPrimary && candidateFitsHolster)
        {
            var current = holsterSlot.ContainedItem as Weapon;
            if (current == null) return new WouldSwapResult(true, candidateScore);
            var currentScore = WeaponScorer.Score(current, inventoryItems, weights);
            var wouldSwap = candidateScore > currentScore * margin;
            return new WouldSwapResult(wouldSwap, candidateScore, wouldSwap ? current : null);
        }

        var current1 = primary1Slot?.ContainedItem as Weapon;
        var current2 = primary2Slot?.ContainedItem as Weapon;
        if (current1 == null) return new WouldSwapResult(true, candidateScore); // primary1 empty → equip, no displacement
        var score1 = WeaponScorer.Score(current1, inventoryItems, weights);
        if (current2 == null)
        {
            // Promote OR fill slot2 — slot2 was empty, so nothing displaced either way.
            return new WouldSwapResult(true, candidateScore);
        }
        var score2 = WeaponScorer.Score(current2, inventoryItems, weights);
        if (candidateScore > score1 * margin)
        {
            // Full rotate: swap1 throws current2 to the corpse, swap2 demotes current1 to slot2.
            return new WouldSwapResult(true, candidateScore, current2);
        }
        if (candidateScore > score2 * margin)
        {
            // Single swap into slot2: current2 goes to the corpse.
            return new WouldSwapResult(true, candidateScore, current2);
        }
        return new WouldSwapResult(false, candidateScore);
    }

    public static async Task<Outcome> TryHandleAsync(BotOwner bot, Weapon candidate, Item rootSource, CancellationToken ct)
    {
        Log.Info($"[TRACE] TryHandleAsync ENTER bot={bot?.Profile?.Nickname ?? "?"} candidate={candidate?.LocalizedName() ?? "?"}");
        if (bot == null || candidate == null) { Log.Info("[TRACE] TryHandleAsync EXIT(NotApplicable, null args)"); return Outcome.NotApplicable; }
        var player = bot.GetPlayer;
        if (player?.InventoryController == null) { Log.Info("[TRACE] TryHandleAsync EXIT(NotApplicable, no ic)"); return Outcome.NotApplicable; }
        var profile = bot.Profile;
        if (profile == null) { Log.Info("[TRACE] TryHandleAsync EXIT(NotApplicable, no profile)"); return Outcome.NotApplicable; }

        var nick = profile.Nickname ?? "(no-nick)";
        var isBotScav = profile.Side == EPlayerSide.Savage && !profile.WillBeAPlayerScav();
        TraceMark(nick, "TryHandleAsync");
        Log.Info($"[TRACE] TryHandleAsync({nick}): isBotScav={isBotScav}, dispatching");

        Outcome outcome;
        try
        {
            if (isBotScav)
                outcome = await TryEquipIntoFirstEmptySlotAsync(bot, candidate, nick, ct);
            else
                outcome = await EvaluateAndPerformAsync(bot, candidate, rootSource, nick, ct);
        }
        finally
        {
            TraceClear(nick);
        }
        Log.Info($"[TRACE] TryHandleAsync({nick}) EXIT outcome={outcome}");
        return outcome;
    }

    private static readonly EquipmentSlot[] WeaponSlotsPrimaryFirst =
    {
        EquipmentSlot.FirstPrimaryWeapon,
        EquipmentSlot.SecondPrimaryWeapon,
        EquipmentSlot.Holster,
    };

    /// <summary>
    /// Equip-only entry point: move <paramref name="candidate"/> into the first empty fitting weapon slot,
    /// never displace an already-equipped weapon. Used for container / loose-loot pickups where displacement
    /// is not appropriate — only corpse loot fires the full swap path.
    /// </summary>
    public static async Task<Outcome> TryEquipOnlyAsync(BotOwner bot, Weapon candidate, CancellationToken ct)
    {
        if (bot == null || candidate == null) return Outcome.NotApplicable;
        var nick = bot.Profile?.Nickname ?? "(no-nick)";
        return await TryEquipIntoFirstEmptySlotAsync(bot, candidate, nick, ct);
    }

    private static async Task<Outcome> TryEquipIntoFirstEmptySlotAsync(BotOwner bot, Weapon candidate, string nick, CancellationToken ct)
    {
        TraceMark(nick, "TryEquipIntoFirstEmptySlot");
        Log.Info($"[TRACE] TryEquipIntoFirstEmptySlot({nick}) ENTER");
        var equipment = bot.GetPlayer.Inventory.Equipment;
        foreach (var slotKind in WeaponSlotsPrimaryFirst)
        {
            Log.Info($"[TRACE] TryEquipIntoFirstEmptySlot({nick}): testing slot {slotKind}");
            var slot = equipment.GetSlot(slotKind);
            if (slot == null || slot.ContainedItem != null) { Log.Info($"[TRACE] TryEquipIntoFirstEmptySlot({nick}): {slotKind} occupied or null, skip"); continue; }
            if (!slot.CheckCompatibility(candidate)) { Log.Info($"[TRACE] TryEquipIntoFirstEmptySlot({nick}): {slotKind} rejects candidate, skip"); continue; }
            Log.Info($"WeaponSwap.Scav({nick}): equip {candidate.LocalizedName()} → {slotKind}");
            var moved = await MoveIntoSlotAsync(bot, candidate, slot, nick, ct);
            Log.Info($"[TRACE] TryEquipIntoFirstEmptySlot({nick}) EXIT moved={moved}");
            return moved ? Outcome.Swapped : Outcome.Skipped;
        }
        Log.Info($"[TRACE] TryEquipIntoFirstEmptySlot({nick}) EXIT skip (no empty compatible slot)");
        return Outcome.Skipped;
    }


    private static async Task<Outcome> EvaluateAndPerformAsync(BotOwner bot, Weapon candidate, Item rootSource, string nick, CancellationToken ct)
    {
        TraceMark(nick, "EvaluateAndPerformAsync");
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}) ENTER");
        var equipment = bot.GetPlayer.Inventory.Equipment;
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): got equipment, fetching slots");
        var primary1Slot = equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon);
        var primary2Slot = equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon);
        var holsterSlot = equipment.GetSlot(EquipmentSlot.Holster);
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): slots fetched (p1={primary1Slot != null}, p2={primary2Slot != null}, h={holsterSlot != null})");

        var mapId = Singleton<OrbitManager>.Instance?.MapId;
        var weights = MapWeaponWeights.Resolve(mapId);
        var margin = LootConfig.WeaponSwapMargin?.Value ?? 1.10f;
        Log.Debug($"WeaponSwap({nick}): map={mapId ?? "?"} weights=ergo×{weights.Ergo:F2} recoil×{weights.Recoil:F2} dist×{weights.EffectiveDist:F2} ammoQ×{weights.AmmoQuality:F2}, margin={margin:F2}");

        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): checking slot compatibilities");
        var candidateFitsHolster = holsterSlot != null && holsterSlot.CheckCompatibility(candidate);
        var candidateFitsPrimary = (primary1Slot != null && primary1Slot.CheckCompatibility(candidate))
                                || (primary2Slot != null && primary2Slot.CheckCompatibility(candidate));
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): fits primary={candidateFitsPrimary} fits holster={candidateFitsHolster}");

        if (!candidateFitsPrimary && !candidateFitsHolster)
        {
            Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}) EXIT Skipped (fits no slot)");
            return Outcome.Skipped;
        }

        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): collecting bot inventory ammo pool");
        var inventoryItems = CollectBotInventoryAmmoPool(bot);
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): collected {inventoryItems.Count} bot items, walking source");
        var sourceItems = WeaponScorer.Walk(rootSource);
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): walked source, scoring candidate");
        var candidateScore = WeaponScorer.Score(candidate, sourceItems, weights, $"{nick}:CAND");
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}): candidate scored {candidateScore:F1}, dispatching");

        // No-downgrade guard: if the candidate's ammo is sub-threshold and the bot already has a high-pen
        // weapon equipped, refuse the swap outright. Prevents cascade chains where SMGs / shotguns keep
        // displacing the bot's good rifle one slot at a time.
        var candidateAmmo = WeaponScorer.CollectUsableAmmo(candidate, sourceItems);
        if (candidateAmmo.BestPenetration < 20 && BotHasGoodPenWeapon(bot, inventoryItems))
        {
            Log.Info($"WeaponSwap({nick}): no-downgrade guard — candidate {candidate.LocalizedName()} (pen={candidateAmmo.BestPenetration}) rejected, bot already has a pen≥20 weapon");
            return Outcome.Skipped;
        }

        Outcome outcome;
        if (!candidateFitsPrimary && candidateFitsHolster)
            outcome = await HandleHolsterCandidateAsync(bot, candidate, holsterSlot, inventoryItems, weights, margin, candidateScore, rootSource, nick, ct);
        else
            outcome = await HandlePrimaryCandidateAsync(bot, candidate, primary1Slot, primary2Slot, inventoryItems, weights, margin, candidateScore, rootSource, nick, ct);
        Log.Info($"[TRACE] EvaluateAndPerformAsync({nick}) EXIT outcome={outcome}");
        return outcome;
    }

    private static async Task<Outcome> HandleHolsterCandidateAsync(
        BotOwner bot, Weapon candidate, Slot holsterSlot,
        List<Item> inventoryItems, WeaponWeights weights, float margin,
        float candidateScore, Item rootSource, string nick, CancellationToken ct)
    {
        var current = holsterSlot.ContainedItem as Weapon;
        if (current == null)
        {
            Log.Info($"WeaponSwap({nick}): holster empty — equip {candidate.LocalizedName()} (score {candidateScore:F1})");
            var moved = await MoveIntoSlotAsync(bot, candidate, holsterSlot, nick, ct);
            return moved ? Outcome.Swapped : Outcome.Skipped;
        }
        var currentScore = WeaponScorer.Score(current, inventoryItems, weights, $"{nick}:HOLSTER");
        if (candidateScore > currentScore * margin)
        {
            // Atomic positional swap: candidate moves to the holster, the previous holster weapon takes the
            // candidate's source address.
            Log.Info($"WeaponSwap({nick}): holster SWAP {current.LocalizedName()}({currentScore:F1}) → {candidate.LocalizedName()}({candidateScore:F1}, margin {margin:F2})");
            var ok = await SwapInPlaceAsync(bot, candidate, current, nick, ct);
            return ok ? Outcome.Swapped : Outcome.Skipped;
        }
        Log.Debug($"WeaponSwap({nick}): holster keep {current.LocalizedName()}({currentScore:F1}) — candidate {candidate.LocalizedName()}({candidateScore:F1}) below margin {margin:F2}");
        return Outcome.Skipped;
    }

    private static async Task<Outcome> HandlePrimaryCandidateAsync(
        BotOwner bot, Weapon candidate, Slot primary1Slot, Slot primary2Slot,
        List<Item> inventoryItems, WeaponWeights weights, float margin,
        float candidateScore, Item rootSource, string nick, CancellationToken ct)
    {
        var current1 = primary1Slot?.ContainedItem as Weapon;
        var current2 = primary2Slot?.ContainedItem as Weapon;

        // Primary1 empty: direct equip.
        if (current1 == null)
        {
            Log.Info($"WeaponSwap({nick}): primary1 empty — equip {candidate.LocalizedName()} (score {candidateScore:F1})");
            var moved = await MoveIntoSlotAsync(bot, candidate, primary1Slot, nick, ct);
            return moved ? Outcome.Swapped : Outcome.Skipped;
        }

        var score1 = WeaponScorer.Score(current1, inventoryItems, weights, $"{nick}:PRI1");

        // Primary2 empty: promote candidate into primary1 if it beats the current primary1 by the margin,
        // otherwise equip it as secondary. The promote path always equips into primary2 first then
        // atomic-swaps slot1↔slot2: primary1 never goes transiently empty, which the bot's hands controller
        // does not tolerate.
        if (current2 == null)
        {
            if (candidateScore > score1 * margin)
            {
                Log.Info($"WeaponSwap({nick}): promote candidate {candidate.LocalizedName()}({candidateScore:F1}) → primary1 (via temp slot2 + atomic swap, demote {current1.LocalizedName()}({score1:F1}))");
                if (!await MoveIntoSlotAsync(bot, candidate, primary2Slot, nick, ct)) return Outcome.Skipped;
                if (!await SwapInPlaceAsync(bot, candidate, current1, nick, ct))
                {
                    Log.Info($"WeaponSwap({nick}): atomic swap fallback — candidate stays as secondary, primary1 unchanged");
                    return Outcome.Swapped; // candidate is in slot2, still a valid pickup
                }
                return Outcome.Swapped;
            }
            Log.Info($"WeaponSwap({nick}): primary2 empty — equip {candidate.LocalizedName()}({candidateScore:F1}) as secondary (slot1 {current1.LocalizedName()}={score1:F1})");
            var movedSecondary = await MoveIntoSlotAsync(bot, candidate, primary2Slot, nick, ct);
            return movedSecondary ? Outcome.Swapped : Outcome.Skipped;
        }

        // Both primary slots occupied.
        var score2 = WeaponScorer.Score(current2, inventoryItems, weights, $"{nick}:PRI2");

        // Candidate beats primary1: full rotate via two atomic swaps. swap1 places the candidate in primary2
        // (current2 displaced to the candidate's source address); swap2 promotes the candidate to primary1
        // and demotes the previous primary1 to primary2. The sync + IsChangingWeapon poll between the two
        // ensures the bot's weapon-manager view is consistent before swap2.
        if (candidateScore > score1 * margin)
        {
            Log.Info($"WeaponSwap({nick}): full rotate — atomic swap1 {candidate.LocalizedName()}({candidateScore:F1}) ↔ {current2.LocalizedName()}({score2:F1}), then atomic swap2 with {current1.LocalizedName()}({score1:F1})");
            if (!await SwapInPlaceAsync(bot, candidate, current2, nick, ct)) return Outcome.Skipped;
            SyncBotBetweenSwaps(bot, nick);
            await WaitForIsChangingWeaponAsync(bot, nick, ct);
            if (!await SwapInPlaceAsync(bot, candidate, current1, nick, ct))
            {
                Log.Info($"WeaponSwap({nick}): second atomic swap fallback — candidate stays as secondary, primary1 unchanged");
                return Outcome.Swapped;
            }
            return Outcome.Swapped;
        }

        // Candidate beats primary2 only: single atomic swap displaces primary2 to the candidate's source
        // address.
        if (candidateScore > score2 * margin)
        {
            Log.Info($"WeaponSwap({nick}): primary2 swap — atomic {candidate.LocalizedName()}({candidateScore:F1}) ↔ {current2.LocalizedName()}({score2:F1})");
            var ok = await SwapInPlaceAsync(bot, candidate, current2, nick, ct);
            return ok ? Outcome.Swapped : Outcome.Skipped;
        }

        Log.Debug($"WeaponSwap({nick}): keep {current1.LocalizedName()}({score1:F1}) + {current2.LocalizedName()}({score2:F1}) — candidate {candidate.LocalizedName()}({candidateScore:F1}) below margin {margin:F2}");
        return Outcome.Skipped;
    }

    // BSG's TryRunNetworkTransaction can hang indefinitely on weapon ops; the await is raced against this
    // timeout and the hands controller is fast-forwarded on expiry to release any pending op.
    private const int NetworkTransactionTimeoutMs = 3000;

    // Settle window after a successful inventory transaction; chaining ops without this produces "Can not
    // execute" build failures and main-thread stalls.
    private const int PostTransactionSettleMs = 2500;

    // Tracks the swap operation each bot is currently executing. Snapshotted on every enter/exit so a cut-off
    // BepInEx log retains the most recent in-flight set just before a freeze.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _activeOps =
        new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

    private static void TraceMark(string nick, string method)
    {
        _activeOps[nick] = method;
        DumpActiveState();
    }

    private static void TraceClear(string nick)
    {
        _activeOps.TryRemove(nick, out _);
        DumpActiveState();
    }

    private static void DumpActiveState()
    {
        var snapshot = string.Join(", ", _activeOps.Select(kv => $"{kv.Key}:{kv.Value}"));
        Log.Info($"[TRACE-STATE] active=[{snapshot}] count={_activeOps.Count}");
    }

    private static async Task<bool> RunGuardedTransactionAsync<T>(BotOwner bot, GStruct154<T> op, string label, string nick, CancellationToken ct)
        where T : IRaiseEvents
    {
        var ic = bot.GetPlayer?.InventoryController;
        if (ic == null) return false;

        TraceMark(nick, $"RunGuardedTransaction.{label}");
        Log.Info($"WeaponSwap.{label}({nick}): tx STARTED (awaiting TryRunNetworkTransaction)");

        using var timeoutCts = new CancellationTokenSource(NetworkTransactionTimeoutMs);
        var networkTask = ic.TryRunNetworkTransaction(op, null);
        var winner = await Task.WhenAny(networkTask, Task.Delay(System.Threading.Timeout.Infinite, timeoutCts.Token), Task.Delay(System.Threading.Timeout.Infinite, ct));

        if (winner != networkTask)
        {
            // Timed out (or cancelled). Fast-forward to release any pending hands-controller op that the
            // network call was waiting on, then bail.
            Log.Warning($"WeaponSwap.{label}({nick}): tx TIMEOUT after {NetworkTransactionTimeoutMs}ms — fast-forwarding hands controller");
            try { bot.GetPlayer?.FastForwardCurrentOperations(); }
            catch (System.Exception ffEx) { Log.Warning($"WeaponSwap.{label}({nick}): FastForward THREW {ffEx.Message}"); }
            return false;
        }

        try
        {
            var result = await networkTask;
            if (!result.Succeed)
            {
                Log.Warning($"WeaponSwap.{label}({nick}): tx FAILED — {result.Error}");
                return false;
            }
            Log.Info($"WeaponSwap.{label}({nick}): tx DONE (succeeded), settling {PostTransactionSettleMs}ms");
            await Task.Delay(PostTransactionSettleMs, ct);
            return true;
        }
        catch (System.OperationCanceledException) { throw; }
        catch (System.Exception e)
        {
            Log.Warning($"WeaponSwap.{label}({nick}): tx THREW {e}");
            return false;
        }
    }

    /// <summary>
    /// Atomic positional swap of two items in their current addresses. Uses <see
    /// cref="InteractionsHandlerClass.Swap"/>, the same op the vanilla UI dispatches when dragging an item
    /// onto an already-occupied slot. Neither slot is transiently empty during the exchange.
    /// </summary>
    private static async Task<bool> SwapInPlaceAsync(BotOwner bot, Item itemA, Item itemB, string nick, CancellationToken ct)
    {
        TraceMark(nick, "SwapInPlace");
        Log.Info($"[TRACE] SwapInPlace({nick}) ENTER A={itemA?.LocalizedName() ?? "?"} B={itemB?.LocalizedName() ?? "?"}");
        var ic = bot.GetPlayer?.InventoryController;
        if (ic == null || itemA == null || itemB == null) { Log.Info($"[TRACE] SwapInPlace({nick}) EXIT false (null args)"); return false; }
        Log.Info($"[TRACE] SwapInPlace({nick}): fetching addresses");
        var addrA = itemA.CurrentAddress;
        var addrB = itemB.CurrentAddress;
        if (addrA == null || addrB == null)
        {
            Log.Warning($"WeaponSwap.Swap({nick}, {itemA.LocalizedName()} ↔ {itemB.LocalizedName()}): missing CurrentAddress on one of the items — aborting");
            Log.Info($"[TRACE] SwapInPlace({nick}) EXIT false (no address)");
            return false;
        }
        Log.Info($"[TRACE] SwapInPlace({nick}): describing addresses");
        var descA = DescribeAddress(addrA, bot);
        var descB = DescribeAddress(addrB, bot);
        Log.Info($"[TRACE] SwapInPlace({nick}): A@{descA} B@{descB}, building Swap op");
        var op = InteractionsHandlerClass.Swap(itemA, addrB, itemB, addrA, ic, true);
        Log.Info($"[TRACE] SwapInPlace({nick}): Swap op built, succeeded={op.Succeeded}");
        if (!op.Succeeded)
        {
            Log.Warning($"WeaponSwap.Swap({nick}, {itemA.LocalizedName()}@{descA} ↔ {itemB.LocalizedName()}@{descB}): build FAILED — {op.Error}");
            Log.Info($"[TRACE] SwapInPlace({nick}) EXIT false (build failed)");
            return false;
        }
        Log.Info($"[TRACE] SwapInPlace({nick}): handing off to RunGuardedTransaction");
        var ok = await RunGuardedTransactionAsync(bot, op, $"Swap({itemA.LocalizedName()}@{descA} ↔ {itemB.LocalizedName()}@{descB})", nick, ct);
        Log.Info($"[TRACE] SwapInPlace({nick}) EXIT {ok}");
        return ok;
    }

    private static async Task<bool> MoveIntoSlotAsync(BotOwner bot, Item item, Slot slot, string nick, CancellationToken ct)
    {
        TraceMark(nick, $"MoveIntoSlot({slot?.ID ?? "?"})");
        Log.Info($"[TRACE] MoveIntoSlot({nick}) ENTER item={item?.LocalizedName() ?? "?"} slot={slot?.ID ?? "?"}");
        var ic = bot.GetPlayer?.InventoryController;
        if (ic == null || item == null || slot == null) { Log.Info($"[TRACE] MoveIntoSlot({nick}) EXIT false (null args)"); return false; }
        var destSlotId = slot.ID ?? "?";
        Log.Info($"[TRACE] MoveIntoSlot({nick}): describing source address");
        var fromDesc = DescribeAddress(item.CurrentAddress, bot);
        Log.Info($"[TRACE] MoveIntoSlot({nick}): from={fromDesc}, checking compatibility");
        if (!slot.CheckCompatibility(item))
        {
            Log.Warning($"WeaponSwap.Move({nick}, {item.LocalizedName()} from {fromDesc} → {destSlotId}): slot rejects item");
            Log.Info($"[TRACE] MoveIntoSlot({nick}) EXIT false (incompatible)");
            return false;
        }
        Log.Info($"[TRACE] MoveIntoSlot({nick}): creating dest address");
        var address = slot.CreateItemAddress();
        Log.Info($"[TRACE] MoveIntoSlot({nick}): building Move op (simulate=true)");
        var op = InteractionsHandlerClass.Move(item, address, ic, true);
        Log.Info($"[TRACE] MoveIntoSlot({nick}): Move op built, succeeded={op.Succeeded}");
        if (!op.Succeeded)
        {
            Log.Warning($"WeaponSwap.Move({nick}, {item.LocalizedName()} from {fromDesc} → {destSlotId}): build FAILED — {op.Error}");
            Log.Info($"[TRACE] MoveIntoSlot({nick}) EXIT false (build failed)");
            return false;
        }
        Log.Info($"[TRACE] MoveIntoSlot({nick}): handing off to RunGuardedTransaction");
        var ok = await RunGuardedTransactionAsync(bot, op, $"Move({item.LocalizedName()}: {fromDesc}→{destSlotId})", nick, ct);
        Log.Info($"[TRACE] MoveIntoSlot({nick}) EXIT {ok}");
        return ok;
    }

    private static string DescribeAddress(ItemAddress address, BotOwner bot = null)
    {
        if (address == null) return "(no-addr)";
        try
        {
            var container = address.Container;
            if (container == null) return "(no-container)";
            var containerKind = container is Slot s ? $"slot:{s.ID}" : "grid";
            var ownerLabel = ResolveOwnerLabel(address, bot);
            return $"{containerKind}@{ownerLabel}";
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[TRACE] DescribeAddress THREW {ex.Message}");
            return "(addr-error)";
        }
    }

    private static string ResolveOwnerLabel(ItemAddress address, BotOwner bot)
    {
        try
        {
            var addressOwner = address?.GetOwnerOrNull();
            var botOwner = bot?.GetPlayer?.InventoryController;
            if (addressOwner != null && botOwner != null && ReferenceEquals(addressOwner, botOwner))
                return "bot";
            return "corpse/external";
        }
        catch
        {
            return "(owner-error)";
        }
    }

    // Refresh BSG's internal weapon-manager view between two atomic swaps of a full rotate. Without it, swap2
    // frequently lands on stale slot data and the BSG Swap op fails ("can not execute") even though the
    // inventory state is valid.
    private static void SyncBotBetweenSwaps(BotOwner bot, string nick)
    {
        try { bot.WeaponManager?.Selector?.UpdateWeaponsList(); }
        catch (System.Exception e) { Log.Warning($"WeaponSwap.SyncBetween({nick}): UpdateWeaponsList THREW {e.Message}"); }
        try { bot.AIData?.CalcPower(); }
        catch (System.Exception e) { Log.Warning($"WeaponSwap.SyncBetween({nick}): CalcPower THREW {e.Message}"); }
    }

    // Wait for the hands controller's IsChangingWeapon flag to clear before the next op. Polled instead of
    // blind-delayed so the typical sub-1s case doesn't hold up the bot.
    private static async Task WaitForIsChangingWeaponAsync(BotOwner bot, string nick, CancellationToken ct)
    {
        const int maxWaitMs = 5000;
        const int pollIntervalMs = 100;
        var ic = bot?.GetPlayer?.InventoryController;
        if (ic == null) return;
        var elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            bool changing;
            try { changing = ic.IsChangingWeapon; }
            catch { changing = false; }
            if (!changing) return;
            await Task.Delay(pollIntervalMs, ct);
            elapsed += pollIntervalMs;
        }
        Log.Warning($"WeaponSwap.WaitForIsChangingWeapon({nick}): hit {maxWaitMs}ms cap — proceeding");
    }

    private static bool BotHasGoodPenWeapon(BotOwner bot, List<Item> inventoryItems)
    {
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        if (equipment == null) return false;
        foreach (var slotKind in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
        {
            var slot = equipment.GetSlot(slotKind);
            if (slot?.ContainedItem is not Weapon w) continue;
            var ammo = WeaponScorer.CollectUsableAmmo(w, inventoryItems);
            if (ammo.BestPenetration >= 20) return true;
        }
        return false;
    }

    // Bot's full ammo pool: every item across equipment slots, including the secure container (part of
    // Inventory.Equipment).
    private static List<Item> CollectBotInventoryAmmoPool(BotOwner bot)
    {
        var list = new List<Item>();
        var equipment = bot.GetPlayer?.Inventory?.Equipment;
        if (equipment == null) return list;
        foreach (var item in equipment.GetAllItems())
            if (item != null) list.Add(item);
        return list;
    }
}
