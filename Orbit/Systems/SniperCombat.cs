using System.Collections.Generic;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

internal static class SniperCombat
{
    private sealed class Lease { internal Agent Agent; internal Vector3 Post; internal float Until; }
    private static readonly Dictionary<BotOwner, Lease> Leases = new();
    internal static void Refresh(Agent agent, Vector3 post, float until)
    {
        if (!Leases.TryGetValue(agent.Bot, out var lease)) Leases[agent.Bot] = lease = new Lease();
        lease.Agent = agent; lease.Post = post; lease.Until = until;
    }
    internal static void Remove(BotOwner bot) { if (bot != null) Leases.Remove(bot); }
    internal static void Clear() => Leases.Clear();
    internal static bool CanHold(BotOwner bot)
    {
        if (!ServerConfig.Rush.Enabled || !ServerConfig.Rush.SniperCombatHold || bot == null || !Leases.TryGetValue(bot, out var lease)) return false;
        if (Time.time > lease.Until) { Leases.Remove(bot); return false; }
        var a = lease.Agent;
        if (bot.IsDead || a.IsDormant || a.SoloExtractRequested || a.Squad == null || a.Squad.ExtractRequested
            || a.Squad.Rush?.Main.Completed != false || !SniperPlan.Near(a.Position, lease.Post, 4)
            || bot.Memory.IsUnderFire || Time.time - a.LastHpDropTime < 5 || !bot.WeaponManager.HaveBullets
            || bot.WeaponManager.Reload.Reloading) return false;
        var close = ServerConfig.Rush.SniperDisengageDistance;
        var enemy = bot.Memory.GoalEnemy;
        if (enemy?.Person == null || !enemy.IsVisible || !enemy.CanShoot || (enemy.Person.Position - a.Position).sqrMagnitude < close * close) return false;
        // React to any locally known close threat, not just the current target.
        foreach (var known in bot.EnemiesController.EnemyInfos.Values)
            if (known.IsVisible && known.Person != null && (known.Person.Position - a.Position).sqrMagnitude < close * close) return false;
        return true;
    }
}
