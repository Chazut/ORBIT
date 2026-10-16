using System.Collections.Generic;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal static class SniperCombat
{
    private const float AlertRadius = 12f;
    private sealed class Lease
    {
        internal Agent Agent;
        internal Vector3 Post;
        internal float Until, DecisionUntil, NextRouteLog;
        internal bool Alert;
        internal string Decision;
        internal object SafetyContext;
        internal readonly Vector3[] Corners = new Vector3[128];
    }
    private static readonly Dictionary<BotOwner, Lease> Leases = new();
    internal static System.Func<object, bool> AlertDanger;
    internal static void Refresh(Agent agent, Vector3 post, float until)
    {
        if (!Leases.TryGetValue(agent.Bot, out var lease)) Leases[agent.Bot] = lease = new Lease();
        lease.Agent = agent; lease.Post = post; lease.Until = until;
    }
    internal static void Remove(BotOwner bot) { if (bot != null) Leases.Remove(bot); }
    internal static void Clear() => Leases.Clear();
    internal static bool HasLease(BotOwner bot) => bot != null && Leases.ContainsKey(bot);

    // Only an already occupied post can constrain an ordinary alert. Combat decisions,
    // perception and steering remain native; this lease limits movement destinations.
    internal static bool ObserveAlert(BotOwner bot, string solo, bool independent, bool visible,
        Vector3? lastKnown, bool danger, object safetyContext)
    {
        if (bot == null || !Leases.TryGetValue(bot, out var lease)) return false;
        lease.Decision = solo;
        lease.SafetyContext = safetyContext;
        lease.DecisionUntil = Time.time + .5f;
        var reason = SafetyReason(bot, lease);
        if (reason == null && !independent) reason = "self-care-or-squad";
        if (reason == null && danger) reason = "sain-danger";
        if (reason == null && solo is not ("SeekCover" or "ShiftCover" or "Search" or "RushEnemy"
            or "StandAndShoot" or "ShootDistantEnemy")) reason = "priority-decision";
        if (reason == null && visible) reason = "visible-enemy";
        if (reason == null && !lastKnown.HasValue) reason = "no-known-contact";
        if (reason == null && !Inside(lease, lease.Agent.Position, lease.Alert ? AlertRadius : 4f)) reason = "outside-post";
        if (reason == null && Mathf.Abs(lastKnown.Value.y - lease.Agent.Position.y) < 2.5f
            && (lastKnown.Value - lease.Agent.Position).sqrMagnitude < ServerConfig.Rush.SniperDisengageDistance * ServerConfig.Rush.SniperDisengageDistance)
            reason = "nearby-known-threat";
        if (reason != null) { ReleaseAlert(lease, reason); return false; }
        if (lease.Alert) return false;
        lease.Alert = true;
        Log.Info($"SNIPER ALERT: {lease.Agent} state=holding reason=unseen-contact decision={solo} post={lease.Post} known={lastKnown.Value}");
        return true; // Cancel a pre-alert route once, before the next native decision update.
    }

    private static string SafetyReason(BotOwner bot, Lease lease)
    {
        var a = lease.Agent;
        if (!ServerConfig.Rush.Enabled || !ServerConfig.Rush.SniperCombatHold || Time.time > lease.Until
            || bot.IsDead || a.IsDormant || a.SoloExtractRequested || a.Squad == null || a.Squad.ExtractRequested
            || a.Squad.Rush?.Main.Completed != false) return "inactive-post";
        if (bot.Memory.IsUnderFire) return "incoming-fire";
        if (Time.time - a.LastHpDropTime < 5f) return "recent-wound";
        if (!bot.WeaponManager.HaveBullets || bot.WeaponManager.Reload.Reloading) return "weapon-not-ready";
        var close = ServerConfig.Rush.SniperDisengageDistance;
        foreach (var known in bot.EnemiesController.EnemyInfos.Values)
            if (known.IsVisible && known.Person != null && (known.Person.Position - a.Position).sqrMagnitude < close * close)
                return "nearby-visible-threat";
        return null;
    }

    private static bool TryAlert(BotOwner bot, out Lease lease)
    {
        lease = null;
        if (bot == null || !Leases.TryGetValue(bot, out lease) || !lease.Alert) return false;
        var reason = SafetyReason(bot, lease);
        if (reason == null && Time.time > lease.DecisionUntil) reason = "stale-decision";
        if (reason == null && (AlertDanger == null || AlertDanger(lease.SafetyContext))) reason = "sain-danger";
        if (reason == null && !Inside(lease, lease.Agent.Position, AlertRadius)) reason = "outside-post";
        if (reason == null) return true;
        ReleaseAlert(lease, reason);
        return false;
    }

    internal static bool IsAlertHeld(BotOwner bot) => TryAlert(bot, out _);
    internal static void CancelAlert(BotOwner bot, string reason)
    { if (bot != null && Leases.TryGetValue(bot, out var lease)) ReleaseAlert(lease, reason); }
    private static void ReleaseAlert(Lease lease, string reason)
    {
        if (!lease.Alert) return;
        lease.Alert = false;
        Log.Info($"SNIPER ALERT: {lease.Agent} state=released reason={reason} decision={lease.Decision} post={lease.Post}");
    }
    private static bool Inside(Lease lease, Vector3 point, float radius)
        => Mathf.Abs(point.y - lease.Post.y) < 1.5f && (point - lease.Post).sqrMagnitude <= radius * radius;

    internal static bool AllowAlertPoint(BotOwner bot, Vector3 point)
    {
        if (!TryAlert(bot, out var lease) || Inside(lease, point, AlertRadius)) return true;
        RouteLog(lease, "blocked-route", "outside-post", point);
        return false;
    }
    internal static bool AllowAlertPath(BotOwner bot, NavMeshPath path)
    {
        if (!TryAlert(bot, out var lease)) return true;
        if (path == null || path.status != NavMeshPathStatus.PathComplete)
        { RouteLog(lease, "blocked-route", "incomplete-path", lease.Post); return false; }
        var count = path.GetCornersNonAlloc(lease.Corners);
        if (count < 2 || count >= lease.Corners.Length)
        { RouteLog(lease, "blocked-route", "invalid-corner-count", lease.Post); return false; }
        return AllowCorners(lease, lease.Corners, count, true);
    }
    internal static bool AllowAlertCachedPath(BotOwner bot, IReadOnlyList<Vector3> points)
    {
        if (!TryAlert(bot, out var lease)) return true;
        return points != null && points.Count >= 2 && points.Count < lease.Corners.Length
            && AllowCorners(lease, points, points.Count, false);
    }
    private static bool AllowCorners(Lease lease, IReadOnlyList<Vector3> points, int count, bool report)
    {
        for (var i = 0; i < count; i++)
            if (!Inside(lease, points[i], AlertRadius))
            { if (report) RouteLog(lease, "blocked-route", "leaves-post-or-floor", points[i]); return false; }
        if (report) RouteLog(lease, "local-move", "connected-roof-path", points[count - 1]);
        return true;
    }
    private static void RouteLog(Lease lease, string state, string reason, Vector3 target)
    {
        if (Time.time < lease.NextRouteLog) return;
        lease.NextRouteLog = Time.time + 3f;
        Log.Info($"SNIPER ALERT: {lease.Agent} state={state} reason={reason} decision={lease.Decision} target={target} post={lease.Post}");
    }
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
