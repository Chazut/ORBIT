using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private readonly Dictionary<BotOwner, float> _spawnStarted = new();
    private readonly HashSet<BotOwner> _spawnContactReported = new();
    private readonly HashSet<BotOwner> _spawnProximityReported = new();

    internal void RegisterSpawn(BotOwner bot)
    {
        if (bot != null && !_spawnStarted.ContainsKey(bot)) _spawnStarted.Add(bot, Time.time);
    }

    private bool FreshSpawn(BotOwner bot)
        => bot != null && _spawnStarted.TryGetValue(bot, out var started) && Time.time - started < 8f;

    private void FinishSpawnProtection(BotOwner bot)
    {
        // Retain the registration so neither a wake nor another PreActivate can rearm it.
        if (bot != null && _spawnStarted.ContainsKey(bot)) _spawnStarted[bot] = float.NegativeInfinity;
    }

    private bool SpawnProtectionEnabled => _enabled && GhostMovementEnabled && _cfg.NativeGhostMovement
        && !_spectatorSuspended && _fightsMode == GhostFightsMode.Simulated
        && !string.Equals(_cfg.GhostAwakeBehavior, "wake_ghost", StringComparison.OrdinalIgnoreCase);

    private bool DeferSpawnProximity(BotOwner bot)
    {
        if (!SpawnProtectionEnabled || !FreshSpawn(bot) || !SpawnGroupCanWait(bot)) return false;
        // Some squadmates can be Active while another is still PreActive. The whole group
        // must get its first sleep opportunity before those ready members wake old Ghosts.
        if (_spawnProximityReported.Add(bot))
            Log.Info($"GHOST SPAWN: {bot.Profile.Nickname} deferred proximity wake during group initialization");
        return true;
    }

    internal bool DeferSpawnContact(EnemyInfo enemy)
    {
        if (!SpawnProtectionEnabled) return false;
        var bot = enemy?.Owner;
        var target = enemy?.Person?.AIData?.BotOwner;
        if (!FreshSpawn(bot) || target == null || DormantProfileIds.Contains(bot.ProfileId)
            || enemy.Person.AIData.IsAI != true
            || enemy.HaveSeenPersonal || enemy.IsVisible || enemy.CanShoot || enemy.PersonalShoot > 0) return false;
        // Nothing is converted after combat starts. Let the first normal sleep poll run before
        // these new, distant groups acquire each other as physical targets.
        UpdateScopeState();
        if (!SpawnGroupCanWait(bot) || !(SpawnGroupCanWait(target) || DormantSpawnTargetCanWait(target))) return false;
        if (_spawnContactReported.Add(bot))
            Log.Info($"GHOST SPAWN: {bot.Profile.Nickname} deferred initial AI contact with {target.Profile.Nickname} for first sleep poll");
        return true;
    }

    private bool SpawnGroupCanWait(BotOwner bot)
    {
        if (!SpawnBodyCanWait(bot)) return false;
        var group = bot.BotsGroup;
        for (var i = 0; group != null && i < group.MembersCount; i++)
        {
            var member = group.Member(i);
            if (member != null && !member.IsDead && !SpawnBodyCanWait(member)) return false;
        }
        return true;
    }

    private bool SpawnBodyCanWait(BotOwner bot)
    {
        if (!FreshSpawn(bot) || DormantProfileIds.Contains(bot.ProfileId) || !SpawnBodyFarFromHumans(bot)
            || bot.Memory?.IsUnderFire == true || bot.Memory?.GoalEnemy != null
            || bot.Medecine?.Using == true || bot.WeaponManager?.Grenades?.ThrowindNow == true) return false;
        if (_minAwakeBots > 0 && !IsDefaultDormant(bot)) return false;
        var health = bot.GetPlayer.HealthController.GetBodyPartHealth(EBodyPart.Common, true);
        if (health.Current < health.Maximum - 0.5f) return false;
        return true;
    }

    private bool DormantSpawnTargetCanWait(BotOwner bot)
    {
        bool Safe(BotOwner member) => DormantProfileIds.Contains(member.ProfileId)
            && member.Memory?.IsUnderFire != true && SpawnBodyFarFromHumans(member);
        if (!Safe(bot)) return false;
        var group = bot.BotsGroup;
        for (var i = 0; group != null && i < group.MembersCount; i++)
        {
            var member = group.Member(i);
            if (member != null && !member.IsDead && !Safe(member)) return false;
        }
        return true;
    }

    private bool SpawnBodyFarFromHumans(BotOwner bot)
    {
        if (bot == null || bot.IsDead || !IsNativeGhostEligible(bot)
            || bot.Profile.Info.Settings.Role == WildSpawnType.shooterBTR
            || bot.GetPlayer?.HealthController is not { IsAlive: true }
            || InScopedView(bot.Position, out _)) return false;
        var gate = IsDefaultDormant(bot) ? _scavSleepDistanceSqr : _sleepDistanceSqr;
        var hasHuman = false;
        foreach (var player in _gameWorld.AllAlivePlayersList)
        {
            if (player?.AIData?.IsAI != false || player.HealthController is not { IsAlive: true }) continue;
            hasHuman = true;
            if ((player.Position - bot.Position).sqrMagnitude <= gate) return false;
        }
        return hasHuman;
    }
}
