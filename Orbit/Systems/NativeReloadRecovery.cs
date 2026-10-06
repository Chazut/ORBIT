using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

// A stale reload flag must not hold a whole native group awake forever. Recovery is
// restricted to sleep admission, after the normal distance/combat checks, on an idle body.
internal static class NativeReloadRecovery
{
    private const float MinimumAge = 45f, StableSeconds = 5f, PollInterval = .5f, MaximumPollGap = 2f;
    private sealed class Observation
    {
        public object Reload, Hands, Operation, Weapon, Magazine;
        public float ReloadStarted, StableSince, LastPoll, NextPoll, NextReport;
        public int Ammo, OperationState;
        public bool Safe;
    }
    private static readonly Dictionary<BotOwner, Observation> Observations = new();

    internal static void Clear() => Observations.Clear();
    internal static void Forget(BotOwner bot)
    {
        if (!ReferenceEquals(bot, null)) Observations.Remove(bot);
    }

    internal static void BeforeSleep(BotOwner bot)
    {
        var reload = bot?.WeaponManager?.Reload;
        if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || !bot.gameObject.activeSelf
            || bot.GetPlayer?.HealthController?.IsAlive != true || bot.Memory == null
            || bot.Memory.GoalEnemy != null || bot.Memory.IsUnderFire || reload?.Reloading != true)
        { Forget(bot); return; }
        if (!Observations.TryGetValue(bot, out var observation))
            Observations.Add(bot, observation = new Observation());
        var now = Time.time;
        if (now < observation.NextPoll) return;
        observation.NextPoll = now + PollInterval;
        try
        {
            var hands = bot.GetPlayer.HandsController as Player.FirearmController;
            var operation = hands?.CurrentOperation;
            var weapon = hands?.Item;
            var magazine = weapon?.GetCurrentMagazine();
            // Read physical ammunition, not BotReload.BulletCount's internal-reload placeholder.
            var ammo = weapon == null ? -1 : weapon.ChamberAmmoCount + (magazine?.Count ?? 0);
            var age = now - reload._reloadStartTime;
            var inReload = hands?.IsInReloadOperation();
            var inventory = bot.GetPlayer.InventoryController;
            var pendingInventory = inventory == null;
            if (inventory != null)
                foreach (var itemEvent in inventory.SelectEvents<ItemEventArgs>())
                { pendingInventory = true; break; }
            var reason = hands == null || hands.Destroyed || operation == null || weapon == null ? "hands-unavailable"
                : !ReferenceEquals(weapon, reload._weapon) || !ReferenceEquals(weapon, bot.WeaponManager.CurrentWeapon)
                    || !ReferenceEquals(hands, bot.WeaponManager.ShootController) ? "weapon-changed"
                : inReload == true ? "active-reload"
                : operation is not Player.FirearmController.Idling || !operation.CanStartReload()
                    || hands.IsInLauncherMode() ? "hands-operation"
                : hands.IsHandsProcessing() || hands.IsInInteractionStrictCheck() ? "hands-animation"
                : pendingInventory ? "inventory-operation"
                : bot.WeaponManager.Selector?.IsWeaponReady != true || bot.WeaponManager.Grenades?.ThrowindNow == true
                    || bot.Medecine?.Using == true || bot.DoorOpener?.Interacting == true ? "body-busy"
                : float.IsNaN(age) || float.IsInfinity(age) || age < MinimumAge ? "recent-reload"
                : null;
            var state = operation == null ? -1 : (int)operation.State;
            var changed = !ReferenceEquals(observation.Reload, reload) || !ReferenceEquals(observation.Hands, hands)
                || !ReferenceEquals(observation.Operation, operation) || !ReferenceEquals(observation.Weapon, weapon)
                || !ReferenceEquals(observation.Magazine, magazine) || observation.Ammo != ammo
                || observation.OperationState != state || observation.ReloadStarted != reload._reloadStartTime;
            if (changed || reason != null || !observation.Safe || now - observation.LastPoll > MaximumPollGap)
                observation.StableSince = now;
            observation.Reload = reload; observation.Hands = hands; observation.Operation = operation;
            observation.Weapon = weapon; observation.Magazine = magazine; observation.Ammo = ammo;
            observation.OperationState = state; observation.ReloadStarted = reload._reloadStartTime;
            observation.LastPoll = now; observation.Safe = reason == null;

            var stable = now - observation.StableSince;
            var recovered = false;
            if (reason == null && stable >= StableSeconds)
            {
                // The native watchdog can be disabled by its previous-failure flag. Only
                // clear that leftover reload state once actual hands/inventory are idle.
                reload.CheckReloadLongTime();
                if (reload.Reloading) { reload.Reloading = false; reason = "idle-flag-reset"; }
                else reason = "native-timeout";
                reload._nextReloadTime = Mathf.Max(reload._nextReloadTime, now + .5f);
                recovered = true;
            }
            if (recovered || now >= observation.NextReport)
            {
                observation.NextReport = now + 30f;
                Report(bot, $"state={(recovered ? "recovered" : "waiting")} reason={reason ?? "confirming-idle"}"
                    + $" reloadAge={age:F1}s idleObservedFor={stable:F1}s reloadType={reload._reloadType}"
                    + $" failFlag={reload._reloadFailDebug} reloading={reload.Reloading} actualReload={inReload?.ToString() ?? "unknown"}"
                    + $" hands={bot.GetPlayer.HandsController?.GetType().FullName ?? "none"}"
                    + $" operation={operation?.GetType().FullName ?? "none"} operationState={state}"
                    + $" ammo={ammo} inventoryPending={pendingInventory} group={bot.BotsGroup?.MembersCount ?? 1}");
            }
            if (recovered) Forget(bot);
        }
        catch (Exception error)
        {
            observation.Safe = false;
            if (now < observation.NextReport) return;
            observation.NextReport = now + 30f;
            Report(bot, $"state=unavailable reason={error.GetType().Name}");
        }
    }

    private static void Report(BotOwner bot, string detail)
    {
        Log.Info($"NATIVE RELOAD: {bot.Profile?.Nickname} [{bot.ProfileId}] {detail}");
        PerformanceJournal.Event("native-reload", bot.ProfileId, detail);
    }
}
