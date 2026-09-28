using System;
using Comfort.Common;
using Orbit.Navigation;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Only BotOwners on the simulation authority enter this service. Observed clients never teleport.
// Keep a bounded watch after ORBIT placement, plus short-lived rejected destinations per bot.
internal static class BotLandingGuard
{
    private sealed class Watch
    {
        internal BotOwner Bot;
        internal Vector3 Surface, Landing;
        internal string Source;
        internal float Started, StableSince = -1f, NextRetry;
        internal int Attempts;
        internal Action Repath;
    }

    private struct Rejected
    {
        internal BotOwner Bot;
        internal Vector3 Point;
        internal float Until;
    }

    private static readonly List<Watch> Watches = new();
    private static readonly List<Rejected> Rejections = new();
    private static readonly Dictionary<BotOwner, float> CorrectedUntil = new();
    private static readonly NavMeshPath LocalPath = new();
    private static float _nextTick;
    private static float _probeWindow;
    private static int _nearbyProbes;
    private static readonly List<Player> Humans = new();

    internal static void Clear()
    {
        Watches.Clear();
        Humans.Clear();
        Rejections.Clear();
        CorrectedUntil.Clear();
        _nextTick = 0f;
        _probeWindow = 0f;
        _nearbyProbes = 0;
    }

    internal static bool IsRejected(BotOwner bot, Vector3 point)
    {
        for (var i = Rejections.Count - 1; i >= 0; i--)
        {
            var rejected = Rejections[i];
            if (Time.time >= rejected.Until || rejected.Bot == null || rejected.Bot.IsDead)
            { Rejections.RemoveAt(i); continue; }
            var delta = point - rejected.Point;
            if (ReferenceEquals(bot, rejected.Bot) && delta.x * delta.x + delta.z * delta.z < 0.75f * 0.75f
                && Mathf.Abs(delta.y) < 1f) return true;
        }
        return false;
    }

    private static void Reject(BotOwner bot, Vector3 point)
    {
        if (IsRejected(bot, point)) return;
        if (Rejections.Count >= 256) Rejections.RemoveAt(0);
        Rejections.Add(new Rejected { Bot = bot, Point = point, Until = Time.time + 120f });
    }

    private static bool Corrected(BotOwner bot)
    {
        if (bot == null || !CorrectedUntil.TryGetValue(bot, out var until)) return false;
        if (Time.time < until) return true;
        CorrectedUntil.Remove(bot);
        return false;
    }

    internal static bool Accepts(BotOwner bot, Vector3 surface)
        => !IsRejected(bot, surface) && (!Corrected(bot)
            || BotGroundPlacement.TryResolve(bot.GetPlayer, surface, out _, out _));

    private static bool BelowFloor(BotOwner bot, Vector3 surface)
    {
        var player = bot?.GetPlayer;
        if (player == null || BotGroundPlacement.HasSupport(player)) return false;
        var delta = player.Position - surface;
        if (delta.y > -0.6f || delta.x * delta.x + delta.z * delta.z > 9f) return false;
        var above = new Vector3(player.Position.x, surface.y, player.Position.z);
        return BotGroundPlacement.TryResolve(player, above, out var expected, out _)
            && player.Position.y < expected.y - 0.6f;
    }

    private static void ConfirmFall(BotOwner bot)
    {
        // Bounded, short-lived state. Normal solo placements keep their original NavMesh offset.
        if (CorrectedUntil.Count >= 256 && !CorrectedUntil.ContainsKey(bot))
        {
            BotOwner oldest = null;
            var expires = float.MaxValue;
            foreach (var entry in CorrectedUntil)
                if (entry.Value < expires) { oldest = entry.Key; expires = entry.Value; }
            if (oldest != null) CorrectedUntil.Remove(oldest);
        }
        CorrectedUntil[bot] = Time.time + 120f;
    }

    internal static bool TryPlace(BotOwner bot, Vector3 surface, string source, Action repath = null, bool validateGround = false)
    {
        if (bot == null || bot.IsDead || bot.GetPlayer == null || IsRejected(bot, surface)) return false;
        var player = bot.GetPlayer;
        if (!BotGroundPlacement.Finite(surface)) return false;
        var corrected = Corrected(bot);
        var landing = surface + Vector3.up * (source == "wake" ? 0f : 0.25f);
        if ((corrected || validateGround) && !BotGroundPlacement.TryResolve(player, surface, out landing, out var reason))
        {
            Log.Debug($"GROUND PLACEMENT: {bot.Profile.Nickname} rejected source={source} surface={surface} reason={reason}");
            return false;
        }
        var from = player.Position;
        player.Teleport(landing);
        // Sleeping rescue placements are checked again at wake, when physics resumes.
        if (bot.gameObject.activeSelf)
        {
            Watches.RemoveAll(w => ReferenceEquals(w.Bot, bot));
            Watches.Add(new Watch { Bot = bot, Surface = surface, Landing = landing, Source = source,
                Started = Time.time, NextRetry = Time.time + 0.5f, Repath = repath });
        }
        Log.Debug($"GROUND PLACEMENT: {bot.Profile.Nickname} placed source={source} from={from} surface={surface} to={landing} controller={player.CharacterController.GetType().Name} mask={player.MovementContext.GroundMask} corrected={corrected}");
        return true;
    }

    internal static void Wake(BotOwner bot, Vector3 surface, Action repath = null)
    {
        if (bot == null || bot.IsDead || bot.GetPlayer == null) return;
        var from = bot.GetPlayer.Position;
        // A stale path corner must not turn a wake adjustment into a long-range teleport.
        if ((surface - from).sqrMagnitude > 4f || Mathf.Abs(surface.y - from.y) > 0.65f) surface = from;
        TryRecover(bot, surface, "wake", repath);
    }

    internal static bool TryRecover(BotOwner bot, Vector3 surface, string source, Action repath = null)
    {
        if (bot == null || bot.IsDead || bot.GetPlayer == null) return false;
        if (BelowFloor(bot, surface)) ConfirmFall(bot);
        if (TryPlace(bot, surface, source, repath)) return true;
        if (!Corrected(bot)) return false;
        RefreshHumans();
        var seed = new Watch { Bot = bot, Surface = surface, Landing = bot.GetPlayer.Position };
        if (TryNearby(seed, Humans, out var nearby, out _))
            return TryPlace(bot, nearby, source + "-nearby", repath);
        // No unverified fallback teleport. The normal mover keeps control when no safe point exists.
        return false;
    }

    private static void RefreshHumans()
    {
        Humans.Clear();
        var players = Singleton<GameWorld>.Instance?.AllAlivePlayersList;
        if (players == null) return;
        foreach (var player in players)
            if (player != null && player.AIData?.IsAI == false) Humans.Add(player);
    }

    internal static void Tick()
    {
        if (Watches.Count == 0 || Time.time < _nextTick) return;
        _nextTick = Time.time + 0.2f;
        RefreshHumans();
        for (var i = Watches.Count - 1; i >= 0; i--)
        {
            var watch = Watches[i];
            try
            {
                if (!Tick(watch, Humans)) Watches.RemoveAt(i);
            }
            catch (Exception e)
            {
                Watches.RemoveAt(i);
                Log.Warning($"GROUND PLACEMENT: watch cancelled reason={e.GetType().Name}");
            }
        }
    }

    private static bool Tick(Watch watch, List<Player> humans)
    {
        var bot = watch.Bot;
        if (bot == null || bot.IsDead || !bot.gameObject.activeSelf) return false;
        var player = bot.GetPlayer;
        if (player == null || GhostBodyTransition.Busy(player)
            || bot.Memory.IsUnderFire || bot.Memory.GoalEnemy != null) return false;
        var age = Time.time - watch.Started;
        var delta = player.Position - watch.Landing;
        // Normal navigation is free to leave the landing. Do not pull a walker back to an old point.
        if (delta.x * delta.x + delta.z * delta.z > 3f * 3f || delta.y > 1f) return false;
        if (BotGroundPlacement.HasSupport(player))
        {
            if (watch.StableSince < 0f) watch.StableSince = Time.time;
            if (Time.time - watch.StableSince >= 0.6f)
            {
                Log.Debug($"GROUND PLACEMENT: {bot.Profile.Nickname} stable source={watch.Source} at={player.Position}");
                return false;
            }
            return age < 8f;
        }
        watch.StableSince = -1f;
        if (age >= 8f)
        {
            // An unsupported landing is never certified just because the watch expired.
            if (Corrected(bot)) Reject(bot, watch.Surface);
            Log.Warning($"GROUND PLACEMENT: {bot.Profile.Nickname} unconfirmed source={watch.Source} at={player.Position}");
            return false;
        }
        if (Time.time < watch.NextRetry || delta.y > -0.6f) return true;
        // Distinguish a body below a nearby solid floor from a legitimate descent off a ledge.
        if (!BelowFloor(bot, watch.Surface)) return true;
        ConfirmFall(bot);
        Reject(bot, watch.Surface);
        Log.Warning($"GROUND PLACEMENT: {bot.Profile.Nickname} fell after source={watch.Source} landing={watch.Landing} now={player.Position} grounded={player.MovementContext.IsGrounded} controller={player.CharacterController.GetType().Name}");
        watch.NextRetry = Time.time + 1f;
        if (watch.Attempts >= 2) return false;
        if (!TryNearby(watch, humans, out var surface, out var landing)) return true;
        watch.Attempts++;
        player.Teleport(landing);
        watch.Surface = surface;
        watch.Landing = landing;
        watch.Repath?.Invoke();
        Log.Info($"GROUND PLACEMENT: {bot.Profile.Nickname} local recovery source={watch.Source} to={landing} attempt={watch.Attempts}");
        return true;
    }

    private static bool TryNearby(Watch watch, List<Player> humans, out Vector3 surface, out Vector3 landing)
    {
        surface = landing = default;
        for (var ring = 1; ring <= 2; ring++)
        for (var direction = 0; direction < 8; direction++)
        {
            // Mass spectator wakes share a query budget, instead of each bot doing a full
            // synchronous search in the same frame. Existing movement/recovery keeps retrying.
            if (Time.time >= _probeWindow)
            { _probeWindow = Time.time + 0.2f; _nearbyProbes = 0; }
            if (_nearbyProbes++ >= 32) return false;
            var angle = direction * Mathf.PI / 4f;
            var candidate = watch.Surface + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring * 1.25f);
            if (!NavMesh.SamplePosition(candidate, out var hit, 0.75f, NavMesh.AllAreas)
                || Mathf.Abs(hit.position.y - watch.Surface.y) > 0.65f
                || (hit.position - watch.Surface).sqrMagnitude > 3f * 3f
                || IsRejected(watch.Bot, hit.position)
                || DangerZones.IsInside(hit.position)
                || !NavMesh.CalculatePath(watch.Surface, hit.position, NavMesh.AllAreas, LocalPath)
                || LocalPath.status != NavMeshPathStatus.PathComplete
                || !BotGroundPlacement.TryResolve(watch.Bot.GetPlayer, hit.position, out landing, out _)
                || !ClearOfOtherBodies(watch.Bot, landing)
                || !Hidden(watch.Bot.GetPlayer.Position, landing, humans)) continue;
            // A short complete route can still go around a wall. Never teleport through that wall.
            var start = watch.Surface + Vector3.up;
            if (Physics.Linecast(start, landing + Vector3.up, watch.Bot.GetPlayer.MovementContext.GroundMask,
                    QueryTriggerInteraction.Ignore)) continue;
            surface = hit.position;
            return true;
        }
        return false;
    }

    private static bool ClearOfOtherBodies(BotOwner bot, Vector3 point)
    {
        var players = Singleton<GameWorld>.Instance?.AllAlivePlayersList;
        if (players == null) return false;
        foreach (var player in players)
            if (player != null && player != bot.GetPlayer && player.HealthController is { IsAlive: true }
                && (player.Position - point).sqrMagnitude < 1f) return false;
        return true;
    }

    private static bool Hidden(Vector3 from, Vector3 to, List<Player> humans)
    {
        foreach (var human in humans)
        {
            if (human?.HealthController is not { IsAlive: true }) continue;
            if ((human.Position - from).sqrMagnitude < 100f || (human.Position - to).sqrMagnitude < 100f) return false;
            var head = human.PlayerBones?.Head?.Original;
            if (head == null) return false;
            for (var h = 0.3f; h < 1.9f; h += 0.6f)
                if (!Physics.Linecast(head.position, from + Vector3.up * h, LayersMaskController.HighPolyWithTerrainMask)
                    || !Physics.Linecast(head.position, to + Vector3.up * h, LayersMaskController.HighPolyWithTerrainMask)) return false;
        }
        return true;
    }
}
