using EFT;
using EFT.InventoryLogic;
using Orbit.Entities;
using Orbit.Looting;
using UnityEngine;

namespace Orbit.Systems;

// Only repairs equipment changed by our own looting. Never takes control of arbitrary melee AI.
internal static class LootHandsRecovery
{
    internal static void Queue(Agent agent)
    {
        if (agent == null || agent.IsDormant) return;
        if (!agent.LootHandsPending)
        {
            agent.LootHandsAttempts = 0;
            agent.LootHandsRetryAt = Time.time;
            Log.Debug($"LOOT HANDS: {agent} reconciliation queued");
        }
        agent.LootHandsPending = true;
    }

    internal static void Tick(Agent agent)
    {
        if (!agent.LootHandsPending || agent.IsDormant || Time.time < agent.LootHandsRetryAt) return;
        agent.LootHandsRetryAt = Time.time + .5f;
        var bot = agent.Bot;
        var player = agent.Player;
        if (!LootActorGuard.IsCurrent(bot, player) || bot.BotState != EBotState.Active
            || !bot.gameObject.activeInHierarchy) return;
        try
        {
            // Cancellation only releases the waiter, not EFT/Fika's pending transaction.
            if (agent.LootHandler?.LootTaskRunning == true || agent.LootHandler?.CanEnterGhost == false
                || GhostBodyTransition.Busy(player) || bot.Medecine?.Using == true
                || bot.WeaponManager?.Reload?.Reloading == true || bot.WeaponManager?.Grenades?.ThrowindNow == true)
                return;
            foreach (var operation in player.InventoryController.SelectEvents<ItemEventArgs>()) return;
            var selector = bot.WeaponManager?.Selector;
            if (selector == null || !selector.IsWeaponReady || selector.IsChanging) return;
            var hands = player.HandsController;
            // Respect medical/grenade/unknown controllers, firing, reloads and underbarrel actions.
            if (hands is Player.FirearmController firearm)
            {
                if (bot.ShootData?.Shooting == true || firearm.IsInReloadOperation() || firearm.Item.IsUnderBarrelDeviceActive) return;
                foreach (var slot in Slots)
                    if (ReferenceEquals(player.Inventory.Equipment.GetSlot(slot)?.ContainedItem, firearm.Item))
                    {
                        // A combat-selected pistol/secondary is just as valid as the primary.
                        selector.UpdateWeaponsList();
                        agent.LootHandsPending = false;
                        Log.Debug($"LOOT HANDS: {agent} owned firearm confirmed attempts={agent.LootHandsAttempts}");
                        return;
                    }
            }
            else if (hands is not Player.KnifeController && hands is not Player.EmptyHandsController) return;
            if (agent.LootHandsAttempts >= 3)
            {
                agent.LootHandsPending = false;
                Log.Warning($"LOOT HANDS: {agent} recovery exhausted attempts={agent.LootHandsAttempts}");
                return;
            }
            selector.UpdateWeaponsList();
            var target = selector._mainWeapon;
            if (player.Inventory.Equipment.GetSlot(target)?.ContainedItem is not Weapon)
                foreach (var slot in Slots)
                    if (player.Inventory.Equipment.GetSlot(slot)?.ContainedItem is Weapon) { target = slot; break; }
            if (player.Inventory.Equipment.GetSlot(target)?.ContainedItem is not Weapon)
            {
                agent.LootHandsPending = false;
                Log.Debug($"LOOT HANDS: {agent} no owned firearm to restore");
                return;
            }
            agent.LootHandsAttempts++;
            agent.LootHandsRetryAt = Time.time + 5f;
            bool accepted = selector.TryChangeToSlot(target, true);
            Log.Debug($"LOOT HANDS: {agent} redraw requested slot={target} accepted={accepted} attempt={agent.LootHandsAttempts}");
            // Confirmation on a later tick, never from a request acknowledgement.
        }
        catch (System.Exception e)
        {
            agent.LootHandsRetryAt = Time.time + 5f;
            if (++agent.LootHandsAttempts >= 3) agent.LootHandsPending = false;
            Log.Warning($"LOOT HANDS: {agent} recovery deferred: {e.Message}");
        }
    }

    private static readonly EquipmentSlot[] Slots =
        { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };
}
