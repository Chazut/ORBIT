using System;
using System.Collections.Generic;
using System.Diagnostics;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private const float WakeQueueDeadline = 0.25f;
    private const int MaxQueuedWakeGroups = 32;
    private readonly GhostWakeBudget _wakeFrameBudget = new();
    private readonly Dictionary<object, PendingWake> _wakingGroups = new();
    private readonly Dictionary<BotOwner, PendingWake> _wakingBots = new();
    private readonly List<PendingWake> _stagedWakes = new();
    private readonly List<WakeHuman> _wakeHumans = new(), _previousWakeHumans = new();

    private struct WakeHuman
    {
        internal Player Player;
        internal Vector3 Position, Motion;
    }

    private sealed class PendingWake
    {
        internal object Key;
        internal Squad Squad;
        internal readonly List<BotOwner> Bots = new();
        internal readonly List<Agent> Agents = new();
        internal GhostWakeReason Reason;
        internal float Started;
        internal int Next, Activated;
        internal bool Invalidated;
    }

    private bool IsWaking(object key) => key != null && _wakingGroups.ContainsKey(key);

    private void BeginWakeHumanScan()
    {
        _previousWakeHumans.Clear();
        _previousWakeHumans.AddRange(_wakeHumans);
        _wakeHumans.Clear();
    }

    private void TrackWakeHuman(Player player)
    {
        var view = new WakeHuman { Player = player, Position = player.Position };
        foreach (var previous in _previousWakeHumans)
            if (ReferenceEquals(previous.Player, player)) { view.Motion = view.Position - previous.Position; break; }
        _wakeHumans.Add(view);
    }

    private bool ApproachingWakeRing(Vector3 position)
    {
        var wake = Mathf.Sqrt(_wakeDistanceSqr);
        var outer = wake + Mathf.Clamp(wake * 0.1f, 5f, 25f);
        foreach (var view in _wakeHumans)
        {
            var to = position - view.Position;
            if (to.sqrMagnitude > _wakeDistanceSqr && to.sqrMagnitude <= outer * outer
                && Vector3.Dot(to, view.Motion) > 0.01f) return true;
        }
        return false;
    }

    private bool CanStageAt(Vector3 position)
        => CanStageAt(position, out _);

    private bool CanStageAt(Vector3 position, out bool adsThreat)
    {
        adsThreat = false;
        // No usable human view means we cannot prove a delayed body would be safely out of sight.
        if (_wakeHumans.Count == 0) return false;
        var aliveView = false;
        var critical = Mathf.Min(_wakeDistanceSqr, 75f * 75f);
        foreach (var view in _wakeHumans)
        {
            var player = view.Player;
            if (player?.HealthController is not { IsAlive: true }) continue;
            aliveView = true;
            var to = position - player.Position;
            if (to.sqrMagnitude <= critical) return false;
            var forward = player.LookDirection;
            if (forward.sqrMagnitude < 0.01f) return false;
            // Cover the normal view plus a sweep margin. ADS only promotes groups in this view,
            // never drains unrelated groups behind the player or behind opaque cover.
            if (Vector3.Dot(to, forward.normalized) < Mathf.Sqrt(to.sqrMagnitude) * 0.5f) continue;
            var eye = player.Position + new Vector3(0f, 1.5f, 0f);
            for (var sample = 0; sample < 2; sample++)
            {
                var ray = position + new Vector3(0f, sample == 0 ? 1.4f : 0.5f, 0f) - eye;
                var distance = ray.magnitude;
                if (distance < 1f || !Physics.Raycast(eye, ray / distance, distance,
                    LayersMaskController.HighPolyWithTerrainMask))
                {
                    adsThreat = player.HandsController is Player.FirearmController firearm && firearm.IsAiming;
                    return false;
                }
            }
        }
        return aliveView;
    }

    private bool CanStageBot(BotOwner bot)
        => bot != null && !bot.IsDead && bot.GetPlayer?.HealthController is { IsAlive: true }
           && bot.Memory?.GoalEnemy == null && bot.Memory?.IsUnderFire != true
           && !_targetedBy.ContainsKey(bot.ProfileId) && !_nativeGhosts.InFight(bot)
           && !InScopedView(bot.Position, out _)
           && CanStageAt(bot.GetPlayer.Position);

    private GhostWakeReason? PreWakeReason(Squad squad)
    {
        if (IsWaking(squad) || !IsSquadDormant(squad) || squad.GhostFightUntil > Time.time) return null;
        var approaching = false;
        foreach (var agent in squad.Members) approaching |= ApproachingWakeRing(agent.Position);
        if (!approaching) return null;
        foreach (var agent in squad.Members) if (!CanStageBot(agent.Bot)) return null;
        return new(GhostWakeCause.PreWake, "human approaching wake ring");
    }

    private GhostWakeReason? PreWakeReason(object key, List<BotOwner> group)
    {
        if (IsWaking(key) || VanillaDormantCount(group) != group.Count) return null;
        var approaching = false;
        foreach (var bot in group) approaching |= ApproachingWakeRing(bot.Position);
        if (!approaching) return null;
        foreach (var bot in group) if (!CanStageBot(bot)) return null;
        return new(GhostWakeCause.PreWake, "human approaching wake ring");
    }

    private static bool StageableReason(GhostWakeReason reason)
        => reason.Cause is GhostWakeCause.HumanProximity or GhostWakeCause.PreWake;

    private bool TryStageWake(Squad squad, GhostWakeReason reason)
    {
        if (!StageableReason(reason)) return false;
        if (_wakingGroups.TryGetValue(squad, out var existing)) return !StagedWakeUrgency(existing).HasValue;
        if (_stagedWakes.Count >= MaxQueuedWakeGroups || !IsSquadDormant(squad)
            || squad.GhostFightUntil > Time.time) return false;
        foreach (var agent in squad.Members)
            if (!CanStageBot(agent.Bot) || _wakingBots.ContainsKey(agent.Bot)
                || AnyAwakeBotNear(agent.Position, squad)) return false;
        var pending = new PendingWake { Key = squad, Squad = squad, Reason = reason, Started = Time.realtimeSinceStartup };
        foreach (var agent in squad.Members) { pending.Bots.Add(agent.Bot); pending.Agents.Add(agent); }
        AddStagedWake(pending);
        return true;
    }

    private bool TryStageWake(object key, List<BotOwner> group, GhostWakeReason reason)
    {
        if (!StageableReason(reason)) return false;
        if (_wakingGroups.TryGetValue(key, out var existing)) return !StagedWakeUrgency(existing).HasValue;
        if (_stagedWakes.Count >= MaxQueuedWakeGroups || group.Count == 0
            || VanillaDormantCount(group) != group.Count) return false;
        foreach (var bot in group)
            if (!CanStageBot(bot) || _wakingBots.ContainsKey(bot) || AnyAwakeBotNear(bot.Position, null)) return false;
        var pending = new PendingWake { Key = key, Reason = reason, Started = Time.realtimeSinceStartup };
        pending.Bots.AddRange(group); // Never retain the collection pool's mutable list.
        AddStagedWake(pending);
        return true;
    }

    private void AddStagedWake(PendingWake pending)
    {
        _wakingGroups.Add(pending.Key, pending);
        _stagedWakes.Add(pending);
        foreach (var bot in pending.Bots) _wakingBots.Add(bot, pending);
        Log.Info($"GHOST WAKE QUEUE: queued members={pending.Bots.Count} cause={pending.Reason.Cause} first={pending.Bots[0].ProfileId}");
    }

    private void CancelStagedWake(object key)
    {
        if (key == null || !_wakingGroups.TryGetValue(key, out var pending)) return;
        _wakingGroups.Remove(key);
        _stagedWakes.Remove(pending);
        foreach (var bot in pending.Bots)
            if (_wakingBots.TryGetValue(bot, out var owner) && ReferenceEquals(owner, pending)) _wakingBots.Remove(bot);
    }

    private void ForgetStagedBot(BotOwner bot)
    {
        if (bot == null || !_wakingBots.TryGetValue(bot, out var pending)) return;
        // Force a fresh safety/membership check on the next pump; never keep removed bodies queued.
        _wakingBots.Remove(bot);
        pending.Invalidated = true;
    }

    private void ClearStagedWakes()
    {
        _stagedWakes.Clear(); _wakingGroups.Clear(); _wakingBots.Clear();
    }

    private GhostWakeReason? StagedWakeUrgency(PendingWake pending)
    {
        if (pending.Invalidated) return new(GhostWakeCause.GroupChanged, "member removed during staged wake");
        if (Time.realtimeSinceStartup - pending.Started >= WakeQueueDeadline)
            return new(pending.Reason.Cause, "staged wake deadline");
        if (pending.Squad != null)
        {
            if (pending.Squad.Members.Count != pending.Agents.Count)
                return new(GhostWakeCause.GroupChanged, "membership changed during staged wake");
            for (var i = 0; i < pending.Agents.Count; i++)
                if (!ReferenceEquals(pending.Agents[i].Squad, pending.Squad)
                    || !pending.Squad.Members.Contains(pending.Agents[i]))
                    return new(GhostWakeCause.GroupChanged, "ownership changed during staged wake");
            var reason = WakeReason(pending.Squad, proximity: false);
            if (reason.HasValue && !StageableReason(reason.Value)) return reason;
        }
        else
        {
            if (_vanillaGroups.TryGetValue(pending.Key, out var current) && current.Count != pending.Bots.Count)
                return new(GhostWakeCause.GroupChanged, "membership changed during staged wake");
            var reason = VanillaWakeReason(pending.Key, pending.Bots, proximity: false);
            if (reason.HasValue && !StageableReason(reason.Value)) return reason;
        }
        foreach (var bot in pending.Bots)
        {
            if (bot == null || bot.IsDead || bot.GetPlayer?.HealthController is not { IsAlive: true })
                return new(GhostWakeCause.GroupChanged, "member removed during staged wake");
            if (pending.Squad == null && (_botRoster.GetAgent(bot) != null
                || !ReferenceEquals((object)bot.BotsGroup ?? bot, pending.Key)))
                return new(GhostWakeCause.GroupChanged, "native ownership changed during staged wake");
            if (bot.Memory?.GoalEnemy != null || bot.Memory?.IsUnderFire == true)
                return new(GhostWakeCause.RealFight, "combat during staged wake");
            if (InScopedView(bot.Position, out _))
                return new(GhostWakeCause.ScopedView, "player scope on staged group");
            if (!CanStageAt(bot.Position, out var adsThreat))
                return new(adsThreat ? GhostWakeCause.ScopedView : GhostWakeCause.HumanProximity,
                    adsThreat ? "player ADS on staged group" : "visible or close during staged wake");
        }
        // Awake squadmates are expected during a transition. Other live bodies still make an
        // encounter urgent, even between the normal world scans.
        foreach (var player in _gameWorld.AllAlivePlayersList)
        {
            if (player == null || !player.AIData.IsAI || player.HealthController is not { IsAlive: true }
                || DormantProfileIds.Contains(player.ProfileId)
                || player.Profile?.Info?.Settings?.Role == WildSpawnType.shooterBTR) continue;
            var owner = player.AIData.BotOwner;
            if (owner == null || pending.Bots.Contains(owner) || !IsActivatedNeighbour(player)) continue;
            foreach (var bot in pending.Bots)
                if (ReferenceEquals(owner.Memory?.GoalEnemy?.Person, bot.GetPlayer)
                    || (player.Position - bot.Position).sqrMagnitude <= _hostileWakeDistanceSqr)
                    return new(GhostWakeCause.BotProximity, "live encounter during staged wake");
        }
        return null;
    }

    private void PumpStagedWakes()
    {
        if (_stagedWakes.Count == 0 || _gameWorld?.AllAlivePlayersList == null) return;
        using var timing = TransitionPerformance.Measure(TransitionPhase.WakeQueue);
        UpdateScopeState();
        for (var i = _stagedWakes.Count - 1; i >= 0; i--)
        {
            if (i >= _stagedWakes.Count) continue;
            var pending = _stagedWakes[i];
            try
            {
                var urgent = StagedWakeUrgency(pending);
                if (urgent.HasValue) FinishStagedWake(pending, urgent.Value, forced: true);
            }
            catch (Exception e)
            {
                Log.Warning($"GHOST WAKE QUEUE: safety check failed, completing group: {e.Message}");
                FinishStagedWake(pending, new(GhostWakeCause.NativeFallback, "staged wake safety fallback"), forced: true);
            }
        }
        while (_stagedWakes.Count > 0 && _wakeFrameBudget.TryBegin(Time.frameCount, Stopwatch.GetTimestamp()))
        {
            var pending = _stagedWakes[0];
            // Round-robin groups so a large squad cannot monopolize the shared frame budget.
            _stagedWakes.RemoveAt(0); _stagedWakes.Add(pending);
            WakeStagedMember(pending, pending.Next++, pending.Reason);
            if (pending.Next == pending.Bots.Count) FinishStagedWake(pending, pending.Reason, forced: false);
        }
    }

    private void WakeStagedMember(PendingWake pending, int index, GhostWakeReason reason)
    {
        var bot = pending.Bots[index];
        if (bot == null || !_wakingBots.TryGetValue(bot, out var owner) || !ReferenceEquals(owner, pending)) return;
        using var timing = TransitionPerformance.Measure(TransitionPhase.WakeGroup);
        try
        {
            if (pending.Squad != null)
            {
                var agent = pending.Agents[index];
                if (agent.IsDormant)
                {
                    if (bot.IsDead || agent.Player?.HealthController is not { IsAlive: true }) OnAgentRemoved(agent);
                    else WakeAgentWithReason(agent, reason);
                    pending.Activated++;
                }
            }
            else if (bot.IsDead || bot.GetPlayer?.HealthController is not { IsAlive: true }) OnVanillaRemoved(bot);
            else if (WakeVanillaBotWithReason(bot, reason)) pending.Activated++;
        }
        catch (Exception e) { Log.Warning($"GHOST WAKE QUEUE: member wake failed: {e}"); }
    }

    private void FinishStagedWake(PendingWake pending, GhostWakeReason reason, bool forced)
    {
        if (forced)
        {
            _wakeFrameBudget.Urgent(Time.frameCount);
            for (var i = pending.Next; i < pending.Bots.Count; i++) WakeStagedMember(pending, i, reason);
        }
        CancelStagedWake(pending.Key);
        // A late addition or ownership transfer must not leave a new sleeper outside the snapshot.
        // Already awake and PreActive members never receive another activation.
        if (pending.Squad != null)
        {
            foreach (var agent in pending.Squad.Members)
                if (agent.IsDormant)
                {
                    _wakeFrameBudget.Urgent(Time.frameCount);
                    if (agent.Bot.IsDead || agent.Player?.HealthController is not { IsAlive: true }) OnAgentRemoved(agent);
                    else WakeAgentWithReason(agent, reason);
                }
            foreach (var agent in pending.Agents)
                if (agent.Squad != null && !ReferenceEquals(agent.Squad, pending.Squad))
                    WakeSquad(agent.Squad, new(GhostWakeCause.GroupChanged, "member transferred during staged wake"));
        }
        else
        {
            if (_vanillaGroups.TryGetValue(pending.Key, out var current))
                foreach (var bot in current)
                    if (_vanillaDormant.Contains(bot))
                    {
                        _wakeFrameBudget.Urgent(Time.frameCount);
                        if (bot.IsDead || bot.GetPlayer?.HealthController is not { IsAlive: true }) OnVanillaRemoved(bot);
                        else WakeVanillaBotWithReason(bot, reason);
                    }
            foreach (var bot in pending.Bots)
            {
                if (_botRoster.GetAgent(bot)?.Squad is { } squad)
                    WakeSquad(squad, new(GhostWakeCause.GroupChanged, "native member transferred during staged wake"));
                else if (!ReferenceEquals((object)bot.BotsGroup ?? bot, pending.Key))
                {
                    var key = (object)bot.BotsGroup ?? bot;
                    if (_vanillaGroups.TryGetValue(key, out var group))
                        WakeVanillaGroup(key, group, new(GhostWakeCause.GroupChanged, "native membership transferred during staged wake"));
                    else if (bot.BotsGroup != null)
                        for (var i = 0; i < bot.BotsGroup.MembersCount; i++)
                            WakeVanillaBotWithReason(bot.BotsGroup.Member(i), reason);
                }
            }
        }
        if (pending.Squad != null) pending.Squad.DormancySleepAllowedAt = Time.time + reason.CooldownSeconds;
        else _vanillaSleepAllowedAt[pending.Key] = Time.time + reason.CooldownSeconds;
        _windowWakes++;
        RecordWake(reason.Cause);
        Log.Info($"GHOST WAKE QUEUE: completed members={pending.Activated}/{pending.Bots.Count} forced={forced} cause={reason.Cause} elapsedMs={(Time.realtimeSinceStartup - pending.Started) * 1000f:F1} first={pending.Bots[0].ProfileId}");
    }
}
