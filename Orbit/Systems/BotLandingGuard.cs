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
        internal bool FallConfirmed;
        internal string DeferredReason;
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
    private static readonly Dictionary<BotOwner, float> WakeRetryAt = new();
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
        WakeRetryAt.Clear();
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
        return BotGroundPlacement.IsBelowFloor(player, surface);
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
        Place(bot, surface, landing, source, repath, corrected);
        return true;
    }

    // Certify a physical landing while the body is still inactive. An off-mesh Ghost can be
    // below a slope even when its path corners are valid. Do not resume gravity in that state.
    internal static bool PrepareWake(BotOwner bot)
    {
        var player = bot?.GetPlayer;
        if (player == null || bot.IsDead) return true;
        if (WakeRetryAt.TryGetValue(bot, out var retry) && Time.time < retry) return false;
        var origin = player.Position;
        if (!BotGroundPlacement.Finite(origin)) return false;
        if (IsRejected(bot, origin))
        {
            RefreshHumans();
            var rejected = new Watch { Bot = bot, Surface = origin, Landing = origin };
            if (TryNearby(rejected, Humans, out _, out var nearby, out _))
            { player.Teleport(nearby); WakeRetryAt.Remove(bot); return true; }
            return false;
        }
        if (BotGroundPlacement.TryResolve(player, origin, out var landing, out var reason))
        {
            if ((landing - origin).sqrMagnitude > .0001f) player.Teleport(landing);
            WakeRetryAt.Remove(bot);
            return true;
        }
        // Inspect nearby height bands in nearest-first order, never distant path corners.
        // The horizontal footprint stays within 2m; each candidate needs real capsule support.
        for (var i = 0; i < 17; i++)
        {
            if (!TakeProbe()) { reason = "query-budget"; break; }
            var height = i == 0 ? 0 : (i + 1) / 2 * (i % 2 == 1 ? 1 : -1);
            var sample = origin + Vector3.up * height;
            if (!NavMesh.SamplePosition(sample, out var hit, .75f, NavMesh.AllAreas)) continue;
            var delta = hit.position - origin;
            if (delta.x * delta.x + delta.z * delta.z > 4f || Mathf.Abs(delta.y) > 8f
                || DangerZones.IsInside(hit.position) || IsRejected(bot, hit.position)) continue;
            if (!BotGroundPlacement.TryResolve(player, hit.position, out landing, out reason)) continue;
            if (!ClearOfOtherBodies(bot, landing)) { reason = "another-body"; continue; }
            player.Teleport(landing);
            WakeRetryAt.Remove(bot);
            Log.Info($"GHOST WAKE GROUND: {bot.Profile.Nickname} supported before activation from={origin} to={landing}");
            return true;
        }
        // A failed physical query must not turn into an unchecked placement. Keep the sleeper
        // eligible for the next wake attempt; urgent requests still bypass the ordinary wake queue.
        if (!WakeRetryAt.ContainsKey(bot))
            Log.Warning($"GHOST WAKE GROUND: {bot.Profile.Nickname} activation deferred at={origin} reason={reason}");
        if (WakeRetryAt.Count >= 256) WakeRetryAt.Clear();
        WakeRetryAt[bot] = Time.time + .25f;
        return false;
    }

    private static void Place(BotOwner bot, Vector3 surface, Vector3 landing, string source, Action repath, bool corrected)
    {
        var player = bot.GetPlayer;
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
    }

    internal static void Wake(BotOwner bot, Vector3 surface, Action repath = null)
    {
        if (bot == null || bot.IsDead || bot.GetPlayer == null) return;
        var from = bot.GetPlayer.Position;
        if (!BotGroundPlacement.Finite(surface)) surface = from;
        // A stale path corner must not turn a wake adjustment into a long-range teleport.
        if ((surface - from).sqrMagnitude > 4f) surface = from;
        if (Mathf.Abs(surface.y - from.y) > 0.65f)
        {
            if (BotGroundPlacement.HasSupport(bot.GetPlayer))
            {
                TryRecover(bot, from, "wake", repath);
                return;
            }
            RefreshHumans();
            if (TryWakeLanding(bot, from, surface, out var resolvedSurface, out var landing, out var reason))
            {
                Place(bot, resolvedSurface, landing, "wake-validated", repath, Corrected(bot));
                return;
            }
            // Retain the nearby mesh height for fall detection. Replacing it with the unsupported
            // Ghost height would make the short floor probe miss the very floor we need to detect.
            Watches.RemoveAll(w => ReferenceEquals(w.Bot, bot));
            Watches.Add(new Watch { Bot = bot, Surface = surface, Landing = from, Source = "wake-pending",
                Started = Time.time, NextRetry = Time.time + 0.5f, Repath = repath });
            Log.Warning($"GROUND PLACEMENT: {bot.Profile.Nickname} wake validation deferred from={from} surface={surface} reason={reason}");
            return;
        }
        if (!TryPlace(bot, surface, "wake", repath, validateGround: true))
        {
            if (IsRejected(bot, surface) && TryRecover(bot, surface, "wake", repath)) return;
            // Activation may have changed the controller. Recheck the actual body instead of
            // trusting a zero-height adjustment as proof of support.
            if (PrepareWake(bot)) TryPlace(bot, bot.GetPlayer.Position, "wake", repath, validateGround: true);
        }
    }

    private static bool TakeProbe()
    {
        if (Time.time >= _probeWindow) { _probeWindow = Time.time + 0.2f; _nearbyProbes = 0; }
        return _nearbyProbes++ < 32;
    }

    private static bool TryWakeLanding(BotOwner bot, Vector3 origin, Vector3 mesh,
        out Vector3 surface, out Vector3 landing, out string reason)
    {
        surface = landing = default;
        reason = "no-local-landing";
        for (var probe = 0; probe < 9; probe++)
        {
            if (!TakeProbe()) { reason = "query-budget"; return false; }
            var angle = (probe - 1) * Mathf.PI / 4f;
            var candidate = probe == 0 ? mesh : mesh + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 1.25f;
            if (!NavMesh.SamplePosition(candidate, out var hit, 0.3f, NavMesh.AllAreas)
                || (hit.position - origin).sqrMagnitude > 4f || Mathf.Abs(hit.position.y - mesh.y) > 0.65f
                || IsRejected(bot, hit.position) || DangerZones.IsInside(hit.position)) continue;
            if (probe != 0 && (!NavMesh.CalculatePath(mesh, hit.position, NavMesh.AllAreas, LocalPath)
                || LocalPath.status != NavMeshPathStatus.PathComplete)) continue;
            if (!BotGroundPlacement.TryResolve(bot.GetPlayer, hit.position, out landing, out reason)) continue;
            if ((landing - origin).sqrMagnitude > 4f) { reason = "wake-distance-limit"; continue; }
            if (!ClearOfOtherBodies(bot, landing)) { reason = "another-body"; continue; }
            if (!Hidden(origin, landing, Humans)) { reason = "human-visible"; continue; }
            // Check the short horizontal approach at the verified floor height. A body already
            // below that floor must be allowed to cross back upward through its support surface.
            var start = new Vector3(origin.x, hit.position.y + 1f, origin.z);
            if (Physics.Linecast(start, landing + Vector3.up, bot.GetPlayer.MovementContext.GroundMask,
                    QueryTriggerInteraction.Ignore)) { reason = "blocked-segment"; continue; }
            surface = hit.position;
            return true;
        }
        return false;
    }

    internal static bool TryRecover(BotOwner bot, Vector3 surface, string source, Action repath = null)
    {
        if (bot == null || bot.IsDead || bot.GetPlayer == null) return false;
        if (BelowFloor(bot, surface)) ConfirmFall(bot);
        if (TryPlace(bot, surface, source, repath)) return true;
        if (!Corrected(bot)) return false;
        RefreshHumans();
        var seed = new Watch { Bot = bot, Surface = surface, Landing = surface };
        if (TryNearby(seed, Humans, out var nearby, out _, out _))
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

    internal static bool TryHandoffLanding(BotOwner bot, out Vector3 landing)
    {
        landing = default;
        var player = bot?.GetPlayer;
        if (player == null || bot.IsDead || !bot.gameObject.activeSelf
            || GhostBodyTransition.Busy(player) || !BotGroundPlacement.Finite(player.Position)) return false;
        RefreshHumans();
        var origin = player.Position;
        // Native handoff can start off-mesh, so no complete path from that invalid start is required.
        // Keep the floor, physical clearance and direct segment checks, with a strict 3m limit.
        for (var probe = 0; probe < 17; probe++)
        {
            if (!TakeProbe()) return false;
            var angle = (probe - 1) % 8 * Mathf.PI / 4f;
            var candidate = probe == 0 ? origin
                : origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (probe <= 8 ? 1.25f : 2.5f);
            if (!NavMesh.SamplePosition(candidate, out var hit, 0.75f, NavMesh.AllAreas)
                || (hit.position - origin).sqrMagnitude > 9f || Mathf.Abs(hit.position.y - origin.y) > 0.65f
                || IsRejected(bot, hit.position) || DangerZones.IsInside(hit.position)
                || !BotGroundPlacement.TryResolve(player, hit.position, out var target, out _)
                || (target - origin).sqrMagnitude > 9f || !ClearOfOtherBodies(bot, target)
                || !Hidden(origin, target, Humans)
                || Physics.Linecast(origin + Vector3.up, target + Vector3.up, player.MovementContext.GroundMask,
                    QueryTriggerInteraction.Ignore)) continue;
            landing = target;
            return true;
        }
        return false;
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
        if (player == null) return false;
        var age = Time.time - watch.Started;
        if (GhostBodyTransition.Busy(player)) return age < 8f;
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
        if (!watch.FallConfirmed && !BelowFloor(bot, watch.Surface)) return true;
        if (!watch.FallConfirmed)
        {
            watch.FallConfirmed = true;
            ConfirmFall(bot);
            Reject(bot, watch.Landing);
            Log.Warning($"GROUND PLACEMENT: {bot.Profile.Nickname} fell after source={watch.Source} landing={watch.Landing} now={player.Position} grounded={player.MovementContext.IsGrounded} controller={player.CharacterController.GetType().Name}");
        }
        watch.NextRetry = Time.time + 1f;
        if (watch.Attempts >= 2) return false;
        if (!TryNearby(watch, humans, out var surface, out var landing, out var reason))
        {
            if (watch.DeferredReason != reason)
                Log.Warning($"GROUND PLACEMENT: {bot.Profile.Nickname} recovery deferred source={watch.Source} reason={reason}");
            watch.DeferredReason = reason;
            return true;
        }
        watch.Attempts++;
        player.Teleport(landing);
        // Combat's native mover must not pull the rescued body back to an old below-floor anchor.
        if (bot.Mover != null) OrbitMovementRecovery.InvalidateNativePosition(bot.Mover, landing);
        watch.Surface = surface;
        watch.Landing = landing;
        watch.FallConfirmed = false;
        watch.DeferredReason = null;
        // The rescue restores the body, but combat keeps its own movement order.
        if (!bot.Memory.IsUnderFire && bot.Memory.GoalEnemy == null) watch.Repath?.Invoke();
        Log.Info($"GROUND PLACEMENT: {bot.Profile.Nickname} local recovery source={watch.Source} to={landing} attempt={watch.Attempts}");
        return true;
    }

    private static bool TryNearby(Watch watch, List<Player> humans, out Vector3 surface, out Vector3 landing, out string reason)
    {
        surface = landing = default;
        reason = "no-local-navmesh";
        for (var ring = 1; ring <= 2; ring++)
        for (var direction = 0; direction < 8; direction++)
        {
            // Mass spectator wakes share a query budget, instead of each bot doing a full
            // synchronous search in the same frame. Existing movement/recovery keeps retrying.
            if (!TakeProbe()) { reason = "query-budget"; return false; }
            var angle = direction * Mathf.PI / 4f;
            var candidate = watch.Surface + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring * 1.25f);
            if (!NavMesh.SamplePosition(candidate, out var hit, 0.75f, NavMesh.AllAreas)
                || Mathf.Abs(hit.position.y - watch.Surface.y) > 0.65f
                || (hit.position - watch.Surface).sqrMagnitude > 3f * 3f
                || IsRejected(watch.Bot, hit.position)
                || DangerZones.IsInside(hit.position)) continue;
            if (!NavMesh.CalculatePath(watch.Surface, hit.position, NavMesh.AllAreas, LocalPath)
                || LocalPath.status != NavMeshPathStatus.PathComplete) { reason = "disconnected-navmesh"; continue; }
            if (!BotGroundPlacement.TryResolve(watch.Bot.GetPlayer, hit.position, out landing, out reason)) continue;
            if ((landing - watch.Landing).sqrMagnitude > 9f) { reason = "local-distance-limit"; continue; }
            if (!ClearOfOtherBodies(watch.Bot, landing)) { reason = "another-body"; continue; }
            if (!Hidden(watch.Bot.GetPlayer.Position, landing, humans)) { reason = "human-visible"; continue; }
            // A short complete route can still go around a wall. Never teleport through that wall.
            var start = watch.Surface + Vector3.up;
            if (Physics.Linecast(start, landing + Vector3.up, watch.Bot.GetPlayer.MovementContext.GroundMask,
                    QueryTriggerInteraction.Ignore)) { reason = "blocked-segment"; continue; }
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
