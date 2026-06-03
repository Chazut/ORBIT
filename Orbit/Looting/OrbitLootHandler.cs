using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Orbit.Core;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Looting;

public class OrbitLootHandler : MonoBehaviour, ILootHandler
{
    // Fallback gate when the bot has no archetype-resolved threshold. Used
    // by PlayerScavs (no SAIN brain, but PMC-like loot behaviour) and as a
    // safety net for any PMC where SAIN async-attach is still pending.
    // 5,000₽ matches the LB-era ScavLootThreshold so PlayerScavs keep a
    // similar feel to pre-rewrite ORBIT.
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

    // Scavs + other bot factions: Scabbard (melee) is lootable. PMC corpses
    // drop the slot — matches EFT live behaviour where PMC melee is bound to
    // the body and stays with it.
    private static readonly EquipmentSlot[] CorpseLootableSlotsNonPmc =
    {
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear,
        EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack,
        EquipmentSlot.Pockets, EquipmentSlot.Scabbard, EquipmentSlot.Dogtag,
    };

    // Slots whose contents are hidden until the bot searches them. Other
    // slots (helmet, weapons, scabbard, etc.) are visible on the body and
    // can be grabbed without a reveal cycle.
    private static readonly HashSet<EquipmentSlot> SearchableCorpseSlots = new()
    {
        EquipmentSlot.TacticalVest, EquipmentSlot.ArmorVest,
        EquipmentSlot.Backpack, EquipmentSlot.Pockets,
    };

    private BotOwner _bot;
    private CancellationTokenSource _cts;

    public LootStats Stats { get; } = new();
    public bool LootTaskRunning { get; private set; }

    public InteractableObject ActiveLoot { get; set; }
    public LootKind ActiveLootType { get; set; } = LootKind.None;
    public Vector3 Destination { get; set; }
    public Vector3 LootObjectPosition { get; set; }
    public bool ForceBrainEnabled { get; set; }

    private string Nick => _bot?.GetPlayer?.Profile?.Nickname ?? "(no-bot)";

    public void Init(BotOwner bot)
    {
        _bot = bot;
        Stats.InitialNetWorth = ItemPriceLookup.SumInventoryWorth(bot);
        Stats.NetWorth = Stats.InitialNetWorth;
        Stats.Looted = 0f;
        Log.Debug($"OrbitLootHandler.Init({Nick}): initialNetWorth={Stats.InitialNetWorth:N0}₽, minPickupFallback={DefaultMinPickupPrice:N0}₽");
    }

    public void StartLooting()
    {
        if (LootTaskRunning)
        {
            Log.Debug($"OrbitLootHandler.StartLooting({Nick}): IGNORED — already running");
            return;
        }
        if (_bot == null || ActiveLoot == null || ActiveLootType == LootKind.None)
        {
            Log.Warning($"OrbitLootHandler.StartLooting({Nick}): IGNORED — bot={_bot != null}, ActiveLoot={ActiveLoot?.name}, kind={ActiveLootType}");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        LootTaskRunning = true;
        Stats.LastItemsTaken = false;
        Stats.LastMaxPerSlotSeen = 0f;
        Stats.LastHadBypassItem = false;

        Log.Info($"OrbitLootHandler.StartLooting({Nick}): kind={ActiveLootType}, target={ActiveLoot.name}, minPrice={GetMinPickupPrice():N0}₽");

        var loot = ActiveLoot;
        var kind = ActiveLootType;
        var ct = _cts.Token;

        _ = RunAsync(loot, kind, ct);
    }

    private async Task RunAsync(InteractableObject loot, LootKind kind, CancellationToken ct)
    {
        var sw = Time.realtimeSinceStartup;
        var takenBefore = Stats.LastItemsTaken;
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
            LootTaskRunning = false;
            var elapsed = Time.realtimeSinceStartup - sw;
            Log.Info($"OrbitLootHandler.RunAsync({Nick}): done in {elapsed:F1}s, kind={kind}, target={loot?.name}, ItemsTaken={Stats.LastItemsTaken} (was {takenBefore})");
        }
    }

    public void StopLooting()
    {
        if (LootTaskRunning) Log.Debug($"OrbitLootHandler.StopLooting({Nick})");
        _cts?.Cancel();
        LootTaskRunning = false;
    }

    public void Cancel()
    {
        if (LootTaskRunning) Log.Debug($"OrbitLootHandler.Cancel({Nick})");
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
            Log.Debug($"OrbitLootHandler.Container({Nick}, {container.name}): Interact(Open), waiting {ContainerOpenAnimMs}ms anim");
            _bot.LootOpener.Interact(container, EInteractionType.Open);
            await Task.Delay(ContainerOpenAnimMs, ct);
        }

        var rootItem = container.ItemOwner?.RootItem;
        if (rootItem == null)
        {
            Log.Warning($"OrbitLootHandler.Container({Nick}, {container.name}): ItemOwner.RootItem is null — can't enumerate items");
        }
        else
        {
            // Walk each top-level child of the container's RootItem (the
            // container itself is a world fixture — never picked). Tree-walk
            // emits grid-content leaves before their wrapper so wallets,
            // pouches etc. get drained inside-out, avoiding double-pick on
            // already-moved sub-items.
            var drain = new List<DrainEntry>();
            foreach (var child in CollectImmediateChildren(rootItem))
                EnumerateItemsForDrain(child, "", drain);
            Log.Info($"OrbitLootHandler.Container({Nick}, {container.name}): {drain.Count} drain entries, progressive reveal (initial {InitialSearchMs}ms + {PerItemRevealMs}ms each)");
            await DrainProgressiveAsync(drain, ct);
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

    private async Task LootCorpseAsync(Corpse corpse, CancellationToken ct)
    {
        // corpse.Item is the player's equipment composite. We enumerate
        // slots explicitly (skip SecuredContainer) and apply a per-slot
        // search delay scaled by the number of items in that slot —
        // mimics the player who inspects the rig, then the backpack,
        // then the pockets, etc.
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

        // Build a single chronological queue. Two parallel tracks share
        // the timeline but neither overlaps with itself:
        //   - Visible track: helmet → headset → weapons, each grab spaced
        //     by InstantGrabDelayMs (sequential thinking).
        //   - Search track: backpack, then vest, then pockets — one slot
        //     at a time. Within a slot, items are revealed progressively.
        //     A slot's search starts after the previous slot's last item
        //     reveal.
        // Both tracks run in parallel timeline-wise, but the bot drains
        // items in revealMs order so we get the natural interleave.
        // Randomize slot order so the bot doesn't always search backpack
        // first, vest second, etc. The two tracks (visible / searchable)
        // are shuffled independently.
        var visibleOrder = lootableSlots.Where(s => !SearchableCorpseSlots.Contains(s)).OrderBy(_ => Random.value).ToList();
        var searchOrder = lootableSlots.Where(s => SearchableCorpseSlots.Contains(s)).OrderBy(_ => Random.value).ToList();
        Log.Debug($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): side={corpse.Side} pmc={isPmc} visibleOrder=[{string.Join(",", visibleOrder)}] searchOrder=[{string.Join(",", searchOrder)}]");

        var queue = new List<(int revealMs, EquipmentSlot slot, DrainEntry entry)>();
        var visibleCursorMs = 0;
        foreach (var slotKind in visibleOrder)
        {
            var slot = inventoryEquipment.GetSlot(slotKind);
            var root = slot?.ContainedItem;
            if (root == null) continue;
            var slotItems = new List<DrainEntry>();
            EnumerateItemsForDrain(root, slotKind.ToString(), slotItems);
            foreach (var entry in slotItems)
            {
                queue.Add((visibleCursorMs, slotKind, entry));
                visibleCursorMs += InstantGrabDelayMs;
            }
        }

        var searchCursorMs = 0;
        foreach (var slotKind in searchOrder)
        {
            var slot = inventoryEquipment.GetSlot(slotKind);
            var root = slot?.ContainedItem;
            if (root == null) continue;
            var slotItems = new List<DrainEntry>();
            EnumerateItemsForDrain(root, slotKind.ToString(), slotItems);
            var lastRevealOffset = 0;
            for (var i = 0; i < slotItems.Count; i++)
            {
                var offset = System.Math.Min(InitialSearchMs + i * PerItemRevealMs, MaxRevealCapMs);
                queue.Add((searchCursorMs + offset, slotKind, slotItems[i]));
                lastRevealOffset = offset;
            }
            searchCursorMs += lastRevealOffset;
        }

        queue.Sort((a, b) => a.revealMs.CompareTo(b.revealMs));
        var totalEstimatedMs = queue.Count > 0 ? queue[queue.Count - 1].revealMs : 0;
        Log.Info($"OrbitLootHandler.Corpse({Nick}, {corpse.name}): drain queue size={queue.Count}, last reveal at {totalEstimatedMs}ms");
        for (var i = 0; i < queue.Count; i++)
        {
            var e = queue[i];
            Log.Debug($"  [{i}] T={e.revealMs}ms slot={e.slot} path={e.entry.Path} item={e.entry.Item.LocalizedName()} ({e.entry.Item.Width}x{e.entry.Item.Height}, price={ItemPriceLookup.GetPrice(e.entry.Item):N0}₽, perSlot={ItemPriceLookup.GetPricePerSlot(e.entry.Item):N0}₽)");
        }

        var startTime = Time.realtimeSinceStartup;
        for (var i = 0; i < queue.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = queue[i];
            var elapsedMs = (int)((Time.realtimeSinceStartup - startTime) * 1000);
            var waitMs = entry.revealMs - elapsedMs;
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
    }

    // Items become "discoverable" progressively as the bot searches. The
    // first item appears after InitialSearchMs, then every PerItemRevealMs
    // a new one is revealed. The pickup grabs each item as soon as its
    // reveal time has passed — so if a grab takes longer than the reveal
    // cadence, the next item is already visible and the bot moves straight
    // to it. If a grab is fast, the bot waits for the next reveal.
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
            var revealMs = System.Math.Min(InitialSearchMs + i * PerItemRevealMs, MaxRevealCapMs);
            var elapsedMs = (int)((Time.realtimeSinceStartup - startTime) * 1000);
            var waitMs = revealMs - elapsedMs;
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
        await Task.Delay(500, ct);
        await PickupLooseAsync(lootItem.Item, ct);
    }

    // Inventory→inventory transfer (container & corpse path). The open /
    // inspect animation already played once before this loop, so no
    // per-item Pickup state — items are dragged across silently.
    private Task<bool> TransferItemAsync(Item item, CancellationToken ct)
        => TransferItemAsync(new DrainEntry(item, ""), ct);

    private async Task<bool> TransferItemAsync(DrainEntry entry, CancellationToken ct)
    {
        if (!ValidatePickup(entry, out var name, out var price, out var pricePerSlot, out var inventoryController)) return false;
        var place = FindPlace(entry, inventoryController, name, price);
        if (!place.Succeeded) return false;

        Log.Debug($"OrbitLootHandler.Transfer({Nick}): TRY {name} at {entry.Path} (price={price:N0}₽, perSlot={pricePerSlot:N0}₽)");
        var success = await RunTransactionAsync(place, name, ct);
        if (success) RecordPickup(name, price, entry.Path);
        return success;
    }

    // Loose world item path — bot kneels via CurrentManagedState.Pickup
    // (one-shot animation per loose item).
    private async Task<bool> PickupLooseAsync(Item item, CancellationToken ct)
    {
        var entry = new DrainEntry(item, "");
        if (!ValidatePickup(entry, out var name, out var price, out var pricePerSlot, out var inventoryController)) return false;
        var player = _bot.GetPlayer;
        var place = FindPlace(entry, inventoryController, name, price);
        if (!place.Succeeded) return false;

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
        // Value-gate bypass: cash and frag grenades are always worth taking
        // regardless of per-slot price OR scav random roll. Cash stacks
        // dwarf the gate, frags are kept for tactical use, and a vanilla
        // scav grabs visible cash and grenades off a body too.
        if (IsValueGateBypass(item, out var bypassReason))
        {
            Stats.LastHadBypassItem = true;
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): {name} at {entry.Path} bypasses value gate ({bypassReason}, perSlot={pricePerSlot:N0}₽)");
            return true;
        }
        // Bot scavs (not PlayerScavs) roll random per item instead of using
        // a deterministic threshold. Mirrors vanilla: opportunistic pickups,
        // not a structured search. PlayerScavs and PMCs fall through to the
        // per-archetype threshold gate below.
        if (IsBotScav(_bot))
        {
            var chance = (LootConfig.ScavLootChancePct?.Value ?? 30) / 100f;
            var roll = Random.value;
            if (roll < chance)
            {
                Log.Debug($"OrbitLootHandler.Pickup({Nick}): scav KEEP {name} at {entry.Path} (roll {roll:F2} < {chance:F2}, perSlot={pricePerSlot:N0}₽)");
                return true;
            }
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): scav SKIP {name} at {entry.Path} (roll {roll:F2} ≥ {chance:F2}, perSlot={pricePerSlot:N0}₽)");
            return false;
        }
        // PMC / PlayerScav: per-archetype threshold gate. Track the highest
        // non-bypass perSlot encountered so the post-loot blacklist can
        // decide which squadmates would also have rejected this POI (their
        // threshold > this max → they'd skip too).
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

    /// <summary>
    /// True if this bot is a bot-controlled scav (WildSpawnType.assault +
    /// not a PlayerScav). Used to route the loot gate through the random-
    /// roll branch instead of the per-archetype threshold. PlayerScavs
    /// share Side=Savage but behave PMC-style in ORBIT and use the normal
    /// threshold path.
    /// </summary>
    private static bool IsBotScav(BotOwner bot)
    {
        var profile = bot?.Profile;
        if (profile == null) return false;
        if (profile.Side != EPlayerSide.Savage) return false;
        return !profile.WillBeAPlayerScav();
    }

    private GStruct154<GInterface424> FindPlace(DrainEntry entry, InventoryController inventoryController, string name, float price)
    {
        var targets = inventoryController.Inventory.Equipment.ToEnumerable<InventoryEquipment>();
        var place = InteractionsHandlerClass.QuickFindAppropriatePlace(
            entry.Item, inventoryController, targets,
            InteractionsHandlerClass.EMoveItemOrder.PickUp, true);
        if (!place.Succeeded)
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): SKIP {name} at {entry.Path} ({entry.Item.Width}x{entry.Item.Height}, price={price:N0}₽, no slot found — {DescribeFreeSpace(inventoryController)})");
        return place;
    }

    private async Task<bool> RunTransactionAsync(GStruct154<GInterface424> place, string name, CancellationToken ct)
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
        Stats.NetWorth += price;
        Stats.Looted = Stats.NetWorth - Stats.InitialNetWorth;
        Stats.LastItemsTaken = true;
        var pathSuffix = string.IsNullOrEmpty(path) ? "" : $" at {path}";
        Log.Info($"OrbitLootHandler.Pickup({Nick}): ✓ PICKED {name}{pathSuffix} ({price:N0}₽), netWorth={Stats.NetWorth:N0}₽, looted={Stats.Looted:N0}₽");
    }

    // Tree-walk for drain ordering.
    //   - Items with grid contents (Wallet, Rig, Backpack, Pockets): emit
    //     children FIRST so loose grid contents are picked before their
    //     wrapper. Otherwise picking the wrapper also moves all sub-items
    //     and the subsequent transaction on each sub-item fails (no longer
    //     in the corpse/container).
    //   - Items with only slot children (Weapon + mods, helmet + face-shield,
    //     armor + plates): emit SELF first, then children — natural mod
    //     chain order. Plates / mods are still pickable individually while
    //     the host is still on the corpse.
    //   - Non-RaidModdable weapon mods (barrel, stock, handguard, etc.)
    //     are physically not removable in raid by a player and are dropped
    //     from the queue entirely.
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

    // Items that always get picked regardless of per-slot value. Cash is
    // dense by definition (a single 1x1 stack is hundreds of thousands ₽
    // but a small fraction of that per slot at low stack counts — and the
    // bot's value lookup doesn't always multiply by stack). Frag grenades
    // are kept for tactical use, not resale value. Smokes / flashes / gas
    // stay on the normal gate — they're low-value and not interesting
    // enough for a Chad to bother carrying.
    private static bool IsValueGateBypass(Item item, out string reason)
    {
        if (item is MoneyItemClass) { reason = "currency"; return true; }
        if (item is ThrowWeapItemClass throwable && throwable.ThrowType == ThrowWeapType.frag_grenade)
        {
            reason = "frag grenade";
            return true;
        }
        // Dogtags: always picked up, regardless of perSlot value or scav
        // random roll. Quest hand-ins ("Tarkov Shooter Part X" etc.) and
        // PMC trophies — a real player never walks past one.
        if (item.GetItemComponent<DogtagComponent>() != null)
        {
            reason = "dogtag";
            return true;
        }
        reason = null;
        return false;
    }

    // One-line summary of the bot's remaining grid capacity. Logged on
    // "no slot found" so we can tell at-a-glance whether the bot was
    // actually full vs the item just didn't fit any shape-wise.
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

    /// <summary>
    /// One unit of drain work — an item to (try to) pick and the breadcrumb
    /// path to where it lives inside the corpse / container. Path is used
    /// for logs only; pickup logic reads <see cref="Item"/>.
    /// </summary>
    public readonly struct DrainEntry
    {
        public readonly Item Item;
        public readonly string Path;
        public DrainEntry(Item item, string path) { Item = item; Path = path ?? ""; }
    }
}
