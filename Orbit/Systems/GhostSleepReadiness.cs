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
            if (selector?.IsChanging == true || selector?.IsWeaponReady == false) return "weapon-return-pending";
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
        if (reason != null || !ReferenceEquals(state.Hands, hands))
        {
            state.Hands = hands;
            state.ReadyAt = Time.time + SettleSeconds;
        }
        if (reason == null && Time.time < state.ReadyAt) reason = "hands-settling";
        if (reason != null)
        {
            if (!state.Blocked || Time.time >= state.ReportAt)
            {
                Log.Info($"GHOST HANDS WAIT: {player.ProfileId} reason={reason}");
                PerformanceJournal.Event("ghost-hands-wait", player.ProfileId, detail: reason);
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
}
