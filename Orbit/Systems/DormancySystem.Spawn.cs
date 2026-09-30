using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private readonly Dictionary<BotOwner, float> _spawnStarted = new();
    private readonly HashSet<BotOwner> _spawnActivated = new();
    private readonly HashSet<BotOwner> _spawnContactReported = new();
    private readonly HashSet<BotOwner> _spawnProximityReported = new();
    private readonly Dictionary<BotOwner, float> _spawnRefusalReportAt = new();
    private readonly Dictionary<BotOwner, float> _spawnSleepReportAt = new();

    internal void RegisterSpawn(BotOwner bot)
    {
        if (bot != null && !_spawnStarted.ContainsKey(bot)) _spawnStarted.Add(bot, Time.time);
    }

    internal void RegisterFirstActivation(BotOwner bot)
    {
        // PreActivate can run during loading, well before the brain gets its first tick.
        // State changes on Ghost wakes must never reopen this one-time window.
        if (bot == null || !_spawnActivated.Add(bot)) return;
        if (_spawnStarted.TryGetValue(bot, out var started) && float.IsNegativeInfinity(started)) return;
        _spawnStarted[bot] = Time.time;
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

    internal bool DeferSpawnContact(EnemyInfo enemy, bool firstSight = false, string source = "native")
    {
        if (!SpawnProtectionEnabled) return false;
        var bot = enemy?.Owner;
        var target = enemy?.Person?.AIData?.BotOwner;
        if (!FreshSpawn(bot) || target == null || DormantProfileIds.Contains(bot.ProfileId)
            || enemy.Person.AIData.IsAI != true) return false;
        // SetVisible(true) follows SetCanShoot in the same vision evaluation. At that
        // boundary CanShoot alone is not a previous contact. Never erase a sighting or shot.
        if (enemy.HaveSeenPersonal || enemy.IsVisible || !firstSight && enemy.CanShoot || enemy.PersonalShoot > 0)
            return ReportSpawnContactRefusal(enemy, "existing-contact", source);
        // Nothing is converted after combat starts. Let the first normal sleep poll run before
        // these new, distant groups acquire each other as physical targets.
        UpdateScopeState();
        if (!SpawnGroupCanWait(bot, out var reason)) return ReportSpawnContactRefusal(enemy, reason, source, "owner");
        if (!SpawnGroupCanWait(target, out reason) && !DormantSpawnTargetCanWait(target))
            return ReportSpawnContactRefusal(enemy, reason, source, "target");
        if (_spawnContactReported.Add(bot))
            Log.Info($"GHOST SPAWN: {bot.Profile.Nickname} deferred initial AI contact with {target.Profile.Nickname} for first sleep poll source={source}");
        return true;
    }

    private bool SpawnGroupCanWait(BotOwner bot)
        => SpawnGroupCanWait(bot, out _);

    private bool SpawnGroupCanWait(BotOwner bot, out string reason)
    {
        if (!SpawnBodyCanWait(bot, out reason)) return false;
        var group = bot.BotsGroup;
        for (var i = 0; group != null && i < group.MembersCount; i++)
        {
            var member = group.Member(i);
            if (member != null && !member.IsDead && !SpawnBodyCanWait(member, out reason)) return false;
        }
        return true;
    }

    private bool SpawnBodyCanWait(BotOwner bot, out string reason)
    {
        reason = "spawn-window";
        if (!FreshSpawn(bot) || DormantProfileIds.Contains(bot.ProfileId)) return false;
        reason = "distance-scope-or-policy";
        if (!SpawnBodyFarFromHumans(bot)) return false;
        reason = "under-fire";
        if (bot.Memory?.IsUnderFire == true) return false;
        reason = "goal-enemy";
        if (bot.Memory?.GoalEnemy != null) return false;
        reason = "physical-operation";
        if (bot.Medecine?.Using == true || bot.WeaponManager?.Grenades?.ThrowindNow == true
            || GhostBodyTransition.Busy(bot.GetPlayer)) return false;
        reason = "population-floor";
        if (_minAwakeBots > 0 && !IsDefaultDormant(bot)) return false;
        reason = "wounded";
        var health = bot.GetPlayer.HealthController.GetBodyPartHealth(EBodyPart.Common, true);
        if (health.Current < health.Maximum - 0.5f) return false;
        reason = null;
        return true;
    }

    private bool ReportSpawnContactRefusal(EnemyInfo enemy, string reason, string source, string participant = "contact")
    {
        var bot = enemy.Owner;
        if (_spawnRefusalReportAt.TryGetValue(bot, out var next) && Time.time < next) return false;
        _spawnRefusalReportAt[bot] = Time.time + 8f;
        Log.Info($"GHOST SPAWN CONTACT: {bot.Profile.Nickname} [{bot.ProfileId}] retained source={source} reason={reason} participant={participant}"
            + $" target={enemy.Person.ProfileId} seen={enemy.HaveSeenPersonal} visible={enemy.IsVisible} canShoot={enemy.CanShoot} shots={enemy.PersonalShoot}");
        return false;
    }

    private bool ReportInitialSleepCombatBlock(BotOwner bot, string reason, Player targeting = null)
    {
        // Only bots awaiting their very first Ghost admission, at most once per minute.
        if (!_spawnStarted.TryGetValue(bot, out var started) || float.IsNegativeInfinity(started)) return false;
        if (_spawnSleepReportAt.TryGetValue(bot, out var next) && Time.time < next) return false;
        _spawnSleepReportAt[bot] = Time.time + 60f;
        var enemy = bot.Memory?.GoalEnemy;
        Log.Info($"GHOST INITIAL SLEEP BLOCKED: {bot.Profile.Nickname} [{bot.ProfileId}] reason={reason}"
            + $" target={enemy?.Person?.ProfileId ?? "none"} targetName={enemy?.Person?.Profile?.Nickname ?? "none"}"
            + $" targeting={targeting?.ProfileId ?? "none"} human={Mathf.Sqrt(MinSqrDistanceToHumans(bot.Position)):F1}m"
            + $" firstActive={_spawnActivated.Contains(bot)} age={Time.time - started:F1}s seen={enemy?.HaveSeenPersonal}"
            + $" visible={enemy?.IsVisible} canShoot={enemy?.CanShoot} underFire={bot.Memory?.IsUnderFire}");
        return false;
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
