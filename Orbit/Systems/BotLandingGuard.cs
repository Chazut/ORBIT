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
    private sealed class WakeSearch
    {
        internal Vector3 Origin;
        internal int Candidate;
        internal float RetryAt, LogAt;
    }
    private static readonly Dictionary<BotOwner, WakeSearch> WakeSearches = new();
    private const float WakeHeightLimit = 0.65f;
    private const float WakeRadius = 3f;
    private static readonly NavMeshPath LocalPath = new();
    private static readonly Vector3[] WakeCorners = new Vector3[64];
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
        WakeSearches.Clear();
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
        var origin = player.Position;
        if (!BotGroundPlacement.Finite(origin)) return false;
        if (!WakeSearches.TryGetValue(bot, out var search))
        {
            if (WakeSearches.Count >= 256) WakeSearches.Clear();
            WakeSearches[bot] = search = new WakeSearch { Origin = origin };
        }
        if ((search.Origin - origin).sqrMagnitude > .01f)
        { search.Origin = origin; search.Candidate = 0; search.RetryAt = 0; }
        if (Time.time < search.RetryAt) return false;
        var reason = "rejected-position";
        if (!IsRejected(bot, origin) && BotGroundPlacement.TryResolve(player, origin, out var current, out reason)
            && SameWakeFloor(origin, current) && ClearOfOtherBodies(bot, current))
        {
            if ((current - origin).sqrMagnitude > .0001f) player.Teleport(current);
            WakeSearches.Remove(bot);
            return true;
        }
        // Resume a bounded lateral search across retries. Never probe another height band:
        // a mesh point above or below the sleeper is not proof of its intended floor.
        for (var probe = 0; probe < 8; probe++)
        {
            if (!TakeProbe()) { reason = "query-budget"; break; }
            var index = search.Candidate;
            search.Candidate = (index + 1) % 32;
            var radius = index < 8 ? .5f : index < 16 ? 1f : index < 24 ? 2f : WakeRadius;
            var angle = index % 8 * Mathf.PI / 4f;
            var candidate = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            if (!NavMesh.SamplePosition(candidate, out var hit, .35f, NavMesh.AllAreas))
            { reason = "no-local-mesh"; continue; }
            if (!SameWakeFloor(origin, hit.position)) { reason = "different-floor"; continue; }
            if (DangerZones.IsInside(hit.position) || IsRejected(bot, hit.position))
            { reason = "unsafe-destination"; continue; }
            if (!BotGroundPlacement.TryResolve(player, hit.position, out var landing, out reason)) continue;
            if (!SameWakeFloor(origin, landing)) { reason = "different-floor"; continue; }
            if (!ClearOfOtherBodies(bot, landing)) { reason = "another-body"; continue; }
            if (!ConnectedWakeFloor(bot, origin, hit.position, landing))
            { reason = "blocked-segment"; continue; }
            player.Teleport(landing);
            WakeSearches.Remove(bot);
            Log.Info($"GHOST WAKE GROUND: {bot.Profile.Nickname} supported before activation from={origin} to={landing} sameFloor=True");
            return true;
        }
        if (Time.time >= search.LogAt)
        {
            Log.Warning($"GHOST WAKE GROUND: {bot.Profile.Nickname} activation deferred at={origin} reason={reason} nextProbe={search.Candidate}");
            search.LogAt = Time.time + 10f;
        }
        search.RetryAt = Time.time + .25f;
        return false;
    }

    private static bool SameWakeFloor(Vector3 origin, Vector3 target)
    {
        var delta = target - origin;
        return BotGroundPlacement.Finite(target) && Mathf.Abs(delta.y) <= WakeHeightLimit
            && delta.x * delta.x + delta.z * delta.z <= WakeRadius * WakeRadius + .001f;
    }

    private static bool ConnectedWakeFloor(BotOwner bot, Vector3 origin, Vector3 mesh, Vector3 landing)
    {
        // A complete route alone can connect two floors by taking a distant staircase.
        // Require a short route whose entire height stays within the starting floor band.
        if (!NavMesh.CalculatePath(origin, mesh, NavMesh.AllAreas, LocalPath)
            || LocalPath.status != NavMeshPathStatus.PathComplete) return false;
        var count = LocalPath.GetCornersNonAlloc(WakeCorners);
        if (count == 0 || count >= WakeCorners.Length) return false;
        var length = 0f;
        var previous = origin;
        for (var i = 0; i < count; i++)
        {
            var corner = WakeCorners[i];
            if (!SameWakeFloor(origin, corner)) return false;
            length += Vector3.Distance(previous, corner);
            previous = corner;
        }
        length += Vector3.Distance(previous, mesh);
        if (length > Vector3.Distance(origin, mesh) * 1.5f + .5f) return false;
        var mask = bot.GetPlayer.MovementContext.GroundMask;
        foreach (var height in WakeClearanceHeights)
            if (Physics.Linecast(origin + Vector3.up * height, landing + Vector3.up * height,
                    mask, QueryTriggerInteraction.Ignore)) return false;
        return true;
    }
    private static readonly float[] WakeClearanceHeights = { .35f, 1f, 1.5f };

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
        // Activation and native path corners must obey the same floor guard as PrepareWake.
        // Certify the actual pose again instead of snapping to a stale corner on another floor.
        if (!PrepareWake(bot)) return;
        var landing = bot.GetPlayer.Position;
        Watches.RemoveAll(w => ReferenceEquals(w.Bot, bot));
        Watches.Add(new Watch { Bot = bot, Surface = landing, Landing = landing, Source = "wake",
            Started = Time.time, NextRetry = Time.time + .5f, Repath = repath });
    }

    private static bool TakeProbe()
    {
        if (Time.time >= _probeWindow) { _probeWindow = Time.time + 0.2f; _nearbyProbes = 0; }
        return _nearbyProbes++ < 32;
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
            if (!SameWakeFloor(watch.Surface, landing)
                || (landing - watch.Landing).sqrMagnitude > 9f) { reason = "local-floor-limit"; continue; }
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
