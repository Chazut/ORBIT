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

// BSG keeps its corpse selection, approach, waits, inventory decisions and transaction results.
// Only the portions requiring a physical body are deferred, before their native state advances.
internal static class NativeGhostCorpse
{
    private const int LootWeapon = 1, CheckBackpack = 2, LootAllItemsMoving = 4, DropBodyVest = 5, DropBodyBackpack = 6;
    private const float TransactionTimeout = 15f;
    private static readonly Func<BotDeadBodyWork, object> Body = Reader<object>("_activeBody");
    private static readonly Func<BotDeadBodyWork, Player> BodyPlayer = Reader<Player>("_activeBody", "Player");
    private static readonly Func<BotDeadBodyWork, Vector3> Position = Reader<Vector3>("_activeBody", "Position");
    private static readonly Func<BotDeadBodyWork, bool> Staying = Reader<bool>("_isStay");
    private static readonly Func<BotDeadBodyWork, bool> Looking = Reader<bool>("_lookInProgress");
    private static readonly Func<BotDeadBodyWork, float> NextCloseCheck = Reader<float>("_nextCheckClose");
    private static readonly Func<BotDeadBodyWork, bool> LastClose = Reader<bool>("_lastDesicion");
    private static readonly Func<BotDeadBodyWork, Task> MoveTask = Reader<Task>("_moveItemTask");
    private static readonly Func<BotDeadBodyWork, int> Index = Reader<int>("_curItemIndex");
    private static readonly Func<BotDeadBodyWork, List<Item>> Items = Reader<List<Item>>("_itemsCache");
    private static readonly Func<BotDeadBodyWork, int> State = StateReader();
    private static readonly ConditionalWeakTable<BotDeadBodyWork, Transfer> Transfers = new();
    private static readonly ConditionalWeakTable<BotDeadBodyWork, Report> Reports = new();

    private sealed class Report { internal object Body; }
    private sealed class Transfer
    {
        internal Task Task;
        internal Item Item;
        internal object Body;
        internal float Started;
        internal readonly HashSet<ItemEventArgs> Events = new();
    }

    private static bool Ready => Body != null && BodyPlayer != null && Position != null
        && Staying != null && Looking != null && NextCloseCheck != null && LastClose != null
        && MoveTask != null && Index != null && Items != null && State != null;

    // Compile the field accessors at plugin initialization, before raid frames are measured.
    internal static void Prepare() { _ = Ready; }

    private static Func<BotDeadBodyWork, T> Reader<T>(string fieldName, string member = null)
    {
        try
        {
            var field = AccessTools.Field(typeof(BotDeadBodyWork), fieldName);
            if (field == null) return null;
            var data = Expression.Parameter(typeof(BotDeadBodyWork), "data");
            Expression value = Expression.Field(data, field);
            if (member != null) value = Expression.PropertyOrField(value, member);
            if (!typeof(T).IsAssignableFrom(value.Type)) return null;
            return Expression.Lambda<Func<BotDeadBodyWork, T>>(Expression.Convert(value, typeof(T)), data).Compile();
        }
        catch { return null; }
    }

    private static Func<BotDeadBodyWork, int> StateReader()
    {
        try
        {
            var field = AccessTools.Field(typeof(BotDeadBodyWork), "_curLookState");
            var names = new[] { "Initial", "LootWeapon", "CheckBackpack", "LootAllCalculations",
                "LootAllItemsMoving", "DropBodyVest", "DropBodyBackpack", "Exit" };
            if (field == null || !field.FieldType.IsEnum || Enum.GetValues(field.FieldType).Length != names.Length) return null;
            for (var i = 0; i < names.Length; i++)
                if (Enum.GetName(field.FieldType, i) != names[i]) return null;
            var data = Expression.Parameter(typeof(BotDeadBodyWork), "data");
            return Expression.Lambda<Func<BotDeadBodyWork, int>>(
                Expression.Convert(Expression.Field(data, field), typeof(int)), data).Compile();
        }
        catch { return null; }
    }

    internal static bool Supports(BotOwner bot, BotLogicDecision decision)
        => decision == BotLogicDecision.deadBody && Ready && bot?.DeadBodyWork != null;

    internal static bool HasPendingTransfer(BotOwner bot)
        => Ready && bot?.DeadBodyWork is { } data && MoveTask(data) != null;

    private static bool Close(BotOwner bot, BotDeadBodyWork data)
    {
        // Match IsClose's native two-second cache without mutating it during CanSleep.
        if (Time.time <= NextCloseCheck(data)) return LastClose(data);
        var delta = bot.Position - Position(data);
        return delta.x * delta.x + delta.z * delta.z < 1.7f * 1.7f && Mathf.Abs(delta.y) < 1.3f;
    }

    internal static string BodyReason(BotOwner bot)
    {
        var data = bot?.DeadBodyWork;
        if (data == null) return null;
        if (!Ready) return data.ShallUse ? "native-corpse-contract" : null;
        var task = MoveTask(data);
        if (task != null)
        {
            if (!NativeGhostSystem.RetainsNativeState(bot)
                || !Transfers.TryGetValue(data, out var transfer) || transfer.Task != task)
                return "native-corpse-unowned-transaction";
            if (task.IsFaulted || task.IsCanceled) return "native-corpse-transaction-failed";
            if (!task.IsCompleted && Time.time - transfer.Started >= TransactionTimeout)
                return "native-corpse-transaction-timeout";
            if (!data.ShallUse || Body(data) != transfer.Body || State(data) != LootAllItemsMoving)
                return "native-corpse-transaction-state";
        }
        if (!data.ShallUse) return null;
        if (Body(data) == null || BodyPlayer(data)?.InventoryController == null) return "native-corpse-target-lost";
        var mode = (EDeadBodyWorkWay)bot.Settings.FileSettings.Mind.HOW_WORK_OVER_DEAD_BODY;
        if (mode != EDeadBodyWorkWay.SimpleLook && mode != EDeadBodyWorkWay.UseSurgicalKit
            && mode != EDeadBodyWorkWay.LootBody) return "native-corpse-mode";
        if (!Close(bot, data)) return null;
        if (!Staying(data))
            return mode == EDeadBodyWorkWay.UseSurgicalKit && bot.Medecine?.SurgicalKit?.HaveSmth2Use == true
                ? "native-corpse-surgery" : null;
        var state = State(data);
        if (state < 0 || state > 7) return "native-corpse-state";
        if (!Looking(data)) return null;
        var inventory = bot.GetPlayer?.InventoryController;
        if (inventory == null) return "native-corpse-inventory-missing";
        var equipment = BodyPlayer(data).InventoryController.Inventory.Equipment;
        if (state == LootWeapon)
        {
            foreach (var slot in WeaponSlots)
                if (equipment.GetSlot(slot).ContainedItem is { } item && inventory.FindSlotToPickUp(item) != null)
                    return "native-corpse-equipment";
        }
        if (state == CheckBackpack && inventory.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem == null
            && equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem != null) return "native-corpse-equipment";
        if (state == DropBodyVest && equipment.GetSlot(EquipmentSlot.TacticalVest).ContainedItem != null
            || state == DropBodyBackpack && equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem != null)
            return "native-corpse-drop";
        return null;
    }

    private static readonly EquipmentSlot[] WeaponSlots =
        { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };

    internal static bool DeferUpdate(BotOwner bot)
    {
        if (!NativeGhostSystem.RetainsNativeState(bot)) return false;
        if (!NativeGhostSystem.OwnsInactiveMovement(bot)) return true;
        if (NativeGhostSystem.DeferUnsafeLoot(bot)) return true;
        var data = bot.DeadBodyWork;
        // A completed/released target can survive until the next brain decision. Do not tick a null corpse.
        if (data == null || !data.ShallUse) return true;
        var report = Reports.GetOrCreateValue(data);
        if (report.Body != Body(data))
        {
            report.Body = Body(data);
            Log.Debug($"NATIVE GHOST CORPSE: {bot.Profile.Nickname} retained native interaction; target={BodyPlayer(data)?.ProfileId} mode={(EDeadBodyWorkWay)bot.Settings.FileSettings.Mind.HOW_WORK_OVER_DEAD_BODY}");
        }
        return false;
    }

    internal static bool OwnsEvent(BotOwner bot, ItemEventArgs operation)
    {
        var data = bot?.DeadBodyWork;
        return Ready && data != null && NativeGhostSystem.RetainsNativeState(bot)
            && Transfers.TryGetValue(data, out var transfer) && transfer.Task != null
            && transfer.Task == MoveTask(data) && ReferenceEquals(operation.Item, transfer.Item)
            && transfer.Events.Contains(operation);
    }

    internal static void AfterUpdate(BotOwner bot)
    {
        if (!Ready || !NativeGhostSystem.OwnsInactiveMovement(bot)) return;
        var data = bot.DeadBodyWork;
        if (data == null) return;
        var task = MoveTask(data);
        if (task == null) { Transfers.Remove(data); return; }
        if (Transfers.TryGetValue(data, out var old) && old.Task == task) return;
        // Unlike PatrolLootPointsData, BSG increments this index immediately after starting the task.
        var index = Index(data) - 1;
        var items = Items(data);
        if (State(data) != LootAllItemsMoving || items == null || index < 0 || index >= items.Count) return;
        var transfer = new Transfer { Task = task, Item = items[index], Body = Body(data), Started = Time.time };
        foreach (var operation in bot.GetPlayer.InventoryController.SelectEvents<ItemEventArgs>())
            if (ReferenceEquals(operation.Item, transfer.Item)) transfer.Events.Add(operation);
        Transfers.Remove(data);
        Transfers.Add(data, transfer);
        Log.Debug($"NATIVE GHOST CORPSE: {bot.Profile.Nickname} storage transfer started; item={transfer.Item?.Id}");
    }

    internal static void Forget(BotOwner bot)
    {
        var data = bot?.DeadBodyWork;
        if (data == null) return;
        Transfers.Remove(data);
        Reports.Remove(data);
    }
}
