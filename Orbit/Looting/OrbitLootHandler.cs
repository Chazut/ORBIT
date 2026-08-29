using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Orbit.Core;
using Orbit.Helpers;
using Orbit.Looting.WeaponSwap;
using UnityEngine;

namespace Orbit.Looting;

public class OrbitLootHandler : MonoBehaviour, ILootHandler
{
    // Fallback per-slot threshold for bots without an archetype-resolved value (PlayerScavs, or PMCs while
    // SAIN attach is pending).
    internal const float DefaultMinPickupPrice = 5000f;
    private const int ContainerOpenAnimMs = 2500;
    private const int InitialSearchMs = 1500;
    private const int PerItemRevealMs = 400;
    private const int MaxRevealCapMs = 8000;
    private const int InstantGrabDelayMs = 800;

    private static readonly EquipmentSlot[] CorpseLootableSlotsPmc =
    {
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear,
        EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack,
        EquipmentSlot.Pockets, EquipmentSlot.Dogtag,
    };

    // Non-PMC corpses keep Scabbard (melee) lootable. PMC melee is body-bound in live and excluded above.
    private static readonly EquipmentSlot[] CorpseLootableSlotsNonPmc =
    {
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear,
        EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack,
        EquipmentSlot.Pockets, EquipmentSlot.Scabbard, EquipmentSlot.Dogtag,
    };

    // Slots that require a search animation (contents hidden until inspected). Other slots are visible on the
    // body and grabbed without a reveal cycle.
    private static readonly HashSet<EquipmentSlot> SearchableCorpseSlots = new()
    {
        EquipmentSlot.TacticalVest, EquipmentSlot.ArmorVest,
        EquipmentSlot.Backpack, EquipmentSlot.Pockets,
    };

    private BotOwner _bot;
    private CancellationTokenSource _cts;

    /// <summary>
    /// Timestamp of the last observed activity in the current loot session — bumped on session start,
    /// on every successful pickup, on every drain-entry evaluation (TAKE / SKIP / SWAP), and on every
    /// async-tx completion. If the loot session is alive (<see cref="LootTaskRunning"/> true) and no
    /// activity has been observed for more than <see cref="LootSessionInactivityTimeoutSeconds"/>, the
    /// Unity Update loop cancels the session — covers the loose-loot hang pattern (observed when
    /// PickupLooseAsync hangs silently after a Task.Delay, leaving the bot pinned crouched for
    /// minutes). Container / corpse sessions can run for minutes legitimately as long as the drain
    /// loop keeps making forward progress; this watchdog ONLY fires when there's no progress at all.
    /// </summary>
    private float _lastLootActivityTime;

    private const float LootSessionInactivityTimeoutSeconds = 30f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BumpLootActivity() => _lastLootActivityTime = Time.realtimeSinceStartup;

    // Current loot source root (corpse equipment / container fixture / loose item). Set per LootXxxAsync
    // entry; used by the swap evaluator to size the candidate's reachable ammo pool.
    private Item _currentSourceRoot;
    // Ids of weapons consumed by a successful swap this session. Subsequent drain entries that descend from
    // them are skipped so the swapped weapon's mods aren't stripped off in the bot's inventory by a stray
    // QFAP transfer.
    private readonly HashSet<string> _swappedWeaponIds = new();

    // Gates the full swap path inside TransferItemAsync. Only the corpse phase 4 entry sets this true, so
    // weapons encountered during multi-item drains (container, corpse phase 1) cannot trigger a swap
    // mid-drain.
    private bool _allowWeaponSwapPath;

    // Phase 4 (mag compat) — the set of weapons the bot will have AFTER all swaps this session resolve.
    // Computed once at corpse / container session start (post weapon-hoist, pre phase 1 drain) and read by
    // ValidatePickup to bypass the value gate for mags / ammo that fit any of those weapons. Excludes
    // weapons that will be displaced into the source (so we don't keep grabbing mags for a gun we're about
    // to discard) and includes phase 2 candidate winners (so we DO grab mags for the gun we're about to
    // equip).
    private readonly List<Weapon> _postSwapWeapons = new();


    public LootStats Stats { get; } = new();
    public bool LootTaskRunning { get; private set; }

    /// <summary>True when the last session ended through a cancel (combat interrupt, watchdog hang
    /// cancel, external stop) rather than running to completion. BotLootState turns this into a FAILED
    /// loot so the squad blacklists the POI — without it a hang-cancelled loose item was re-picked
    /// forever (observed: 'doom' looping 6 times on the same sewing kit).</summary>
    public bool LastSessionCancelled { get; private set; }

    public InteractableObject CurrentTarget { get; set; }
    public LootKind CurrentTargetKind { get; set; } = LootKind.None;
    public Vector3 ApproachPosition { get; set; }
    public Vector3 TargetWorldPosition { get; set; }
    public bool ForceEnabled { get; set; }

    /// <summary>See <see cref="ILootHandler.DormantMode"/>. Set per session by BotLootState.</summary>
    public bool DormantMode { get; set; }

    private string Nick => _bot?.GetPlayer?.Profile?.Nickname ?? "(no-bot)";

    public void Init(BotOwner bot)
    {
        _bot = bot;
        Stats.SpawnValue = ItemPriceLookup.SumInventoryWorth(bot);
        Stats.InventoryValue = Stats.SpawnValue;
        Stats.TotalGained = 0f;
        Log.Debug($"OrbitLootHandler.Init({Nick}): spawnValue={Stats.SpawnValue:N0}₽, minPickupFallback={DefaultMinPickupPrice:N0}₽");
    }

    /// <summary>
    /// Unity Update — runs the loot-session inactivity watchdog. If the session has been alive for more
    /// than <see cref="LootSessionInactivityTimeoutSeconds"/> without any activity (no pickup, no drain
    /// entry processed, no tx completion), assume the session is hung and cancel it. The activity-based
    /// gate is what lets corpse / container sessions legitimately run for minutes — they keep processing
    /// drain entries, each one bumps the timer.
    /// </summary>
    private void Update()
    {
        if (!LootTaskRunning) return;
        var elapsedSinceActivity = Time.realtimeSinceStartup - _lastLootActivityTime;
        if (elapsedSinceActivity < LootSessionInactivityTimeoutSeconds) return;

        Log.Warning($"OrbitLootHandler.Watchdog({Nick}): loot session HUNG — no activity for {elapsedSinceActivity:F1}s on {CurrentTarget?.name} (kind={CurrentTargetKind}) → cancelling. Bot will unfreeze + re-dispatch.");
        // Push the activity timer forward so we don't re-fire the warning every frame while
        // RunAsync's finally block tears down the session (UnfreezeBot + log emit can take a couple
        // frames). The cancellation drives the actual cleanup; this just suppresses the spam.
        _lastLootActivityTime = Time.realtimeSinceStartup;
        LastSessionCancelled = true;
        _cts?.Cancel();
    }

    public void StartLooting()
    {
        if (LootTaskRunning)
        {
            Log.Debug($"OrbitLootHandler.StartLooting({Nick}): IGNORED — already running");
            return;
        }
        if (_bot == null || CurrentTarget == null || CurrentTargetKind == LootKind.None)
        {
            Log.Warning($"OrbitLootHandler.StartLooting({Nick}): IGNORED — bot={_bot != null}, target={CurrentTarget?.name}, kind={CurrentTargetKind}");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        LootTaskRunning = true;
        LastSessionCancelled = false;
        Stats.LastItemsTaken = false;
        Stats.LastMaxPerSlotSeen = 0f;
        Stats.LastHadBypassItem = false;
        _swappedWeaponIds.Clear();
        BumpLootActivity();

        Log.Debug($"OrbitLootHandler.StartLooting({Nick}): kind={CurrentTargetKind}, target={CurrentTarget.name}, minPrice={GetMinPickupPrice():N0}₽");

        var loot = CurrentTarget;
        var kind = CurrentTargetKind;
        var ct = _cts.Token;

        _ = RunAsync(loot, kind, ct);
    }

    private async Task RunAsync(InteractableObject loot, LootKind kind, CancellationToken ct)
    {
        var sw = Time.realtimeSinceStartup;
        var takenBefore = Stats.LastItemsTaken;
        // Pin the bot stationary for the loot session. BSG's BotMover.SetPlayerToNavMesh ticks in LateUpdate
        // and can throw on a bot whose movement state is in flux while weapon slots are being mutated; pose=0
        // + Mover.Pause keeps it quiescent.
        if (!DormantMode)
        {
            FreezeBotForLootSession();
            LookAtLootTarget(loot);
        }
        try
        {
            switch (kind)
            {
                case LootKind.Container:
                    if (loot is LootableContainer container)
                        await LootContainerAsync(container, ct);
                    else
                        Log.Warning($"OrbitLootHandler.RunAsync({Nick}): kind=Container but target is {loot?.GetType().Name}");
                    break;
                case LootKind.Corpse:
                    if (loot is Corpse corpse)
                        await LootCorpseAsync(corpse, ct);
                    else
                        Log.Warning($"OrbitLootHandler.RunAsync({Nick}): kind=Corpse but target is {loot?.GetType().Name}");
                    break;
                case LootKind.Item:
                    if (loot is LootItem lootItem)
                        await LootLooseItemAsync(lootItem, ct);
                    else
                        Log.Warning($"OrbitLootHandler.RunAsync({Nick}): kind=Item but target is {loot?.GetType().Name}");
                    break;
            }
        }
        catch (System.OperationCanceledException)
        {
            Log.Debug($"OrbitLootHandler.RunAsync({Nick}): CANCELLED ({kind}, target={loot?.name})");
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.RunAsync({Nick}, {kind}) THREW: {e}");
        }
        finally
        {
            // Session-end weapon-draw animation. Mid-drain we only refresh internal data; the animation-side
            // switch (SetSlotItem) is deferred here so it doesn't collide with the loot animation and stall
            // the main thread.
            if (!DormantMode)
            {
                if (_swappedWeaponIds.Count > 0)
                    SyncBotWeaponStateAtSessionEnd();
                // A dormant session must NOT unfreeze: the sleep recipe owns PatrollingData/Mover state.
                UnfreezeBotAfterLootSession();
            }
            LootTaskRunning = false;
            var elapsed = Time.realtimeSinceStartup - sw;
            Log.Debug($"OrbitLootHandler.RunAsync({Nick}): done in {elapsed:F1}s, kind={kind}, target={loot?.name}, ItemsTaken={Stats.LastItemsTaken} (was {takenBefore})");
        }
    }

    /// <summary>
    /// Orient the bot's head and steering toward the loot target so the crouched inspect / pickup animations
    /// read naturally instead of having the bot stare off in whatever direction the approach left it facing.
    /// </summary>
    private void LookAtLootTarget(InteractableObject loot)
    {
        if (_bot == null || loot == null) return;
        try
        {
            var target = loot.transform?.position;
            if (!target.HasValue) return;
            _bot.Steering?.LookToPoint(target.Value);
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.LookAtLootTarget({Nick}): THREW {e.Message}");
        }
    }

    private void FreezeBotForLootSession()
    {
        try
        {
            if (_bot == null) return;
            // PatrollingData ticks nav recalcs in LateUpdate; pausing it prevents them from racing with
            // weapon-slot mutations during the loot session.
            try { _bot.PatrollingData?.Pause(); } catch (System.Exception e) { Log.Warning($"OrbitLootHandler.FreezeBotForLootSession({Nick}): PatrollingData.Pause THREW {e.Message}"); }
            try { _bot.Mover?.Stop(); } catch (System.Exception e) { Log.Warning($"OrbitLootHandler.FreezeBotForLootSession({Nick}): Mover.Stop THREW {e.Message}"); }
            try { if (_bot.Mover != null) _bot.Mover.Pause = true; } catch (System.Exception e) { Log.Warning($"OrbitLootHandler.FreezeBotForLootSession({Nick}): Mover.Pause THREW {e.Message}"); }
            try { _bot.SetPose(0f); } catch (System.Exception e) { Log.Warning($"OrbitLootHandler.FreezeBotForLootSession({Nick}): SetPose THREW {e.Message}"); }
            Log.Debug($"OrbitLootHandler.FreezeBotForLootSession({Nick}): bot pinned crouched + movement paused + patrol data paused for loot session");
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.FreezeBotForLootSession({Nick}) THREW: {e}");
        }
    }

    private void UnfreezeBotAfterLootSession()
    {
        try
        {
            if (_bot == null) return;
            try { _bot.PatrollingData?.Unpause(); } catch (System.Exception e) { Log.Warning($"OrbitLootHandler.UnfreezeBotAfterLootSession({Nick}): PatrollingData.Unpause THREW {e.Message}"); }
            try { if (_bot.Mover != null) _bot.Mover.Pause = false; } catch (System.Exception e) { Log.Warning($"OrbitLootHandler.UnfreezeBotAfterLootSession({Nick}): Mover.Pause THREW {e.Message}"); }
            try { _bot.SetPose(1f); } catch (System.Exception e) { Log.Warning($"OrbitLootHandler.UnfreezeBotAfterLootSession({Nick}): SetPose THREW {e.Message}"); }
            Log.Debug($"OrbitLootHandler.UnfreezeBotAfterLootSession({Nick}): bot movement resumed + standing + patrol data resumed");
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.UnfreezeBotAfterLootSession({Nick}) THREW: {e}");
        }
    }

    public void StopLooting()
    {
        if (LootTaskRunning) { Log.Debug($"OrbitLootHandler.StopLooting({Nick})"); LastSessionCancelled = true; }
        _cts?.Cancel();
        LootTaskRunning = false;
    }

    public void Cancel()
    {
        if (LootTaskRunning) { Log.Debug($"OrbitLootHandler.Cancel({Nick})"); LastSessionCancelled = true; }
        _cts?.Cancel();
        LootTaskRunning = false;
    }

    private float GetMinPickupPrice()
    {
        var agent = Singleton<BotRoster>.Instance?.GetAgent(_bot);
        if (agent == null) return DefaultMinPickupPrice;
        var threshold = Orbit.Tasks.Actions.LootContainerAction.GetOrResolveAgentMiniLootThreshold(agent);
        return threshold > 0f ? threshold : DefaultMinPickupPrice;
    }

    private async Task LootContainerAsync(LootableContainer container, CancellationToken ct)
    {
        var initialState = container.DoorState;
        Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): door={initialState}, ItemOwner={(container.ItemOwner != null ? "yes" : "null")}");

        if (container.DoorState != EDoorState.Open)
        {
            if (!DormantMode)
            {
                Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): Interact(Open), waiting {ContainerOpenAnimMs}ms anim");
                _bot.LootOpener.Interact(container, EInteractionType.Open);
            }
            // Ghosts pay the same opening time, just without the animation (disabled body). Without
            // this a ghost emptied a whole container in one continuation chain (raid 8 lesson).
            await Task.Delay(ContainerOpenAnimMs, ct);
        }

        var rootItem = container.ItemOwner?.RootItem;
        _currentSourceRoot = rootItem;
        if (rootItem == null)
        {
            Log.Warning($"OrbitLootHandler.Container({Nick}, {container.name}): ItemOwner.RootItem is null — can't enumerate items");
        }
        else
        {
            await LootContainerDrainAsync(container, rootItem, ct);
        }

        try
        {
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): calling LootOpener.Interact(Close), door={container.DoorState}");
            _bot.LootOpener.Interact(container, EInteractionType.Close);
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.Container({Nick}, {container.name}): close failed: {e.Message}");
        }
    }

    /// <summary>
    /// Four-phase drain for a container, mirroring the corpse pipeline so a weapon equip can never be
    /// followed by another pickup tx in the same session (the post-swap deferred-work freeze window).
    /// </summary>
    private async Task LootContainerDrainAsync(LootableContainer container, Item rootItem, CancellationToken ct)
    {
        // Hoist every weapon, body armor and helmet found in the container out of the drain queue and into
        // their deferred phase 2/4 pipelines. Keeps every gear-affecting tx as the last op of the session.
        var drain = new List<DrainEntry>();
        var containerWeapons = new List<(Weapon weapon, string path)>();
        var containerArmors = new List<(Item item, string path)>();
        var containerHelmets = new List<(Item item, string path)>();
        var containerRigs = new List<(Item item, string path)>();
        var containerBackpacks = new List<(Item item, string path)>();
        var containerHeadsets = new List<(Item item, string path)>();
        foreach (var child in CollectImmediateChildren(rootItem))
        {
            var slotItems = new List<DrainEntry>();
            EnumerateItemsForDrain(child, "", slotItems);
            foreach (var entry in slotItems)
            {
                if (entry.Item is Weapon w)
                {
                    containerWeapons.Add((w, entry.Path));
                    continue;
                }
                if (entry.Item is Armor armorItem)
                {
                    containerArmors.Add((armorItem, entry.Path));
                    continue;
                }
                if (entry.Item is Headwear helmetItem)
                {
                    containerHelmets.Add((helmetItem, entry.Path));
                    continue;
                }
                if (entry.Item is Vest rigItem)
                {
                    containerRigs.Add((rigItem, entry.Path));
                    continue;
                }
                if (entry.Item is Backpack backpackItem)
                {
                    containerBackpacks.Add((backpackItem, entry.Path));
                    continue;
                }
                if (entry.Item is Headphones headsetItem)
                {
                    containerHeadsets.Add((headsetItem, entry.Path));
                    continue;
                }
                drain.Add(entry);
            }
        }
        if (containerWeapons.Count > 0)
        {
            var nestedWeaponIds = new HashSet<string>();
            foreach (var (w, _) in containerWeapons) nestedWeaponIds.Add(w.Id);
            drain.RemoveAll(e => IsDescendantOfWeaponSet(e.Item, nestedWeaponIds));
        }
        Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase1 drain queue size={drain.Count} non-gear entries, {containerWeapons.Count} weapon(s) + {containerArmors.Count} armor(s) + {containerHelmets.Count} helmet(s) + {containerRigs.Count} rig(s) + {containerBackpacks.Count} backpack(s) + {containerHeadsets.Count} headset(s) deferred to phase 2");

        // Resolve the post-swap weapon set so mag / ammo pickups during phase 1 can be gated against the
        // loadout the bot will end the session with (not the one it currently holds).
        ResolvePostSwapWeapons(null, containerWeapons);

        // Phase 1: drain non-weapon items.
        await DrainProgressiveAsync(drain, ct);

        if (containerWeapons.Count == 0 && containerArmors.Count == 0 && containerHelmets.Count == 0 && containerRigs.Count == 0 && containerBackpacks.Count == 0 && containerHeadsets.Count == 0) return;

        // Phase 2: pre-classify container weapons. Containers use equip-only semantics (no displacement) —
        // a candidate is only viable if it fits in one of the bot's currently empty weapon slots. The
        // highest-scoring viable candidate becomes the single phase 4 equip; all others get their mods
        // stripped in phase 3.
        (Weapon weapon, string path, float score)? bestWeapon = null;
        var toStrip = new List<(Weapon weapon, string path)>();
        foreach (var (w, path) in containerWeapons)
        {
            if (!CanEquipWeaponIntoEmptySlot(w))
            {
                toStrip.Add((w, path));
                continue;
            }
            var mapId = Singleton<OrbitManager>.Instance?.MapId;
            var weights = MapWeaponWeights.Resolve(mapId);
            var sourceItems = new List<Item>(WeaponScorer.Walk(_currentSourceRoot));
            var score = WeaponScorer.Score(w, sourceItems, weights);
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase2 pre-eval {path} → {w.LocalizedName()} fits-empty=True score={score:F1}");
            if (bestWeapon == null || score > bestWeapon.Value.score)
            {
                if (bestWeapon != null) toStrip.Add((bestWeapon.Value.weapon, bestWeapon.Value.path));
                bestWeapon = (w, path, score);
            }
            else
            {
                toStrip.Add((w, path));
            }
        }
        if (bestWeapon != null)
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase2 winner = {bestWeapon.Value.path} → {bestWeapon.Value.weapon.LocalizedName()} (score {bestWeapon.Value.score:F1}); stripping {toStrip.Count} other weapon(s)");

        // Phase 3: strip the mods of every weapon we won't equip. Pickup tx are safe here because no swap
        // has fired yet.
        foreach (var (w, path) in toStrip)
        {
            ct.ThrowIfCancellationRequested();
            var modItems = new List<DrainEntry>();
            EnumerateItemsForDrain(w, path, modItems);
            foreach (var modEntry in modItems)
            {
                if (ReferenceEquals(modEntry.Item, w)) continue;
                ct.ThrowIfCancellationRequested();
                await TransferItemAsync(modEntry, ct);
            }
        }

        // Phase 2b: pre-classify armor and helmet candidates. Container path is equip-only — a candidate
        // qualifies only if the bot's target slot is empty AND the candidate compatible. The highest-scoring
        // qualifying item per slot is kept; the others get nothing done to them (no mods to strip on armor).
        (Item item, string path, float score)? bestArmor = ResolveBestGearForEmptySlot(containerArmors, EquipmentSlot.ArmorVest);
        if (bestArmor != null)
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase2 armor winner = {bestArmor.Value.path} → {bestArmor.Value.item.LocalizedName()} (score {bestArmor.Value.score:F1})");
        (Item item, string path, float score)? bestHelmet = ResolveBestGearForEmptySlot(containerHelmets, EquipmentSlot.Headwear);
        if (bestHelmet != null)
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase2 helmet winner = {bestHelmet.Value.path} → {bestHelmet.Value.item.LocalizedName()} (score {bestHelmet.Value.score:F1})");
        // Rig candidates use a dedicated resolver because RigScorer is keyed off TacticalVest cells + armor
        // class instead of ArmorComponent alone.
        (Item item, string path, float score)? bestRig = null;
        if (containerRigs.Count > 0)
        {
            var equipment = _bot?.GetPlayer?.Inventory?.Equipment;
            var rigSlot = equipment?.GetSlot(EquipmentSlot.TacticalVest);
            if (rigSlot != null && rigSlot.ContainedItem == null)
            {
                foreach (var (item, path) in containerRigs)
                {
                    if (!rigSlot.CheckCompatibility(item)) continue;
                    var score = RigScorer.Score(item);
                    if (score <= 0f) continue;
                    if (bestRig == null || score > bestRig.Value.score) bestRig = (item, path, score);
                }
                if (bestRig != null)
                    Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase2 rig winner = {bestRig.Value.path} → {bestRig.Value.item.LocalizedName()} (score {bestRig.Value.score:F1})");
            }
        }
        (Item item, string path, float score)? bestBackpack = null;
        if (containerBackpacks.Count > 0)
        {
            var equipment = _bot?.GetPlayer?.Inventory?.Equipment;
            var backpackSlot = equipment?.GetSlot(EquipmentSlot.Backpack);
            if (backpackSlot != null && backpackSlot.ContainedItem == null)
            {
                foreach (var (item, path) in containerBackpacks)
                {
                    if (!backpackSlot.CheckCompatibility(item)) continue;
                    var score = BackpackScorer.Score(item);
                    if (score <= 0f) continue;
                    if (bestBackpack == null || score > bestBackpack.Value.score) bestBackpack = (item, path, score);
                }
                if (bestBackpack != null)
                    Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase2 backpack winner = {bestBackpack.Value.path} → {bestBackpack.Value.item.LocalizedName()} (score {bestBackpack.Value.score:F1})");
            }
        }
        (Item item, string path, float score)? bestHeadset = null;
        if (containerHeadsets.Count > 0)
        {
            var equipment = _bot?.GetPlayer?.Inventory?.Equipment;
            var earpieceSlot = equipment?.GetSlot(EquipmentSlot.Earpiece);
            if (earpieceSlot != null && earpieceSlot.ContainedItem == null)
            {
                foreach (var (item, path) in containerHeadsets)
                {
                    if (!earpieceSlot.CheckCompatibility(item)) continue;
                    var score = HeadsetScorer.Score(item);
                    if (score <= 0f) continue;
                    if (bestHeadset == null || score > bestHeadset.Value.score) bestHeadset = (item, path, score);
                }
                if (bestHeadset != null)
                    Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase2 headset winner = {bestHeadset.Value.path} → {bestHeadset.Value.item.LocalizedName()} (score {bestHeadset.Value.score:F1})");
            }
        }

        // Dormant fast path: pickups only — equips/swaps mutate BSG weapon/hands state, which must not
        // run on a disabled body. The value is already banked by the transfers above.
        if (DormantMode) return;

        // Phase 4: perform up to four equips in sequence (weapon → armor → helmet → rig), each preceded by a
        // settle so the bot's BSG-side state is quiescent before the next op. These are the last BSG ops of
        // the session — no further pickup tx can collide with deferred work.
        if (bestWeapon != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 pre-equip settle (2500ms) — weapon");
            await Task.Delay(2500, ct);
            if (!CanEquipWeaponIntoEmptySlot(bestWeapon.Value.weapon))
            {
                Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 SKIP {bestWeapon.Value.path} → {bestWeapon.Value.weapon.LocalizedName()} — no longer fits any empty slot");
            }
            else
            {
                Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 perform equip {bestWeapon.Value.path} → {bestWeapon.Value.weapon.LocalizedName()}");
                await TransferItemAsync(new DrainEntry(bestWeapon.Value.weapon, bestWeapon.Value.path), ct);
            }
        }
        if (bestArmor != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 pre-equip settle (2500ms) — armor");
            await Task.Delay(2500, ct);
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 perform armor equip → {bestArmor.Value.item.LocalizedName()}");
            await ArmorSwapper.TryEquipOnlyAsync(_bot, bestArmor.Value.item, EquipmentSlot.ArmorVest, ct);
            _bot.AIData?.CalcPower();
        }
        if (bestHelmet != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 pre-equip settle (2500ms) — helmet");
            await Task.Delay(2500, ct);
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 perform helmet equip → {bestHelmet.Value.item.LocalizedName()}");
            await ArmorSwapper.TryEquipOnlyAsync(_bot, bestHelmet.Value.item, EquipmentSlot.Headwear, ct);
            _bot.AIData?.CalcPower();
        }
        if (bestRig != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 pre-equip settle (2500ms) — rig");
            await Task.Delay(2500, ct);
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 perform rig equip → {bestRig.Value.item.LocalizedName()}");
            await RigSwapper.TryEquipOnlyAsync(_bot, bestRig.Value.item, ct);
            _bot.AIData?.CalcPower();
        }
        if (bestBackpack != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 pre-equip settle (2500ms) — backpack");
            await Task.Delay(2500, ct);
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 perform backpack equip → {bestBackpack.Value.item.LocalizedName()}");
            await BackpackSwapper.TryEquipOnlyAsync(_bot, bestBackpack.Value.item, ct);
        }
        if (bestHeadset != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 pre-equip settle (2500ms) — headset");
            await Task.Delay(2500, ct);
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): phase4 perform headset equip → {bestHeadset.Value.item.LocalizedName()}");
            await HeadsetSwapper.TryEquipOnlyAsync(_bot, bestHeadset.Value.item, ct);
        }
    }

    private (Item item, string path, float score)? ResolveBestGearForEmptySlot(List<(Item item, string path)> candidates, EquipmentSlot slotKind)
    {
        if (candidates.Count == 0) return null;
        var equipment = _bot?.GetPlayer?.Inventory?.Equipment;
        var slot = equipment?.GetSlot(slotKind);
        if (slot == null || slot.ContainedItem != null) return null; // equip-only — slot must already be empty
        (Item item, string path, float score)? best = null;
        foreach (var (item, path) in candidates)
        {
            if (!slot.CheckCompatibility(item)) continue;
            var score = ArmorScorer.Score(item);
            if (score <= 0f) continue;
            if (best == null || score > best.Value.score) best = (item, path, score);
        }
        return best;
    }

    private bool CanEquipWeaponIntoEmptySlot(Weapon weapon)
    {
        if (DormantMode) return false;
        // "Bots keep their spawn weapons": an empty slot stays empty — found weapons are bag loot only.
        if (ServerConfig.Loot.KeepSpawnWeapons) return false;
        var equipment = _bot?.GetPlayer?.Inventory?.Equipment;
        if (equipment == null) return false;
        foreach (var slotKind in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
        {
            var slot = equipment.GetSlot(slotKind);
            if (slot == null || slot.ContainedItem != null) continue;
            if (slot.CheckCompatibility(weapon)) return true;
        }
        return false;
    }

    private async Task LootCorpseAsync(Corpse corpse, CancellationToken ct)
    {
        var equip = corpse.Item;
        if (equip == null)
        {
            Log.Warning($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): Item is null — nothing to drain");
            return;
        }

        var inventoryEquipment = equip as InventoryEquipment;
        if (inventoryEquipment == null)
        {
            Log.Warning($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): Item is {equip.GetType().Name}, not InventoryEquipment — drain skipped");
            return;
        }

        var isPmc = corpse.Side == EPlayerSide.Bear || corpse.Side == EPlayerSide.Usec;
        var lootableSlots = isPmc ? CorpseLootableSlotsPmc : CorpseLootableSlotsNonPmc;
        _currentSourceRoot = inventoryEquipment;

        // Two interleaved timelines merged into one chronological queue: visible-track grabs are spaced by
        // InstantGrabDelayMs, search-track slots are sequential with progressive per-item reveal. Slot order
        // is randomised per track for natural variation.
        var visibleOrder = lootableSlots.Where(s => !SearchableCorpseSlots.Contains(s)).OrderBy(_ => Random.value).ToList();
        var searchOrder = lootableSlots.Where(s => SearchableCorpseSlots.Contains(s)).OrderBy(_ => Random.value).ToList();
        Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): side={corpse.Side} pmc={isPmc} visibleOrder=[{string.Join(",", visibleOrder)}] searchOrder=[{string.Join(",", searchOrder)}]");

        // Loot session phases: 1 — drain non-weapon equipment + searchable grids; 2 — pre-classify every
        // corpse weapon (synchronous WouldSwap); 3 — strip mods of non-best weapons; 4 — fire the single best
        // swap, last BSG op of the session.
        bool IsWeaponSlot(EquipmentSlot s)
            => s == EquipmentSlot.FirstPrimaryWeapon || s == EquipmentSlot.SecondPrimaryWeapon || s == EquipmentSlot.Holster;
        bool IsGearSlot(EquipmentSlot s)
            => s == EquipmentSlot.ArmorVest || s == EquipmentSlot.Headwear || s == EquipmentSlot.Earpiece;

        // Hoist every weapon found on the corpse — slot-equipped or nested in a container — out of the drain
        // queue and into the phase 2/4 swap pipeline. Keeps swap dispatch separated from mid-drain pickup tx.
        var corpseWeapons = new List<(string sourcePath, Weapon weapon)>();
        foreach (var slotKind in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
        {
            var slot = inventoryEquipment.GetSlot(slotKind);
            if (slot?.ContainedItem is Weapon weaponInSlot)
                corpseWeapons.Add((slotKind.ToString(), weaponInSlot));
        }
        // Same hoisting for body armor, helmet and rig so they go through the gear swap paths instead of
        // being looted as loose items by the regular drain.
        Item corpseArmorItem = null;
        Item corpseHelmetItem = null;
        Item corpseRigItem = null;
        Item corpseBackpackItem = null;
        Item corpseHeadsetItem = null;
        var armorSlot = inventoryEquipment.GetSlot(EquipmentSlot.ArmorVest);
        if (armorSlot?.ContainedItem != null) corpseArmorItem = armorSlot.ContainedItem;
        var headwearSlot = inventoryEquipment.GetSlot(EquipmentSlot.Headwear);
        if (headwearSlot?.ContainedItem != null) corpseHelmetItem = headwearSlot.ContainedItem;
        var corpseRigSlot = inventoryEquipment.GetSlot(EquipmentSlot.TacticalVest);
        if (corpseRigSlot?.ContainedItem != null) corpseRigItem = corpseRigSlot.ContainedItem;
        var corpseBackpackSlot = inventoryEquipment.GetSlot(EquipmentSlot.Backpack);
        if (corpseBackpackSlot?.ContainedItem != null) corpseBackpackItem = corpseBackpackSlot.ContainedItem;
        var earpieceSlot = inventoryEquipment.GetSlot(EquipmentSlot.Earpiece);
        if (earpieceSlot?.ContainedItem != null) corpseHeadsetItem = earpieceSlot.ContainedItem;

        var queue = new List<(int revealMs, EquipmentSlot slot, DrainEntry entry)>();
        var visibleCursorMs = 0;
        foreach (var slotKind in visibleOrder)
        {
            if (IsWeaponSlot(slotKind) || IsGearSlot(slotKind)) continue;
            var slot = inventoryEquipment.GetSlot(slotKind);
            var root = slot?.ContainedItem;
            if (root == null) continue;
            var slotItems = new List<DrainEntry>();
            EnumerateItemsForDrain(root, slotKind.ToString(), slotItems);
            foreach (var entry in slotItems)
            {
                if (entry.Item is Weapon nestedWeapon)
                {
                    corpseWeapons.Add((entry.Path, nestedWeapon));
                    continue;
                }
                // The rig (TacticalVest top-level item) is hoisted out for the phase 2/4 rig swap; its
                // contents (mags / grenades / etc.) stay in the drain queue and get looted normally.
                if (slotKind == EquipmentSlot.TacticalVest && ReferenceEquals(entry.Item, corpseRigItem)) continue;
                // Same for the backpack top-level item — contents drained, the bag itself goes through
                // the phase 2/4 backpack swap.
                if (slotKind == EquipmentSlot.Backpack && ReferenceEquals(entry.Item, corpseBackpackItem)) continue;
                queue.Add((visibleCursorMs, slotKind, entry));
                visibleCursorMs += InstantGrabDelayMs;
            }
        }

        var searchCursorMs = 0;
        foreach (var slotKind in searchOrder)
        {
            if (IsWeaponSlot(slotKind) || IsGearSlot(slotKind)) continue;
            var slot = inventoryEquipment.GetSlot(slotKind);
            var root = slot?.ContainedItem;
            if (root == null) continue;
            var slotItems = new List<DrainEntry>();
            EnumerateItemsForDrain(root, slotKind.ToString(), slotItems);
            var lastRevealOffset = 0;
            for (var i = 0; i < slotItems.Count; i++)
            {
                if (slotItems[i].Item is Weapon nestedWeapon)
                {
                    corpseWeapons.Add((slotItems[i].Path, nestedWeapon));
                    continue;
                }
                if (slotKind == EquipmentSlot.TacticalVest && ReferenceEquals(slotItems[i].Item, corpseRigItem)) continue;
                if (slotKind == EquipmentSlot.Backpack && ReferenceEquals(slotItems[i].Item, corpseBackpackItem)) continue;
                var offset = System.Math.Min(InitialSearchMs + i * PerItemRevealMs, MaxRevealCapMs);
                queue.Add((searchCursorMs + offset, slotKind, slotItems[i]));
                lastRevealOffset = offset;
            }
            searchCursorMs += lastRevealOffset;
        }

        // Drop any drain entries that descend from a nested weapon — those mods belong to a weapon being
        // evaluated for swap in phase 2 and would otherwise be stripped before the decision.
        var nestedWeaponIds = new HashSet<string>();
        foreach (var (_, w) in corpseWeapons) nestedWeaponIds.Add(w.Id);
        queue.RemoveAll(e => IsDescendantOfWeaponSet(e.entry.Item, nestedWeaponIds));

        queue.Sort((a, b) => a.revealMs.CompareTo(b.revealMs));
        var totalEstimatedMs = queue.Count > 0 ? queue[queue.Count - 1].revealMs : 0;
        Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase1 drain queue size={queue.Count} non-weapon entries, {corpseWeapons.Count} weapon(s) deferred to phase 2, last reveal at {totalEstimatedMs}ms");
        for (var i = 0; i < queue.Count; i++)
        {
            var e = queue[i];
            Log.Debug($"  [{i}] T={e.revealMs}ms slot={e.slot} path={e.entry.Path} item={e.entry.Item.LocalizedName()} ({e.entry.Item.Width}x{e.entry.Item.Height}, price={ItemPriceLookup.GetPrice(e.entry.Item):N0}₽, perSlot={ItemPriceLookup.GetPricePerSlot(e.entry.Item):N0}₽)");
        }

        // Resolve the post-swap weapon set so mag / ammo pickups during phase 1 can be gated against the
        // loadout the bot will end the session with (not the one it currently holds).
        var corpseWeaponsForResolver = corpseWeapons.ConvertAll(t => (t.sourcePath, t.weapon));
        ResolvePostSwapWeapons(corpseWeaponsForResolver, null);

        // Phase 1: drain everything that isn't a corpse weapon slot (rig / pockets / armor / bag, plus their
        // contents).
        var startTime = Time.realtimeSinceStartup;
        for (var i = 0; i < queue.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = queue[i];
            var elapsedMs = (int)((Time.realtimeSinceStartup - startTime) * 1000);
            var waitMs = entry.revealMs - elapsedMs; // ghosts pay real reveal time too
            if (waitMs > 0)
            {
                Log.Debug($"OrbitLootHandler.Corpse({Nick}): waiting {waitMs}ms for queue[{i}] (T={entry.revealMs}ms, elapsed={elapsedMs}ms) — {entry.entry.Path}/{entry.entry.Item.LocalizedName()}");
                await Task.Delay(waitMs, ct);
            }
            else
            {
                Log.Debug($"OrbitLootHandler.Corpse({Nick}): queue[{i}] already revealed (T={entry.revealMs}ms, elapsed={elapsedMs}ms) — immediate grab on {entry.entry.Path}/{entry.entry.Item.LocalizedName()}");
            }
            await TransferItemAsync(entry.entry, ct);
        }

        // Phase 2: synchronous WouldSwap pre-check on every corpse weapon. No BSG tx is fired. Keeps the
        // highest-scoring accepted weapon as the single swap candidate; all others (rejected or not-best) are
        // queued for mod-stripping.
        (string sourcePath, Weapon weapon, float score)? best = null;
        var toStrip = new List<(string sourcePath, Weapon weapon)>();
        foreach (var (sourcePath, weaponRoot) in corpseWeapons)
        {
            var verdict = WeaponSwapper.WouldSwap(_bot, weaponRoot, _currentSourceRoot);
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase2 pre-eval {sourcePath} → {weaponRoot.LocalizedName()} would-swap={verdict.WouldSwap} score={verdict.CandidateScore:F1}");
            if (!verdict.WouldSwap)
            {
                toStrip.Add((sourcePath, weaponRoot));
                continue;
            }
            if (best == null || verdict.CandidateScore > best.Value.score)
            {
                if (best != null) toStrip.Add((best.Value.sourcePath, best.Value.weapon));
                best = (sourcePath, weaponRoot, verdict.CandidateScore);
            }
            else
            {
                toStrip.Add((sourcePath, weaponRoot));
            }
        }
        if (best != null)
            Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase2 winner = {best.Value.sourcePath} → {best.Value.weapon.LocalizedName()} (score {best.Value.score:F1}); stripping {toStrip.Count} other weapon(s)");

        // Same pre-classification for body armor and helmet. Each slot is independent — the bot can swap a
        // better armor and a better helmet in the same session.
        (EquipmentSlot slotKind, Item candidate, float score)? bestArmor = null;
        if (corpseArmorItem != null)
        {
            var verdict = ArmorSwapper.WouldSwap(_bot, corpseArmorItem, EquipmentSlot.ArmorVest);
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase2 pre-eval ArmorVest → {corpseArmorItem.LocalizedName()} would-swap={verdict.WouldSwap} score={verdict.CandidateScore:F1}");
            if (verdict.WouldSwap) bestArmor = (EquipmentSlot.ArmorVest, corpseArmorItem, verdict.CandidateScore);
        }
        (EquipmentSlot slotKind, Item candidate, float score)? bestHelmet = null;
        if (corpseHelmetItem != null)
        {
            var verdict = ArmorSwapper.WouldSwap(_bot, corpseHelmetItem, EquipmentSlot.Headwear);
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase2 pre-eval Headwear → {corpseHelmetItem.LocalizedName()} would-swap={verdict.WouldSwap} score={verdict.CandidateScore:F1}");
            if (verdict.WouldSwap) bestHelmet = (EquipmentSlot.Headwear, corpseHelmetItem, verdict.CandidateScore);
        }
        (Item candidate, float score)? bestRig = null;
        if (corpseRigItem != null)
        {
            var verdict = RigSwapper.WouldSwap(_bot, corpseRigItem);
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase2 pre-eval TacticalVest → {corpseRigItem.LocalizedName()} would-swap={verdict.WouldSwap} score={verdict.CandidateScore:F1}");
            if (verdict.WouldSwap) bestRig = (corpseRigItem, verdict.CandidateScore);
        }
        (Item candidate, float score)? bestBackpack = null;
        if (corpseBackpackItem != null)
        {
            var verdict = BackpackSwapper.WouldSwap(_bot, corpseBackpackItem);
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase2 pre-eval Backpack → {corpseBackpackItem.LocalizedName()} would-swap={verdict.WouldSwap} score={verdict.CandidateScore:F1}");
            if (verdict.WouldSwap) bestBackpack = (corpseBackpackItem, verdict.CandidateScore);
        }
        (Item candidate, float score)? bestHeadset = null;
        if (corpseHeadsetItem != null)
        {
            var verdict = HeadsetSwapper.WouldSwap(_bot, corpseHeadsetItem);
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase2 pre-eval Earpiece → {corpseHeadsetItem.LocalizedName()} would-swap={verdict.WouldSwap} score={verdict.CandidateScore:F1}");
            if (verdict.WouldSwap) bestHeadset = (corpseHeadsetItem, verdict.CandidateScore);
        }

        // Phase 3: strip the mods of every weapon we won't swap. Pickup tx are safe here because no swap has
        // fired yet.
        foreach (var (sourcePath, weaponToStrip) in toStrip)
        {
            ct.ThrowIfCancellationRequested();
            var modItems = new List<DrainEntry>();
            EnumerateItemsForDrain(weaponToStrip, sourcePath, modItems);
            foreach (var modEntry in modItems)
            {
                if (ReferenceEquals(modEntry.Item, weaponToStrip)) continue;
                ct.ThrowIfCancellationRequested();
                Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase3 strip mod {modEntry.Path}/{modEntry.Item.LocalizedName()} from {weaponToStrip.LocalizedName()}");
                await TransferItemAsync(modEntry, ct);
            }
        }

        // Dormant fast path: no swaps/equips on a disabled body — the drained value is already banked.
        if (DormantMode) return;

        // Phase 4: perform the single best swap — last BSG-affecting op of the session.
        if (best != null)
        {
            ct.ThrowIfCancellationRequested();
            // Pre-swap settle: let pickup tx fired in phases 1 and 3 quiesce. Mirrors the post-swap settle so
            // the bot is stable on both sides of the swap window.
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 pre-swap settle (2500ms)");
            await Task.Delay(2500, ct);
            // Re-evaluate against the bot's updated loadout (phase 3 may have widened its ammo pool, shifting
            // the baseline). The recheck also surfaces the weapon that will be displaced so its mods can be
            // salvaged before the swap fires.
            var recheck = WeaponSwapper.WouldSwap(_bot, best.Value.weapon, _currentSourceRoot);
            if (!recheck.WouldSwap)
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 SKIP {best.Value.sourcePath} → {best.Value.weapon.LocalizedName()} — no longer beats updated loadout");
                return;
            }
            if (recheck.DisplacedWeapon != null)
            {
                Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 pre-strip mods of {recheck.DisplacedWeapon.LocalizedName()} (will be thrown to corpse by swap)");
                var displacedModItems = new List<DrainEntry>();
                EnumerateItemsForDrain(recheck.DisplacedWeapon, "BotDisplaced", displacedModItems);
                foreach (var modEntry in displacedModItems)
                {
                    if (ReferenceEquals(modEntry.Item, recheck.DisplacedWeapon)) continue;
                    ct.ThrowIfCancellationRequested();
                    Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 strip mod {modEntry.Path}/{modEntry.Item.LocalizedName()} from to-be-displaced {recheck.DisplacedWeapon.LocalizedName()}");
                    await TransferItemAsync(modEntry, ct);
                }
            }
            Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 perform weapon swap — candidate {best.Value.weapon.LocalizedName()} (from corpse {best.Value.sourcePath}); destination decided by WeaponSwapper");
            _allowWeaponSwapPath = true;
            try { await TransferItemAsync(new DrainEntry(best.Value.weapon, best.Value.sourcePath), ct); }
            finally { _allowWeaponSwapPath = false; }
        }

        // Phase 4b: body armor swap. Independent of the weapon swap; if both fired, the bot's inventory
        // controller already had its post-weapon-swap settle window so we just pre-settle again and proceed.
        if (bestArmor != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 pre-armor settle (2500ms)");
            await Task.Delay(2500, ct);
            var recheck = ArmorSwapper.WouldSwap(_bot, bestArmor.Value.candidate, bestArmor.Value.slotKind);
            if (!recheck.WouldSwap)
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 SKIP armor → {bestArmor.Value.candidate.LocalizedName()} — no longer beats updated loadout");
            }
            else
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 perform armor swap → {bestArmor.Value.candidate.LocalizedName()}");
                await ArmorSwapper.TryHandleAsync(_bot, bestArmor.Value.candidate, bestArmor.Value.slotKind, ct);
                _bot.AIData?.CalcPower();
            }
        }

        // Phase 4c: helmet swap. Same pattern as armor.
        if (bestHelmet != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 pre-helmet settle (2500ms)");
            await Task.Delay(2500, ct);
            var recheck = ArmorSwapper.WouldSwap(_bot, bestHelmet.Value.candidate, bestHelmet.Value.slotKind);
            if (!recheck.WouldSwap)
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 SKIP helmet → {bestHelmet.Value.candidate.LocalizedName()} — no longer beats updated loadout");
            }
            else
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 perform helmet swap → {bestHelmet.Value.candidate.LocalizedName()}");
                await ArmorSwapper.TryHandleAsync(_bot, bestHelmet.Value.candidate, bestHelmet.Value.slotKind, ct);
                _bot.AIData?.CalcPower();
            }
        }

        // Phase 4d: rig swap. Same-type only (simple ↔ simple, armored ↔ armored). Item transfer from the
        // current rig to the candidate happens inside RigSwapper.TryHandleAsync before the atomic Swap so the
        // bot keeps their carry.
        if (bestRig != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 pre-rig settle (2500ms)");
            await Task.Delay(2500, ct);
            var recheck = RigSwapper.WouldSwap(_bot, bestRig.Value.candidate);
            if (!recheck.WouldSwap)
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 SKIP rig → {bestRig.Value.candidate.LocalizedName()} — no longer beats updated loadout");
            }
            else
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 perform rig swap → {bestRig.Value.candidate.LocalizedName()}");
                await RigSwapper.TryHandleAsync(_bot, bestRig.Value.candidate, ct);
                _bot.AIData?.CalcPower();
            }
        }

        // Phase 4e: backpack swap. Pure capacity play, no type split. The bot's carry is transferred into
        // the candidate bag inside BackpackSwapper.TryHandleAsync before the atomic Swap; the corpse's
        // value-gated leftovers ride along into our inventory with the bag.
        if (bestBackpack != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 pre-backpack settle (2500ms)");
            await Task.Delay(2500, ct);
            var recheck = BackpackSwapper.WouldSwap(_bot, bestBackpack.Value.candidate);
            if (!recheck.WouldSwap)
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 SKIP backpack → {bestBackpack.Value.candidate.LocalizedName()} — no longer beats updated loadout");
            }
            else
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 perform backpack swap → {bestBackpack.Value.candidate.LocalizedName()}");
                await BackpackSwapper.TryHandleAsync(_bot, bestBackpack.Value.candidate, ct);
            }
        }

        // Phase 4f: headset swap. Simple atomic swap, no grids / contents / no AI-power impact.
        if (bestHeadset != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 pre-headset settle (2500ms)");
            await Task.Delay(2500, ct);
            var recheck = HeadsetSwapper.WouldSwap(_bot, bestHeadset.Value.candidate);
            if (!recheck.WouldSwap)
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 SKIP headset → {bestHeadset.Value.candidate.LocalizedName()} — no longer beats updated loadout");
            }
            else
            {
                Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): phase4 perform headset swap → {bestHeadset.Value.candidate.LocalizedName()}");
                await HeadsetSwapper.TryHandleAsync(_bot, bestHeadset.Value.candidate, ct);
            }
        }
    }

    // Parent ids already reported for a self-referencing CurrentAddress (BSG's InventoryEquipment loops back
    // on itself as a root). De-dups the warning so it fires once per id per session.
    private static readonly HashSet<string> _cycleWarningSeen = new();

    private static bool IsDescendantOfWeaponSet(Item item, HashSet<string> weaponIds)
    {
        if (weaponIds.Count == 0 || item == null) return false;
        const int maxDepth = 32;
        HashSet<string> visited = null;
        var addr = item.CurrentAddress;
        var depth = 0;
        while (addr != null && depth < maxDepth)
        {
            var parent = addr.Container?.ParentItem;
            if (parent == null) return false;
            if (weaponIds.Contains(parent.Id)) return true;
            if (visited == null) visited = new HashSet<string>();
            if (!visited.Add(parent.Id))
            {
                if (_cycleWarningSeen.Add(parent.Id))
                    Log.Debug($"OrbitLootHandler.IsDescendantOfWeaponSet: cycle at parent id={parent.Id} after {depth} hops (BSG-side root self-reference, expected)");
                return false;
            }
            addr = parent.CurrentAddress;
            depth++;
        }
        if (depth >= maxDepth)
            Log.Warning($"OrbitLootHandler.IsDescendantOfWeaponSet: max depth {maxDepth} reached without root — bailing");
        return false;
    }

    // Progressive reveal: each item becomes discoverable at InitialSearchMs + i × PerItemRevealMs (capped).
    // Grabs fire as soon as the reveal time elapses; a fast grab waits for the next reveal, a slow grab
    // transitions straight to an already-visible item.
    private async Task DrainProgressiveAsync(List<DrainEntry> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        for (var i = 0; i < items.Count; i++)
        {
            var revealMs = System.Math.Min(InitialSearchMs + i * PerItemRevealMs, MaxRevealCapMs);
            var entry = items[i];
            Log.Debug($"  [{i}] T={revealMs}ms path={entry.Path} item={entry.Item.LocalizedName()} ({entry.Item.Width}x{entry.Item.Height}, price={ItemPriceLookup.GetPrice(entry.Item):N0}₽, perSlot={ItemPriceLookup.GetPricePerSlot(entry.Item):N0}₽)");
        }
        var startTime = Time.realtimeSinceStartup;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            BumpLootActivity(); // each drain entry is forward progress, keeps the watchdog patient
            var revealMs = System.Math.Min(InitialSearchMs + i * PerItemRevealMs, MaxRevealCapMs);
            var elapsedMs = (int)((Time.realtimeSinceStartup - startTime) * 1000);
            var waitMs = revealMs - elapsedMs; // ghosts pay real reveal time too
            if (waitMs > 0)
            {
                Log.Debug($"OrbitLootHandler.Drain({Nick}): waiting {waitMs}ms for item[{i}] (T={revealMs}ms, elapsed={elapsedMs}ms) — {items[i].Path}/{items[i].Item.LocalizedName()}");
                await Task.Delay(waitMs, ct);
            }
            else
            {
                Log.Debug($"OrbitLootHandler.Drain({Nick}): item[{i}] already revealed (T={revealMs}ms, elapsed={elapsedMs}ms) — immediate grab on {items[i].Path}/{items[i].Item.LocalizedName()}");
            }
            await TransferItemAsync(items[i], ct);
        }
    }

    private async Task LootLooseItemAsync(LootItem lootItem, CancellationToken ct)
    {
        if (lootItem.Item == null)
        {
            Log.Warning($"OrbitLootHandler.Loose({Nick}, {lootItem.name}): lootItem.Item is null");
            return;
        }
        Log.Debug($"OrbitLootHandler.Loose({Nick}, {lootItem.name}): awaiting 0.5s, item={lootItem.Item.LocalizedName()}");
        _currentSourceRoot = lootItem.Item;
        await Task.Delay(500, ct); // ghosts included — same approach time as an awake pickup
        await PickupLooseAsync(lootItem.Item, ct);
    }

    // Silent inventory→inventory transfer (container/corpse path). No per-item Pickup state — the
    // open/inspect animation already played once.
    private Task<bool> TransferItemAsync(Item item, CancellationToken ct)
        => TransferItemAsync(new DrainEntry(item, ""), ct);

    private async Task<bool> TransferItemAsync(DrainEntry entry, CancellationToken ct)
    {
        // Bump the watchdog on every transfer attempt — each one is forward progress regardless of
        // outcome (TAKE / SKIP / SWAP all count).
        BumpLootActivity();
        // Skip entries that descend from a weapon already moved into the bot's slots by a swap — re-picking
        // them would detach mods from the freshly equipped weapon.
        if (entry.Item != null && IsDescendantOfSwappedWeapon(entry.Item))
        {
            Log.Debug($"OrbitLootHandler.Transfer({Nick}): SKIP {entry.Item.LocalizedName()} at {entry.Path} (descends from a swapped weapon)");
            return false;
        }

        // Weapons take the swap path. Only corpse loot (phase 4) can fire the displacement variant — that
        // path is sequenced to make the swap the last BSG op of the session. Container weapons go through
        // equip-only; if no empty slot fits, fall through to the default placement so the weapon lands in the
        // bot's bag instead of being left behind.
        if (entry.Item is Weapon candidateWeapon)
        {
            WeaponSwapper.Outcome outcome;
            if (_allowWeaponSwapPath)
            {
                outcome = await WeaponSwapper.TryHandleAsync(_bot, candidateWeapon, _currentSourceRoot, ct);
            }
            else if (DormantMode)
            {
                // Dormant: never equip — bag the weapon via the default placement below.
                outcome = WeaponSwapper.Outcome.NotApplicable;
            }
            else
            {
                outcome = await WeaponSwapper.TryEquipOnlyAsync(_bot, candidateWeapon, ct);
                // Skipped here just means "no empty slot fits" — fall through to default bag placement.
                if (outcome == WeaponSwapper.Outcome.Skipped) outcome = WeaponSwapper.Outcome.NotApplicable;
            }
            if (outcome == WeaponSwapper.Outcome.Swapped)
            {
                _swappedWeaponIds.Add(candidateWeapon.Id);
                var weaponName = candidateWeapon.LocalizedName();
                var weaponPrice = ItemPriceLookup.GetPrice(candidateWeapon);
                RecordPickup(weaponName, weaponPrice, entry.Path);
                // Refresh internal weapon-manager data, then wait for BSG's hands controller and deferred
                // slot observers to settle. IsChangingWeapon catches the animation path; the blind hold
                // covers the rest.
                SyncBotWeaponStateAfterSwap();
                await WaitForHandsControllerSettleAsync(ct);
                Log.Debug($"OrbitLootHandler.Transfer({Nick}): post-swap blind hold (2500ms)");
                await Task.Delay(2500, ct);
                return true;
            }
            if (outcome == WeaponSwapper.Outcome.Skipped)
                return false;
            // NotApplicable: fall through to default loot path.
        }

        if (!ValidatePickup(entry, out var name, out var price, out var pricePerSlot, out var inventoryController)) return false;
        var place = FindPlace(entry, inventoryController, name, price);
        if (!place.Succeeded) return false;

        Log.Debug($"OrbitLootHandler.Transfer({Nick}): TRY {name} at {entry.Path} (price={price:N0}₽, perSlot={pricePerSlot:N0}₽)");
        var success = await RunTransactionAsync(place, name, ct);
        if (success) RecordPickup(name, price, entry.Path);
        return success;
    }

    /// <summary>
    /// Refresh the bot's weapon-manager weapons list and recompute its combat power rating after a swap.
    /// Intentionally skips FastForwardCurrentState and SetSlotItem — both queue hands-controller animations
    /// and must not run while the bot is still in the loot animation. The animation-side switch is deferred
    /// to <see cref="SyncBotWeaponStateAtSessionEnd"/>.
    /// </summary>
    private void SyncBotWeaponStateAfterSwap()
    {
        try
        {
            if (_bot == null) return;
            try { _bot.WeaponManager?.Selector?.UpdateWeaponsList(); }
            catch (System.Exception ulEx) { Log.Warning($"OrbitLootHandler.SyncBotWeaponStateAfterSwap({Nick}): UpdateWeaponsList THREW {ulEx.Message}"); }
            try { _bot.AIData?.CalcPower(); }
            catch (System.Exception cpEx) { Log.Warning($"OrbitLootHandler.SyncBotWeaponStateAfterSwap({Nick}): CalcPower THREW {cpEx.Message}"); }
            Log.Debug($"OrbitLootHandler.SyncBotWeaponStateAfterSwap({Nick}): refreshed WeaponManager list + AIData (animation deferred to session end)");
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.SyncBotWeaponStateAfterSwap({Nick}) THREW: {e}");
        }
    }

    /// <summary>
    /// Poll the inventory controller's <c>IsChangingWeapon</c> flag until it clears, <paramref name="ct"/> is
    /// cancelled, or the cap is hit. A swap leaves BSG's hands controller mid weapon-draw; firing the next op
    /// during that window is liable to stall the main thread.
    /// </summary>
    private async Task WaitForHandsControllerSettleAsync(CancellationToken ct)
    {
        const int maxWaitMs = 10000;
        const int pollIntervalMs = 100;
        var ic = _bot?.GetPlayer?.InventoryController;
        if (ic == null) return;
        var elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            bool changing;
            try { changing = ic.IsChangingWeapon; }
            catch { changing = false; }
            if (!changing)
            {
                if (elapsed > 0)
                    Log.Debug($"OrbitLootHandler.WaitForHandsControllerSettleAsync({Nick}): hands controller idle after {elapsed}ms");
                return;
            }
            await Task.Delay(pollIntervalMs, ct);
            elapsed += pollIntervalMs;
        }
        Log.Warning($"OrbitLootHandler.WaitForHandsControllerSettleAsync({Nick}): timed out after {maxWaitMs}ms — proceeding anyway");
    }

    /// <summary>
    /// End-of-session sync. Bot is no longer in the loot animation cycle: fast-forward the hands controller
    /// and call SetSlotItem so BSG re-picks and draws the new best weapon.
    /// </summary>
    private void SyncBotWeaponStateAtSessionEnd()
    {
        try
        {
            var player = _bot?.GetPlayer;
            if (player == null) return;
            try { player.HandsController?.FastForwardCurrentState(); }
            catch (System.Exception ffEx) { Log.Warning($"OrbitLootHandler.SyncBotWeaponStateAtSessionEnd({Nick}): FastForward THREW {ffEx.Message}"); }
            try { _bot.WeaponManager?.Selector?.SetSlotItem(new Callback<IHandsController>(_ => { }), true); }
            catch (System.Exception ssEx) { Log.Warning($"OrbitLootHandler.SyncBotWeaponStateAtSessionEnd({Nick}): SetSlotItem THREW {ssEx.Message}"); }
            Log.Debug($"OrbitLootHandler.SyncBotWeaponStateAtSessionEnd({Nick}): triggered weapon-draw animation after {_swappedWeaponIds.Count} swapped weapon(s)");
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.SyncBotWeaponStateAtSessionEnd({Nick}) THREW: {e}");
        }
    }

    private bool IsDescendantOfSwappedWeapon(Item item)
    {
        if (_swappedWeaponIds.Count == 0) return false;
        var addr = item.CurrentAddress;
        while (addr != null)
        {
            var parent = addr.Container?.ParentItem;
            if (parent == null) return false;
            if (_swappedWeaponIds.Contains(parent.Id)) return true;
            addr = parent.CurrentAddress;
        }
        return false;
    }

    // Loose world item path: kneel animation per pickup via the player's managed Pickup state.
    private async Task<bool> PickupLooseAsync(Item item, CancellationToken ct)
    {
        var entry = new DrainEntry(item, "");
        if (!ValidatePickup(entry, out var name, out var price, out var pricePerSlot, out var inventoryController)) return false;
        var player = _bot.GetPlayer;
        var place = FindPlace(entry, inventoryController, name, price);
        if (!place.Succeeded) return false;

        // Dormant fast path: the kneel animation callback would never fire on a disabled body — run the
        // transaction directly (identical data op; the world item is consumed by the transaction).
        if (DormantMode)
        {
            await Task.Delay(500, ct); // kneel-time equivalent — the animation can't play on a disabled body
            var taken = await RunTransactionAsync(place, name, ct);
            if (taken) RecordPickup(name, price, entry.Path);
            return taken;
        }

        var pickupReady = new TaskCompletionSource<bool>();
        try
        {
            player.CurrentManagedState.Pickup(true, () => pickupReady.TrySetResult(true));
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.Loose({Nick}, {name}): Pickup(true) THREW {e}");
            return false;
        }

        using (ct.Register(() => pickupReady.TrySetResult(false)))
        {
            if (!await pickupReady.Task)
            {
                try { player.CurrentManagedState.Pickup(false, null); } catch { }
                return false;
            }
        }

        Log.Debug($"OrbitLootHandler.Loose({Nick}): TRY {name} (price={price:N0}₽, perSlot={pricePerSlot:N0}₽, kneel done)");
        var success = await RunTransactionAsync(place, name, ct);

        try { player.CurrentManagedState.Pickup(false, null); } catch { }
        try { player.UpdateInteractionCast(); } catch { }

        if (success) RecordPickup(name, price, entry.Path);
        return success;
    }

    private bool ValidatePickup(DrainEntry entry, out string name, out float price, out float pricePerSlot, out InventoryController inventoryController)
    {
        name = null; price = 0f; pricePerSlot = 0f;
        var item = entry.Item;
        inventoryController = _bot.GetPlayer?.InventoryController;
        if (inventoryController == null || item == null)
        {
            Log.Warning($"OrbitLootHandler.Pickup({Nick}): inventoryController={inventoryController != null}, item={item != null} — abort");
            return false;
        }
        name = item.LocalizedName();
        price = ItemPriceLookup.GetPrice(item);
        pricePerSlot = ItemPriceLookup.GetPricePerSlot(item);
        // Currency / frag grenades / dogtags bypass the value gate and the scav random roll.
        if (IsValueGateBypass(item, out var bypassReason))
        {
            Stats.LastHadBypassItem = true;
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): {name} at {entry.Path} bypasses value gate ({bypassReason}, perSlot={pricePerSlot:N0}₽)");
            return true;
        }
        // Phase 4 mag-compat bypass — mags / ammo that fit any weapon the bot will own post-swap are useful
        // even if their cash value is below threshold. See SWAP_PLAN.md for the post-swap caliber rationale.
        if (FitsPostSwapWeapon(item, out var fitReason))
        {
            Stats.LastHadBypassItem = true;
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): {name} at {entry.Path} bypasses value gate ({fitReason}, perSlot={pricePerSlot:N0}₽)");
            return true;
        }
        // Bot scavs use a per-item random roll instead of a value threshold — mirrors vanilla opportunistic
        // looting. PlayerScavs and PMCs continue to the per-archetype gate below.
        if (IsBotScav(_bot))
        {
            var chance = (ServerConfig.Loot.ScavLootChancePct) / 100f;
            var roll = Random.value;
            if (roll < chance)
            {
                Log.Debug($"OrbitLootHandler.Pickup({Nick}): scav KEEP {name} at {entry.Path} (roll {roll:F2} < {chance:F2}, perSlot={pricePerSlot:N0}₽)");
                return true;
            }
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): scav SKIP {name} at {entry.Path} (roll {roll:F2} ≥ {chance:F2}, perSlot={pricePerSlot:N0}₽)");
            return false;
        }
        // PMC / PlayerScav: per-archetype threshold gate. Tracks the highest non-bypass perSlot so the
        // post-loot blacklist can identify which squadmates would also reject this POI.
        var minPrice = GetMinPickupPrice();
        if (pricePerSlot > Stats.LastMaxPerSlotSeen)
            Stats.LastMaxPerSlotSeen = pricePerSlot;
        if (pricePerSlot < minPrice)
        {
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): SKIP {name} at {entry.Path} ({item.Width}x{item.Height}, price={price:N0}₽, perSlot={pricePerSlot:N0}₽ < min={minPrice:N0}₽)");
            return false;
        }
        return true;
    }

    // True for AI scavs (Savage side, excluding PlayerScavs which are routed through the PMC-style threshold
    // path).
    private static bool IsBotScav(BotOwner bot)
    {
        var profile = bot?.Profile;
        if (profile == null) return false;
        if (profile.Side != EPlayerSide.Savage) return false;
        return !profile.WillBeAPlayerScav();
    }

    private OperationResult<IItemOperationResult> FindPlace(DrainEntry entry, InventoryController inventoryController, string name, float price)
    {
        var targets = new[] { inventoryController.Inventory.Equipment };
        var place = ItemManipulator.QuickFindAppropriatePlace(
            entry.Item, inventoryController, targets,
            ItemManipulator.EMoveItemOrder.PickUp, true);
        if (!place.Succeeded)
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): SKIP {name} at {entry.Path} ({entry.Item.Width}x{entry.Item.Height}, price={price:N0}₽, no slot found — {DescribeFreeSpace(inventoryController)})");
        return place;
    }

    private async Task<bool> RunTransactionAsync(OperationResult<IItemOperationResult> place, string name, CancellationToken ct)
    {
        var inventoryController = _bot.GetPlayer?.InventoryController;
        if (inventoryController == null) return false;
        try
        {
            var result = await inventoryController.TryRunNetworkTransaction(place, null);
            if (!result.Succeed) Log.Warning($"OrbitLootHandler.Pickup({Nick}): TX FAILED on {name} (Error={result.Error})");
            return result.Succeed;
        }
        catch (System.OperationCanceledException) { throw; }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.Pickup({Nick}, {name}): tx THREW {e}");
            return false;
        }
    }

    private void RecordPickup(string name, float price, string path)
    {
        Stats.InventoryValue += price;
        Stats.TotalGained = Stats.InventoryValue - Stats.SpawnValue;
        Stats.LastItemsTaken = true;
        BumpLootActivity();
        var pathSuffix = string.IsNullOrEmpty(path) ? "" : $" at {path}";
        Log.Debug($"OrbitLootHandler.Pickup({Nick}): ✓ PICKED {name}{pathSuffix} ({price:N0}₽), invValue={Stats.InventoryValue:N0}₽, gained={Stats.TotalGained:N0}₽");
    }

    // Recursive drain enumeration. Containers with grid contents (wallets, rigs, backpacks, pockets) emit
    // children before the wrapper so loose contents are extracted before the wrapper itself is moved. Slot
    // chains (weapon + mods, armor + plates) emit root-first. Non-RaidModdable weapon mods are skipped — they
    // can't be detached in raid.
    private static void EnumerateItemsForDrain(Item item, string parentPath, List<DrainEntry> output)
    {
        if (item == null) return;
        if (item is Mod mod && !mod.RaidModdable)
        {
            Log.Debug($"  drop non-RaidModdable mod {item.LocalizedName()} at {parentPath} (not detachable in-raid)");
            return;
        }

        var children = CollectImmediateChildren(item);
        var hasGridContents = item is CompoundItem ci
                              && ci.Grids != null
                              && ci.Grids.Length > 0
                              && ci.Grids.Any(g => g.Items != null && g.Items.Any());
        var nextPath = string.IsNullOrEmpty(parentPath) ? item.LocalizedName() : $"{parentPath}/{item.LocalizedName()}";

        if (hasGridContents)
        {
            foreach (var child in children)
                EnumerateItemsForDrain(child, nextPath, output);
            output.Add(new DrainEntry(item, parentPath));
        }
        else
        {
            output.Add(new DrainEntry(item, parentPath));
            foreach (var child in children)
                EnumerateItemsForDrain(child, nextPath, output);
        }
    }

    private static List<Item> CollectImmediateChildren(Item item)
    {
        var list = new List<Item>();
        if (item is CompoundItem compound)
        {
            if (compound.Slots != null)
                foreach (var slot in compound.Slots)
                    if (slot?.ContainedItem != null) list.Add(slot.ContainedItem);
            if (compound.Grids != null)
                foreach (var grid in compound.Grids)
                    if (grid?.Items != null)
                        foreach (var child in grid.Items)
                            if (child != null) list.Add(child);
        }
        return list;
    }

    // Items that always get picked regardless of per-slot value or scav roll: currency stacks, frag grenades
    // (tactical), and dogtags (quest hand-ins). Smokes / flashes / gas grenades stay on the normal gate.
    private static bool IsValueGateBypass(Item item, out string reason)
    {
        if (item is Money) { reason = "currency"; return true; }
        if (item is ThrowWeap throwable && throwable.ThrowType == ThrowWeapType.frag_grenade)
        {
            reason = "frag grenade";
            return true;
        }
        if (item.GetItemComponent<DogtagComponent>() != null)
        {
            reason = "dogtag";
            return true;
        }
        reason = null;
        return false;
    }

    /// <summary>
    /// Phase 4 mag-compat bypass. Mags and loose ammo whose caliber matches ANY weapon in
    /// <see cref="_postSwapWeapons"/> (the post-session loadout) bypass the value gate — they're useful
    /// even when below the per-archetype price threshold. Empty post-swap set falls through to the default
    /// value gate, so this is a no-op for non-corpse / non-container drains that haven't run the resolver.
    /// </summary>
    private bool FitsPostSwapWeapon(Item item, out string reason)
    {
        reason = null;
        if (_postSwapWeapons.Count == 0) return false;
        switch (item)
        {
            case Magazine mag:
                foreach (var w in _postSwapWeapons)
                {
                    var slot = w.GetMagazineSlot();
                    if (slot == null) continue;
                    try
                    {
                        if (slot.CheckCompatibility(mag))
                        {
                            reason = $"mag fits {w.LocalizedName()}";
                            return true;
                        }
                    }
                    catch { /* BSG can throw on filter probe — treat as no-fit */ }
                }
                return false;
            case Ammo ammo:
                foreach (var w in _postSwapWeapons)
                {
                    if (string.Equals(ammo.Caliber, w.AmmoCaliber, System.StringComparison.OrdinalIgnoreCase))
                    {
                        reason = $"ammo caliber matches {w.LocalizedName()}";
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Build <see cref="_postSwapWeapons"/> for the current session: walk the bot's slots, drop weapons
    /// that will be displaced by phase 4 swaps, add the phase 2 weapon winners (corpse) or empty-slot
    /// equips (container). Called once per session, before phase 1 drain runs.
    /// </summary>
    private void ResolvePostSwapWeapons(List<(string sourcePath, Weapon weapon)> corpseWeapons,
                                        List<(Weapon weapon, string path)> containerWeapons)
    {
        _postSwapWeapons.Clear();
        var displaced = new HashSet<string>();
        if (corpseWeapons != null)
        {
            foreach (var (_, w) in corpseWeapons)
            {
                var verdict = WeaponSwapper.WouldSwap(_bot, w, _currentSourceRoot);
                if (!verdict.WouldSwap) continue;
                _postSwapWeapons.Add(w);
                if (verdict.DisplacedWeapon != null) displaced.Add(verdict.DisplacedWeapon.Id);
            }
        }
        if (containerWeapons != null)
        {
            foreach (var (w, _) in containerWeapons)
            {
                if (CanEquipWeaponIntoEmptySlot(w)) _postSwapWeapons.Add(w);
            }
        }
        var equipment = _bot?.GetPlayer?.Inventory?.Equipment;
        if (equipment != null)
        {
            foreach (var slotKind in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
            {
                if (equipment.GetSlot(slotKind)?.ContainedItem is Weapon w
                    && !displaced.Contains(w.Id)
                    && !_postSwapWeapons.Contains(w))
                {
                    _postSwapWeapons.Add(w);
                }
            }
        }
        Log.Debug($"OrbitLootHandler.ResolvePostSwapWeapons({Nick}): {_postSwapWeapons.Count} post-swap weapon(s) — [{string.Join(", ", _postSwapWeapons.ConvertAll(w => $"{w.LocalizedName()}({w.AmmoCaliber})"))}]");
    }

    // Compact "bp=X/Y free, vest=…, pockets=…" summary used in no-slot logs.
    private static string DescribeFreeSpace(InventoryController inventoryController)
    {
        var equipment = inventoryController?.Inventory?.Equipment;
        if (equipment == null) return "equipment=null";
        var sb = new StringBuilder();
        AppendSlotFreeSpace(equipment, EquipmentSlot.Backpack, "bp", sb);
        AppendSlotFreeSpace(equipment, EquipmentSlot.TacticalVest, "vest", sb);
        AppendSlotFreeSpace(equipment, EquipmentSlot.Pockets, "pockets", sb);
        return sb.Length == 0 ? "no grids" : sb.ToString();
    }

    private static void AppendSlotFreeSpace(InventoryEquipment equipment, EquipmentSlot slotKind, string label, StringBuilder sb)
    {
        var slot = equipment.GetSlot(slotKind);
        var root = slot?.ContainedItem;
        if (root is not CompoundItem compound || compound.Grids == null || compound.Grids.Length == 0)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"{label}=none");
            return;
        }
        var totalCells = 0;
        var usedCells = 0;
        foreach (var grid in compound.Grids)
        {
            if (grid == null) continue;
            totalCells += grid.GridWidth * grid.GridHeight;
            if (grid.Items != null)
                foreach (var i in grid.Items)
                    if (i != null) usedCells += i.Width * i.Height;
        }
        var free = totalCells - usedCells;
        if (sb.Length > 0) sb.Append(", ");
        sb.Append($"{label}={free}/{totalCells} free");
    }

    /// <summary>One drain unit: the item to pick and its breadcrumb path for logs.</summary>
    public readonly struct DrainEntry
    {
        public readonly Item Item;
        public readonly string Path;
        public DrainEntry(Item item, string path) { Item = item; Path = path ?? ""; }
    }
}
