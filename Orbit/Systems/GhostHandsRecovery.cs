using EFT;
using EFT.InventoryLogic;
using Orbit.Entities;
using Orbit.Looting;
using Orbit.Looting.WeaponSwap;
using UnityEngine;

namespace Orbit.Systems;

internal static class GhostHandsRecovery
{
    internal static void Tick(Agent agent)
    {
        if (!agent.GhostHandsResync || agent.IsDormant || Time.time < agent.GhostHandsRetryAt) return;
        agent.GhostHandsRetryAt = Time.time + 1f;
        var bot = agent.Bot;
        var player = agent.Player;
        if (!LootActorGuard.IsCurrent(bot, player) || bot.BotState != EBotState.Active
            || !bot.gameObject.activeInHierarchy) return;
        try
        {
            // A retained Ghost loot session can finish after the wake. Let it settle first.
            if (agent.LootHandler?.LootTaskRunning == true || agent.LootHandler?.CanEnterGhost == false
                || GhostBodyTransition.Busy(player) || bot.Medecine?.Using == true
                || bot.WeaponManager?.Reload?.Reloading == true || bot.WeaponManager?.Grenades?.ThrowindNow == true)
                return;
            foreach (var operation in player.InventoryController.SelectEvents<ItemEventArgs>()) return;
            var selector = bot.WeaponManager?.Selector;
            if (selector == null || !selector.IsWeaponReady) return;
            if (agent.GhostHandsDrawPending)
            {
                // Native completion must leave real, owned equipment in the hands. A successful
                // request alone is insufficient: its asynchronous animation can still fail.
                var held = player.HandsController?.Item;
                var slot = player.Inventory?.Equipment?.GetSlot(selector.LastEquipmentSlot);
                if (held != null && ReferenceEquals(held, slot?.ContainedItem))
                {
                    agent.GhostHandsResync = agent.GhostHandsDrawPending = false;
                    Log.Debug($"GHOST HANDS: {agent} weapon redraw confirmed");
                    return;
                }
                agent.GhostHandsDrawPending = false;
            }
            // Do not start another draw while the combat controller needs the current hands.
            if (bot.Memory?.HaveEnemy == true || bot.Memory?.IsUnderFire == true) return;
            agent.GhostHandsDrawPending = WeaponSwapper.ResyncHandsAfterWake(bot, agent.ToString());
        }
        catch (System.Exception e)
        {
            agent.GhostHandsRetryAt = Time.time + 5f;
            Log.Warning($"GHOST HANDS: {agent} recovery deferred: {e.Message}");
        }
    }
}
