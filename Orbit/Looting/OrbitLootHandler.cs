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
    // Fallback per-slot threshold for bots without an archetype-resolved value
    // (PlayerScavs, or PMCs while SAIN attach is still pending).
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

    // Non-PMC corpses keep Scabbard (melee) lootable. PMC melee is body-bound
    // in live and excluded above.
    private static readonly EquipmentSlot[] CorpseLootableSlotsNonPmc =
    {
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear,
        EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack,
        EquipmentSlot.Pockets, EquipmentSlot.Scabbard, EquipmentSlot.Dogtag,
    };

    // Slots that require a search animation (contents hidden until inspected).
    // Other slots are visible on the body and grabbed without a reveal cycle.
    private static readonly HashSet<EquipmentSlot> SearchableCorpseSlots = new()
    {
        EquipmentSlot.TacticalVest, EquipmentSlot.ArmorVest,
        EquipmentSlot.Backpack, EquipmentSlot.Pockets,
    };

    private BotOwner _bot;
    private CancellationTokenSource _cts;

    public LootStats Stats { get; } = new();
    public bool LootTaskRunning { get; private set; }

    public InteractableObject CurrentTarget { get; set; }
    public LootKind CurrentTargetKind { get; set; } = LootKind.None;
    public Vector3 ApproachPosition { get; set; }
    public Vector3 TargetWorldPosition { get; set; }
    public bool ForceEnabled { get; set; }

    private string Nick => _bot?.GetPlayer?.Profile?.Nickname ?? "(no-bot)";

    public void Init(BotOwner bot)
    {
        _bot = bot;
        Stats.SpawnValue = ItemPriceLookup.SumInventoryWorth(bot);
        Stats.InventoryValue = Stats.SpawnValue;
        Stats.TotalGained = 0f;
        Log.Debug($"OrbitLootHandler.Init({Nick}): spawnValue={Stats.SpawnValue:N0}₽, minPickupFallback={DefaultMinPickupPrice:N0}₽");
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
        Stats.LastItemsTaken = false;
        Stats.LastMaxPerSlotSeen = 0f;
        Stats.LastHadBypassItem = false;

        Log.Info($"OrbitLootHandler.StartLooting({Nick}): kind={CurrentTargetKind}, target={CurrentTarget.name}, minPrice={GetMinPickupPrice():N0}₽");

        var loot = CurrentTarget;
        var kind = CurrentTargetKind;
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
            // RootItem is the container fixture itself (not pickable); walk its
            // immediate children only.
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

        // Two interleaved timelines merged into one chronological queue:
        // visible-track grabs are spaced by InstantGrabDelayMs, search-track
        // slots are sequential with progressive per-item reveal. Slot order
        // is randomised per track for natural variation.
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

    // Progressive reveal: each item becomes discoverable at
    // InitialSearchMs + i × PerItemRevealMs (capped). Grabs fire as soon as
    // the reveal time has elapsed, so a fast grab waits for the next reveal
    // and a slow grab transitions straight to an already-visible item.
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

    // Silent inventory→inventory transfer (container/corpse path). No per-item
    // Pickup state — the open/inspect animation already played once.
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

    // Loose world item path: kneel animation per pickup via the player's
    // managed Pickup state.
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
        // Currency / frag grenades / dogtags bypass the value gate and the
        // scav random roll.
        if (IsValueGateBypass(item, out var bypassReason))
        {
            Stats.LastHadBypassItem = true;
            Log.Debug($"OrbitLootHandler.Pickup({Nick}): {name} at {entry.Path} bypasses value gate ({bypassReason}, perSlot={pricePerSlot:N0}₽)");
            return true;
        }
        // Bot scavs use a per-item random roll instead of a value threshold —
        // mirrors vanilla opportunistic looting. PlayerScavs and PMCs continue
        // to the per-archetype gate below.
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
        // PMC / PlayerScav: per-archetype threshold gate. Tracks the highest
        // non-bypass perSlot so the post-loot blacklist can identify which
        // squadmates would also reject this POI.
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

    // True for AI scavs (Savage side, excluding PlayerScavs which are
    // routed through the PMC-style threshold path).
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
        Stats.InventoryValue += price;
        Stats.TotalGained = Stats.InventoryValue - Stats.SpawnValue;
        Stats.LastItemsTaken = true;
        var pathSuffix = string.IsNullOrEmpty(path) ? "" : $" at {path}";
        Log.Info($"OrbitLootHandler.Pickup({Nick}): ✓ PICKED {name}{pathSuffix} ({price:N0}₽), invValue={Stats.InventoryValue:N0}₽, gained={Stats.TotalGained:N0}₽");
    }

    // Recursive drain enumeration. Containers with grid contents (wallets,
    // rigs, backpacks, pockets) emit children before the wrapper so loose
    // contents are extracted before the wrapper itself is moved. Slot chains
    // (weapon + mods, armor + plates) emit root-first. Non-RaidModdable
    // weapon mods are skipped — they can't be detached in raid.
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

    // Items that always get picked regardless of per-slot value or scav roll:
    // currency stacks, frag grenades (tactical), and dogtags (quest hand-ins).
    // Smokes / flashes / gas grenades stay on the normal gate.
    private static bool IsValueGateBypass(Item item, out string reason)
    {
        if (item is MoneyItemClass) { reason = "currency"; return true; }
        if (item is ThrowWeapItemClass throwable && throwable.ThrowType == ThrowWeapType.frag_grenade)
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
