using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Inventory;

/// <summary>
/// Per-bot loot state machine. Lives as a MonoBehaviour on the bot's
/// GameObject so its lifetime tracks the bot's existence. The action
/// layer (LootContainerAction in <c>Tasks/Actions/</c>) drives this by
/// setting an active lootable then calling <see cref="StartLooting"/>;
/// the brain runs the appropriate async chain (corpse / container /
/// loose item), updates inventory stats, and cleans up.
/// </summary>
public class LootBrain : MonoBehaviour
{
    public BotOwner BotOwner;

    /// <summary>Inventory mutation surface.</summary>
    public BotInventoryController InventoryController;

    /// <summary>Lootable the bot is currently working on.</summary>
    public InteractableObject ActiveLoot;

    /// <summary>Discriminator for <see cref="ActiveLoot"/>.</summary>
    public LootKind ActiveLootType = LootKind.None;

    /// <summary>Final destination for the path-to-loot move order.</summary>
    public Vector3 Destination = Vector3.zero;

    /// <summary>Collider transform position; used by LoS raycast checks
    /// to avoid looting through walls.</summary>
    public Vector3 LootObjectPosition;

    /// <summary>Object ids the bot has already looted.</summary>
    public HashSet<string> IgnoredLootIds;

    /// <summary>Object ids that proved unreachable even though a valid
    /// path nominally exists. Cleared periodically (every 2 min default)
    /// so dynamic geometry changes can rehabilitate them.</summary>
    public HashSet<string> NonNavigableLootIds;

    public bool IsPlayerScav;

    public bool LockUntilNextScan;

    /// <summary>External overrides bypassing the per-faction enabled
    /// gate.</summary>
    public bool ForceBrainEnabled;

    public bool IsBrainEnabled
        => ForceBrainEnabled
            || (LootConfig.ContainerLootingEnabled?.Value ?? LootingFaction.None).IsBotEnabledForBrain(this)
            || (LootConfig.LooseItemLootingEnabled?.Value ?? LootingFaction.None).IsBotEnabledForBrain(this)
            || (LootConfig.CorpseLootingEnabled?.Value ?? LootingFaction.None).IsBotEnabledForBrain(this);

    public BotStats Stats => InventoryController.Stats;

    public bool HasActiveLootable => ActiveLootType is not LootKind.None && ActiveLoot != null;

    public bool IsBotLooting => LootTaskRunning || HasActiveLootable;

    public bool HasFreeSpace => Stats.AvailableGridSpaces > LootHelpers.RESERVED_SLOT_COUNT;

    /// <summary>True while the looting coroutine is active.</summary>
    public bool LootTaskRunning { get; private set; }

    public float DistanceToLoot = float.MaxValue;

    /// <summary>Container-open kneel delay before the actual pickup
    /// chain begins. Mimics the UI animation a real player sees.</summary>
    public const double LootingStartDelay = 2500D;

    private BotLog _log;
    private CancellationTokenSource _lootingCts;

    public void Init(BotOwner botOwner)
    {
        _log = new BotLog(botOwner);
        BotOwner = botOwner;
        InventoryController = new BotInventoryController(BotOwner, this);
        IgnoredLootIds = [];
        NonNavigableLootIds = [];
    }

    /// <summary>
    /// Called automatically when this MonoBehaviour begins running.
    /// IMPORTANT: <see cref="IsPlayerScav"/> must be updated AFTER
    /// <see cref="Init"/> because SPT changes the WildSpawnType for
    /// PlayerScavs after that method runs.
    /// </summary>
    public void Start()
    {
        IsPlayerScav = BotOwner.Profile.WillBeAPlayerScav();
        LootClaimsCache.Init();
        // The upstream perf-throttling layer (cap-by-distance scan scheduler)
        // is gone: ORBIT's action manager / claim system is the gating
        // layer now, and only one bot per dispatch is ever in the loot
        // action at a time.
    }

    /// <summary>
    /// Per-frame upkeep. Reacts to active-loot disappearance (a player
    /// picked up the item, the container despawned) by cleaning up.
    /// </summary>
    public void Update()
    {
        try
        {
            if (BotOwner.BotState != EBotState.Active) return;
            if (!IsBrainEnabled) return;

            // Crack any nearby door we happen to be standing at.
            BotOwner.DoorOpener.UpdateDoorInteractionStatus();

            // If a player picks up an item we'd marked active, its
            // ItemOwner?.RootItem goes null. Clean up in that case.
            if (ActiveLoot == null) return;

            switch (ActiveLoot)
            {
                case LootableContainer container when container.ItemOwner?.RootItem != null:
                case LootItem lootItem when lootItem.ItemOwner?.RootItem != null:
                    return;
                default:
                    CleanupLoot(false, true);
                    break;
            }
        }
        catch (Exception e)
        {
            _log.LogError(e);
        }
    }

    /// <summary>Kicks off the looting coroutine for the current
    /// <see cref="ActiveLootType"/>.</summary>
    public void StartLooting()
    {
        StopLooting();

        LootTaskRunning = true;
        _lootingCts = new CancellationTokenSource(LootConfig.LootTimeout * 1000);

        if (_log.InfoEnabled)
            _log.LogInfo($"Trying to loot {ActiveLoot.GetLootName()} [{ActiveLootType}]. Looted: {Stats.Looted:N0}₽");

        switch (ActiveLootType)
        {
            case LootKind.Corpse:
                _ = LootCorpseAsync(_lootingCts.Token).ContinueWith(ExceptionHandler, TaskScheduler.Current);
                break;
            case LootKind.Container:
                _ = LootContainerAsync(_lootingCts.Token).ContinueWith(ExceptionHandler, TaskScheduler.Current);
                break;
            case LootKind.Item:
                _ = LootItemAsync(_lootingCts.Token).ContinueWith(ExceptionHandler, TaskScheduler.Current);
                break;
        }
    }

    public void StopLooting()
    {
        if (_lootingCts is null) return;

        _lootingCts.Cancel();
        _lootingCts.Dispose();
        _lootingCts = null;
    }

    public void OnDestroy() => StopLooting();

    private readonly Stopwatch _lootTimer = new();
    private readonly List<Item> _itemsToLoot = new(13);

    /// <summary>Loots a corpse — priority order: weapons-or-storage,
    /// then everything else.</summary>
    private async Task LootCorpseAsync(CancellationToken token)
    {
        var isSuccessful = false;
        try
        {
            _lootTimer.Restart();

            if (ActiveLoot.GetRootItem() is not InventoryEquipment corpseInventoryEquipment)
            {
                if (_log.DebugEnabled)
                    _log.LogDebug($"ActiveLoot.Item for Corpse [{ActiveLoot.GetLootName()}] was not InventoryEquipment!");
                return;
            }

            // Walk corpse slots in priority order.
            _itemsToLoot.Clear();
            corpseInventoryEquipment.GetPriorityItems(BotOwner.InventoryController.Inventory.Equipment, _itemsToLoot);

            await LootTransactionController.SimulatePlayerDelayAsync(LootingStartDelay, token);

            isSuccessful = await InventoryController.TryAddItemsToBotAsync(_itemsToLoot, token);
        }
        finally
        {
            OnLootTaskEnd(isSuccessful);

            if (_log.InfoEnabled)
                _log.LogInfo($"Corpse loot time: {_lootTimer.ElapsedMilliseconds / 1000f:F0}s. Looted: {Stats.Looted:N0}₽. Was successful: {isSuccessful}");
        }
    }

    /// <summary>Loots a container — opens it if shut, walks every
    /// item, optionally closes when done.</summary>
    private async Task LootContainerAsync(CancellationToken token)
    {
        var isSuccessful = false;
        try
        {
            _lootTimer.Restart();

            if (ActiveLoot is not LootableContainer container || container.ItemOwner?.RootItem is not { } item)
            {
                if (_log.WarningEnabled) _log.LogWarning("Tried to loot container but container is empty");
                return;
            }

            var didOpen = false;
            if (container.DoorState == EDoorState.Shut)
            {
                LootHelpers.InteractContainer(container, BotOwner, EInteractionType.Open, _log);
                didOpen = true;
            }

            await LootTransactionController.SimulatePlayerDelayAsync(LootingStartDelay, token);

            isSuccessful = await InventoryController.LootNestedItemsAsync(item, token);

            // Close the container if the setting says so, or if we opened it.
            if (isSuccessful && (LootConfig.BotsAlwaysCloseContainers || !didOpen))
                LootHelpers.InteractContainer(container, BotOwner, EInteractionType.Close, _log);
        }
        finally
        {
            OnLootTaskEnd(isSuccessful);

            if (_log.InfoEnabled)
                _log.LogInfo($"Container loot time: {_lootTimer.ElapsedMilliseconds / 1000f:F0}s. Looted: {Stats.Looted:N0}₽. Was successful: {isSuccessful}");
        }
    }

    /// <summary>Picks up a single loose item.</summary>
    public async Task LootItemAsync(CancellationToken token)
    {
        var isSuccessful = false;
        try
        {
            _lootTimer.Restart();

            var item = ActiveLoot.GetRootItem();
            if (item == null)
            {
                if (_log.WarningEnabled) _log.LogWarning("Trying to pick up loose item but is NULL");
                return;
            }

            _itemsToLoot.Clear();
            _itemsToLoot.Add(item);
            isSuccessful = await InventoryController.TryAddItemsToBotAsync(_itemsToLoot, token);
        }
        finally
        {
            OnLootTaskEnd(isSuccessful);

            if (_log.InfoEnabled)
                _log.LogInfo($"Loose item loot time: {_lootTimer.ElapsedMilliseconds / 1000f:F0}s. Looted: {Stats.Looted:N0}₽. Was successful: {isSuccessful}");
        }
    }

    public void OnLootTaskEnd(bool lootingSuccessful)
    {
        _lootTimer.Stop();

        // Item ownership transfers during loot, so we have to clean up
        // explicitly. Only ignore + clear on success — failed attempts
        // (interrupted by combat, etc.) should remain retryable.
        CleanupLoot(lootingSuccessful);

        InventoryController.UpdateActiveWeapon();
        InventoryController.UpdateGridStats();
        BotOwner.AIData.CalcPower();
        LootTaskRunning = false;
    }

    public void UpdateGridStats() => InventoryController.UpdateGridStats();

    /// <summary>
    /// True if the bot should skip this lootable — either marked
    /// unreachable, or in its personal "already looted" set.
    /// </summary>
    public bool IsLootIgnored(string lootId)
        => lootId == null || NonNavigableLootIds.Contains(lootId) || IgnoredLootIds.Contains(lootId);

    /// <summary>
    /// True if this item exceeds the bot's per-slot value threshold.
    /// PMC bots use the PMC range; everyone else uses the scav range.
    /// </summary>
    public bool IsValuableEnough(Item lootItem)
    {
        var itemValue = LootConfig.ItemValuator.GetItemPrice(lootItem, _log);
        // Divide by slots to get price per slot — a 3×1 weapon worth
        // 100k counts as ~33k/slot for the threshold comparison.
        return InventoryController.IsValuableEnough(itemValue / lootItem.GetItemSize());
    }

    /// <summary>
    /// Mark the current active loot as non-navigable (we tried to reach
    /// it and couldn't) and clean up.
    /// </summary>
    public void HandleNonNavigableLoot()
    {
        var lootId = ActiveLoot.GetRootItemId();

        if (lootId != null) NonNavigableLootIds.Add(lootId);

        Cleanup();
    }

    /// <summary>Permanently ignore this loot id on subsequent picks.</summary>
    public void IgnoreLoot(string id) => IgnoredLootIds.Add(id);

    /// <summary>Push the active loot into the ignore list and drop the
    /// active state. No-op if no active loot.</summary>
    public void Cleanup()
    {
        if (ActiveLoot != null) CleanupLoot();
    }

    /// <summary>
    /// Clear the active loot. <paramref name="ignore"/>=true adds it to
    /// the bot's permanent ignore list; <paramref name="clear"/>=true
    /// forces a re-scan even when not ignoring.
    /// </summary>
    public void CleanupLoot(bool ignore = true, bool clear = false)
    {
        var item = ActiveLoot.GetRootItem();
        if (item != null && ignore)
            IgnoreLoot(item.Id);

        if (ignore || clear)
            SetLoot(null, LootKind.None, Vector3.zero, Vector3.zero);

        LootClaimsCache.Cleanup(BotOwner);
    }

    public void SetLoot(
        InteractableObject interactableObject,
        LootKind lootType,
        Vector3 position,
        Vector3 destination,
        float dist = float.MaxValue)
    {
        ActiveLoot = interactableObject;
        ActiveLootType = lootType;
        LootObjectPosition = position;
        Destination = destination;
        DistanceToLoot = dist;
    }

    private void ExceptionHandler(Task task)
    {
        if (task.IsCanceled)
        {
            if (_lootTimer.ElapsedMilliseconds / 1000L > LootConfig.LootTimeout)
            {
                if (_log.WarningEnabled)
                    _log.LogWarning($"Looting interrupted due to timeout ({LootConfig.LootTimeout}s)");
            }
            else if (_log.DebugEnabled)
            {
                _log.LogDebug("Looting interrupted");
            }
            return;
        }

        if (task.IsFaulted)
        {
            _log.LogError("Exception while trying to loot:");
            _log.LogError(task.Exception!.ToString());
        }
    }
}
