using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Systems;

// Keep the native arrival, waits, item selection and transaction completion. Equipment changes
// still run awake: the native equip path can throw worn gear or require a hands animation.
internal static class NativeGhostLoot
{
    private const int EquipItems = 1, LootItems = 2;
    private const float TransactionTimeout = 15f;
    private static readonly Func<PatrolLootPointsData, bool> Looting = Reader<bool>("_lootingNow");
    private static readonly Func<PatrolLootPointsData, Task> MoveTask = Reader<Task>("_moveItemTask");
    private static readonly Func<PatrolLootPointsData, int> State = StateReader();
    private static readonly Func<PatrolLootPointsData, int> Index = Reader<int>("_moveItemIndex");
    private static readonly Func<PatrolLootPointsData, List<Item>> Equipment = Reader<List<Item>>("_allItemsToLootCache");
    private static readonly Func<PatrolLootPointsData, List<Item>> Storage = Reader<List<Item>>("_notEquipableItemsToLoot");
    private static readonly ConditionalWeakTable<PatrolLootPointsData, Transfer> Transfers = new();

    private sealed class Transfer
    {
        internal Task Task;
        internal Item Item;
        internal AILootPoint Point;
        internal float Started;
        internal readonly HashSet<ItemEventArgs> Events = new();
    }

    private static bool Ready => Looting != null && MoveTask != null && State != null
        && Index != null && Equipment != null && Storage != null;

    private static Func<PatrolLootPointsData, T> Reader<T>(string name)
    {
        try
        {
            var field = AccessTools.Field(typeof(PatrolLootPointsData), name);
            if (field == null || !typeof(T).IsAssignableFrom(field.FieldType)) return null;
            var data = Expression.Parameter(typeof(PatrolLootPointsData), "data");
            return Expression.Lambda<Func<PatrolLootPointsData, T>>(
                Expression.Convert(Expression.Field(data, field), typeof(T)), data).Compile();
        }
        catch { return null; }
    }

    private static Func<PatrolLootPointsData, int> StateReader()
    {
        try
        {
            var field = AccessTools.Field(typeof(PatrolLootPointsData), "_lootState");
            if (field == null || !field.FieldType.IsEnum
                || Enum.GetName(field.FieldType, 0) != "AssembleAllItemsToLoot"
                || Enum.GetName(field.FieldType, EquipItems) != "EquipItems"
                || Enum.GetName(field.FieldType, LootItems) != "LootItems") return null;
            var data = Expression.Parameter(typeof(PatrolLootPointsData), "data");
            return Expression.Lambda<Func<PatrolLootPointsData, int>>(
                Expression.Convert(Expression.Field(data, field), typeof(int)), data).Compile();
        }
        catch { return null; }
    }

    internal static bool Supports(BotOwner bot, BotLogicDecision decision)
        => decision == BotLogicDecision.goToLootPointNode && Ready
            && bot?.PatrollingData?.LootData?.CachedLootPoint != null;

    internal static bool HasPendingTransfer(BotOwner bot)
        => Ready && bot?.PatrollingData?.LootData is { } data && MoveTask(data) != null;

    private static bool RealLoot(BotOwner bot)
        => bot?.Settings?.FileSettings?.Patrol?.USE_REAL_LOOTING == true;

    internal static string BodyReason(BotOwner bot)
    {
        var data = bot?.PatrollingData?.LootData;
        if (data == null || !RealLoot(bot)) return null;
        if (!Ready) return "native-loot-contract";
        var task = MoveTask(data);
        if (task != null)
        {
            // Never adopt a transaction started awake or by a different inventory path.
            if (!NativeGhostSystem.RetainsNativeState(bot)
                || !Transfers.TryGetValue(data, out var transfer) || transfer.Task != task)
                return "native-loot-unowned-transaction";
            if (task.IsFaulted || task.IsCanceled) return "native-loot-transaction-failed";
            if (!task.IsCompleted && Time.time - transfer.Started >= TransactionTimeout)
                return "native-loot-transaction-timeout";
            if (data.CachedLootPoint != transfer.Point || State(data) != LootItems)
                return "native-loot-transaction-state";
        }
        if (!Looting(data)) return null;
        if (data.CachedLootPoint == null) return "native-loot-target-lost";
        var state = State(data);
        if (state < 0 || state > LootItems) return "native-loot-state";
        if (state != EquipItems) return null;
        var inventory = bot.GetPlayer?.InventoryController;
        if (inventory == null) return "native-loot-inventory-missing";
        var items = Equipment(data);
        // Completion advances the index before processing the next item in the same native tick.
        var index = Index(data) + (task?.IsCompleted == true ? 1 : 0);
        if (items == null || index < 0) return "native-loot-state";
        if (index >= items.Count) return null;
        var item = items[index];
        if (item == null) return "native-loot-item-missing";
        return inventory.FindEquipmentSlotToReplaceWithBetterItem(item, true, out _) != null
            ? "native-loot-equipment" : null;
    }

    internal static bool OwnsEvent(BotOwner bot, ItemEventArgs operation)
    {
        var data = bot?.PatrollingData?.LootData;
        return data != null && Ready && NativeGhostSystem.RetainsNativeState(bot)
            && Transfers.TryGetValue(data, out var transfer) && transfer.Task == MoveTask(data)
            && transfer.Task != null && ReferenceEquals(operation.Item, transfer.Item)
            && transfer.Events.Contains(operation);
    }

    internal static bool DeferArrival(BotOwner bot) => Defer(bot);
    internal static bool DeferUpdate(BotOwner bot) => Defer(bot);

    private static bool Defer(BotOwner bot)
    {
        if (!NativeGhostSystem.RetainsNativeState(bot)) return false;
        if (!NativeGhostSystem.OwnsInactiveMovement(bot)) return true;
        return NativeGhostSystem.DeferUnsafeLoot(bot);
    }

    // The postfix only recognizes the storage transfer just started by this native update.
    // Keep the task in BSG's state, including through a wake: only BSG consumes its result.
    internal static void AfterUpdate(BotOwner bot)
    {
        if (!Ready || !NativeGhostSystem.OwnsInactiveMovement(bot) || !RealLoot(bot)) return;
        var data = bot.PatrollingData?.LootData;
        if (data == null) return;
        var task = MoveTask(data);
        if (task == null) { Transfers.Remove(data); return; }
        if (Transfers.TryGetValue(data, out var existing) && existing.Task == task) return;
        if (State(data) != LootItems) return;
        var items = Storage(data);
        var index = Index(data);
        if (items == null || index < 0 || index >= items.Count) return;
        Transfers.Remove(data);
        var transfer = new Transfer { Task = task, Item = items[index], Point = data.CachedLootPoint, Started = Time.time };
        foreach (var operation in bot.GetPlayer.InventoryController.SelectEvents<ItemEventArgs>())
            if (ReferenceEquals(operation.Item, transfer.Item)) transfer.Events.Add(operation);
        Transfers.Add(data, transfer);
        Log.Debug($"NATIVE GHOST LOOT: {bot.Profile.Nickname} storage transfer started; item={transfer.Item?.Id}");
    }

    internal static void Forget(BotOwner bot)
    {
        var data = bot?.PatrollingData?.LootData;
        if (data != null) Transfers.Remove(data);
    }
}
