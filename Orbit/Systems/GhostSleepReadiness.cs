using System;
using System.Runtime.CompilerServices;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

// Admission only. Do not use this settling window for bodies already asleep.
internal static class GhostSleepReadiness
{
    private sealed class State
    {
        internal object Hands;
        internal float ReadyAt;
        internal float ReportAt;
        internal bool Blocked;
        internal float BlockedAt;
        internal string Reason;
        internal float StableSwitchSince = -1f;
        internal object StableWeapon;
    }
    private static readonly ConditionalWeakTable<Player, State> States = new();
    internal const float SettleSeconds = 0.75f;

    internal static string BlockReason(BotOwner bot)
    {
        try
        {
            var player = bot?.GetPlayer;
            if (player?.InventoryController == null || player.HandsController == null) return "hands-unavailable";
            if (bot.Medecine?.Using == true) return "medical-operation";
            if (bot.WeaponManager?.Grenades?.ThrowindNow == true) return "grenade-operation";
            if (bot.WeaponManager?.Reload?.Reloading == true) return "reload-operation";
            var selector = bot.WeaponManager?.Selector;
            // Native OnWeaponTaken clears IsChanging for knives, but the
            // firearm-ready flag is only restored by UpdateHandsController's
            // firearm branch. A settled knife must not wait for that flag.
            if (selector?.IsChanging == true || selector?.IsWeaponReady == false
                && player.HandsController is not Player.KnifeController) return "weapon-return-pending";
            if (player.HandsController is not Player.FirearmController
                && player.HandsController is not Player.KnifeController
                && player.HandsController is not Player.EmptyHandsController) return "temporary-hands-controller";
            if (GhostBodyTransition.Busy(player)) return "body-operation";
            if (player.InventoryController.IsInventoryBlocked()) return "inventory-blocked";
            foreach (var operation in player.InventoryController.SelectEvents<ItemEventArgs>())
                return "inventory-operation";
            return null;
        }
        catch { return "hands-check-unavailable"; }
    }

    internal static bool Ready(BotOwner bot)
    {
        var player = bot?.GetPlayer;
        if (player == null) return false;
        var state = States.GetValue(player, _ => new State { ReadyAt = Time.time + SettleSeconds });
        var reason = BlockReason(bot);
        var hands = player.HandsController;
        // A native selector callback can leave IsChanging set after the gun is
        // already idle. Do not freeze a genuine transition or force a weapon draw.
        if (reason == "weapon-return-pending" && SettledSwitch(bot))
        {
            var weapon = ((Player.FirearmController)hands).Item;
            if (!ReferenceEquals(state.Hands, hands) || !ReferenceEquals(state.StableWeapon, weapon)
                || state.StableSwitchSince < 0f || Time.time < state.StableSwitchSince)
            { state.StableSwitchSince = Time.time; state.StableWeapon = weapon; }
            if (Time.time - state.StableSwitchSince >= 3f)
            {
                bot.WeaponManager.Selector.IsChanging = false;
                state.StableSwitchSince = -1f;
                state.ReadyAt = Time.time + SettleSeconds;
                reason = BlockReason(bot);
                Log.Info($"GHOST HANDS RECOVERY: {player.ProfileId} cleared stale selector after stable idle weapon confirmation");
                PerformanceJournal.Event("ghost-hands-recovery", player.ProfileId, detail: "stable-idle-selector");
            }
        }
        else { state.StableSwitchSince = -1f; state.StableWeapon = null; }
        if (reason != null || !ReferenceEquals(state.Hands, hands))
        {
            state.Hands = hands;
            state.ReadyAt = Time.time + SettleSeconds;
        }
        if (reason == null && Time.time < state.ReadyAt) reason = "hands-settling";
        if (reason != null)
        {
            if(!state.Blocked || state.Reason!=reason)state.BlockedAt=Time.time;
            state.Reason=reason;
            if (!state.Blocked || Time.time >= state.ReportAt)
            {
                var detail=$"{reason} waited={Math.Max(0,Time.time-state.BlockedAt):F2}s {Describe(bot)}";
                Log.Info($"GHOST HANDS WAIT: {player.ProfileId} reason={detail}");
                PerformanceJournal.Event("ghost-hands-wait", player.ProfileId, detail: detail);
                state.ReportAt = Time.time + 10f;
            }
            state.Blocked = true;
            return false;
        }
        if (state.Blocked)
        {
            Log.Info($"GHOST HANDS READY: {player.ProfileId} settled before sleep");
            PerformanceJournal.Event("ghost-hands-ready", player.ProfileId);
            state.Blocked = false;
        }
        return true;
    }
    private static bool SettledSwitch(BotOwner bot)
    {
        try
        {
            var p = bot.GetPlayer; var wm = bot.WeaponManager; var selector = wm?.Selector;
            if (selector?.IsChanging != true || !selector.IsWeaponReady || !wm.IsReady
                || p.HandsController is not Player.FirearmController gun
                || gun.CurrentOperation is not Player.FirearmController.Idling
                || gun.IsInReloadOperation() || GhostBodyTransition.Busy(p)
                || p.InventoryController.IsInventoryBlocked()
                || !ReferenceEquals(wm.ShootController, gun) || !ReferenceEquals(wm.CurrentWeapon, gun.Item)
                || !ReferenceEquals(p.Inventory.Equipment.GetSlot(selector.EquipmentSlot).ContainedItem, gun.Item)) return false;
            foreach (var op in p.InventoryController.SelectEvents<ItemEventArgs>()) return false;
            return true;
        }
        catch { return false; }
    }
    private static string Describe(BotOwner bot)
    {
        try
        {
            var p=bot.GetPlayer;var selector=bot.WeaponManager?.Selector;
            return $"hands={p.HandsController?.GetType().Name ?? "none"} changing={selector?.IsChanging} weaponReady={selector?.IsWeaponReady} bodyBusy={GhostBodyTransition.Busy(p)} inventoryBlocked={p.InventoryController?.IsInventoryBlocked()}";
        }
        catch{return "state=unavailable";}
    }
}
