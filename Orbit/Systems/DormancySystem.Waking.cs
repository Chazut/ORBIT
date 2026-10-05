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
    private const float WakeQueuePriorityAge = 0.5f;
    private float _nextOverflowTrace;
    private BotOwner _activatingBot;
    private PendingWake _activatingGroup;
    private long _activationStarted;
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
        internal float GroundRetryAt;
        internal int Next, Activated;
        internal bool Invalidated, Promoted;
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

    private bool CanStageAt(Vector3 position) => ViewUrgency(position) == null;

    private string ViewUrgency(Vector3 position)
    {
        if (_wakeHumans.Count == 0) return "human-view-unavailable";
        var aliveView = false;
        foreach (var view in _wakeHumans)
        {
            var player = view.Player;
            if (player?.HealthController is not { IsAlive: true }) continue;
            aliveView = true;
            var to = position - player.Position;
            var forward = player.LookDirection;
            if (forward.sqrMagnitude < 0.01f) return "human-view-unavailable";
            if (Vector3.Dot(to, forward.normalized) < Mathf.Sqrt(to.sqrMagnitude) * 0.5f) continue;
            var eye = player.Position + new Vector3(0f, 1.5f, 0f);
            for (var sample = 0; sample < 2; sample++)
            {
                var ray = position + new Vector3(0f, sample == 0 ? 1.4f : 0.5f, 0f) - eye;
                var distance = ray.magnitude;
                if (distance < 1f || !Physics.Raycast(eye, ray / distance, distance,
                    LayersMaskController.HighPolyWithTerrainMask))
                    return player.HandsController is Player.FirearmController firearm && firearm.IsAiming
                        ? "player-ADS" : "player-visible";
            }
        }
        return aliveView ? null : "human-view-unavailable";
    }

    private string BotWakeUrgency(BotOwner bot)
    {
        if (bot == null || bot.IsDead || bot.GetPlayer?.HealthController is not { IsAlive: true }) return "invalid-member";
        if (bot.Memory?.GoalEnemy != null || bot.Memory?.IsUnderFire == true) return "active-combat";
        if (_targetedBy.ContainsKey(bot.ProfileId)) return "targeted";
        if (_nativeGhosts.InFight(bot)) return "native-combat";
        if (InScopedView(bot.Position, out _)) return "scoped-view";
        return ViewUrgency(bot.GetPlayer.Position);
    }

    private bool CanStageBot(BotOwner bot) => BotWakeUrgency(bot) == null;

    private void TraceWakeDecision(string mode, GhostWakeReason reason, string detail, BotOwner member, float waitedMs = 0)
        => DiagnosticCapture.WakeDecision(mode, reason.Cause.ToString(), detail, member?.ProfileId, _stagedWakes.Count, waitedMs);

    private bool BypassWake(GhostWakeReason reason, string detail, BotOwner member)
    {
        TraceWakeDecision("bypass", reason, detail, member);
        return false;
    }

    private bool DeferOverflow(GhostWakeReason reason, BotOwner member)
    {
        // Keep the group asleep and retry on its next normal poll, rather than flushing many bodies.
        // Admission always checks real urgency before capacity, so danger never waits for a slot.
        if (Time.realtimeSinceStartup >= _nextOverflowTrace)
        {
            _nextOverflowTrace = Time.realtimeSinceStartup + 1f;
            TraceWakeDecision("deferred", reason, "queue-capacity", member);
        }
        return true;
    }

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
        => reason.Cause is GhostWakeCause.HumanProximity or GhostWakeCause.PreWake
            or GhostWakeCause.BotProximity or GhostWakeCause.Extraction or GhostWakeCause.RealFight;

    private bool TryStageWake(Squad squad, GhostWakeReason reason)
    {
        var first = squad.Members.Count > 0 ? squad.Members[0].Bot : null;
        if (!StageableReason(reason)) return BypassWake(reason, "wake-cause", first);
        if (_wakingGroups.TryGetValue(squad, out var existing))
        {
            var urgent = StagedWakeUrgency(existing);
            return !urgent.HasValue || BypassWake(reason, urgent.Value.Message, first);
        }
        if (!IsSquadDormant(squad)) return BypassWake(reason, "group-not-fully-ghost", first);
        if (squad.GhostFightUntil > Time.time) return BypassWake(reason, "active-ghost-fight", first);
        foreach (var agent in squad.Members)
        {
            var blocker = BotWakeUrgency(agent.Bot);
            if (blocker != null) return BypassWake(reason, blocker, agent.Bot);
            if (_wakingBots.ContainsKey(agent.Bot)) return BypassWake(reason, "member-already-queued", agent.Bot);
        }
        if (_stagedWakes.Count >= MaxQueuedWakeGroups) return DeferOverflow(reason, first);
        var pending = new PendingWake { Key = squad, Squad = squad, Reason = reason, Started = Time.realtimeSinceStartup };
        foreach (var agent in squad.Members) { pending.Bots.Add(agent.Bot); pending.Agents.Add(agent); }
        AddStagedWake(pending);
        return true;
    }

    private bool TryStageWake(object key, List<BotOwner> group, GhostWakeReason reason)
    {
        var first = group.Count > 0 ? group[0] : null;
        if (!StageableReason(reason)) return BypassWake(reason, "wake-cause", first);
        if (_wakingGroups.TryGetValue(key, out var existing))
        {
            var urgent = StagedWakeUrgency(existing);
            return !urgent.HasValue || BypassWake(reason, urgent.Value.Message, first);
        }
        if (group.Count == 0 || VanillaDormantCount(group) != group.Count)
            return BypassWake(reason, "group-not-fully-ghost", first);
        foreach (var bot in group)
        {
            var blocker = BotWakeUrgency(bot);
            if (blocker != null) return BypassWake(reason, blocker, bot);
            if (_wakingBots.ContainsKey(bot)) return BypassWake(reason, "member-already-queued", bot);
        }
        if (_stagedWakes.Count >= MaxQueuedWakeGroups) return DeferOverflow(reason, first);
        var pending = new PendingWake { Key = key, Reason = reason, Started = Time.realtimeSinceStartup };
        pending.Bots.AddRange(group);
        AddStagedWake(pending);
        return true;
    }

    private void AddStagedWake(PendingWake pending)
    {
        _wakingGroups.Add(pending.Key, pending);
        _stagedWakes.Add(pending);
        foreach (var bot in pending.Bots) _wakingBots.Add(bot, pending);
        TraceWakeDecision("queued", pending.Reason, "safe-to-delay", pending.Bots[0]);
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
        GhostWakeActivationDiagnostics.Forget(bot);
        if (bot == null || !_wakingBots.TryGetValue(bot, out var pending)) return;
        // Force a fresh safety/membership check on the next pump; never keep removed bodies queued.
        _wakingBots.Remove(bot);
        pending.Invalidated = true;
    }

    private void ClearStagedWakes()
    {
        _stagedWakes.Clear(); _wakingGroups.Clear(); _wakingBots.Clear();
        _wakeFrameBudget.Reset(); _nextOverflowTrace = 0;
        _activatingBot = null; _activatingGroup = null;
        GhostWakeActivationDiagnostics.Reset();
    }

    private GhostWakeReason? StagedWakeUrgency(PendingWake pending)
    {
        if (pending.Invalidated) return new(GhostWakeCause.GroupChanged, "member removed during staged wake");
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
            if (ViewUrgency(bot.Position) is { } viewReason)
                return new(viewReason == "player-ADS" ? GhostWakeCause.ScopedView : GhostWakeCause.HumanProximity, viewReason);
        }
        // Mere proximity is queued. Actual targeting must still interrupt the delay between world scans.
        foreach (var player in _gameWorld.AllAlivePlayersList)
        {
            if (player == null || !player.AIData.IsAI || player.HealthController is not { IsAlive: true }
                || DormantProfileIds.Contains(player.ProfileId)
                || player.Profile?.Info?.Settings?.Role == WildSpawnType.shooterBTR) continue;
            var owner = player.AIData.BotOwner;
            if (owner == null || pending.Bots.Contains(owner) || !IsActivatedNeighbour(player)) continue;
            foreach (var bot in pending.Bots)
                if (ReferenceEquals(owner.Memory?.GoalEnemy?.Person, bot.GetPlayer))
                    return new(GhostWakeCause.Targeted, "live targeting during staged wake");
        }
        return null;
    }

    private void PumpStagedWakes()
    {
        if (_gameWorld?.AllAlivePlayersList == null) return;
        if (_stagedWakes.Count == 0 && _activatingBot == null) return;
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
        if (_activatingBot != null)
        {
            var waiting = !_activatingBot.IsDead && _activatingBot.GetPlayer?.HealthController is { IsAlive: true }
                && _activatingBot.BotState is EBotState.PreActive or EBotState.NonActive;
            var elapsed = (Stopwatch.GetTimestamp() - _activationStarted) * 1000d / Stopwatch.Frequency;
            if (waiting && elapsed < 5000) return;
            if (waiting)
            {
                TraceWakeDecision("activation-stalled", _activatingGroup.Reason, "not-active-after-5s", _activatingBot, (float)elapsed);
                Log.Warning($"GHOST WAKE QUEUE: activation stalled for {_activatingBot.ProfileId}; releasing queue slot without replaying activation");
                GhostWakeActivationDiagnostics.Forget(_activatingBot);
            }
            var completed = _activatingGroup;
            _activatingBot = null; _activatingGroup = null;
            _wakeFrameBudget.Complete(Stopwatch.GetTimestamp(), _cfg.WakeIntervalMs);
            if (completed.Next == completed.Bots.Count && _wakingGroups.TryGetValue(completed.Key, out var current)
                && ReferenceEquals(current, completed)) FinishStagedWake(completed, completed.Reason, forced: false);
        }
        if (_stagedWakes.Count == 0) return;
        var selected = -1;
        var oldest = float.MaxValue;
        for (var i = 0; i < _stagedWakes.Count; i++)
        {
            var item = _stagedWakes[i];
            if (Time.realtimeSinceStartup < item.GroundRetryAt) continue;
            if (selected < 0) selected = i;
            var age = Time.realtimeSinceStartup - item.Started;
            if (age < WakeQueuePriorityAge) continue;
            if (!item.Promoted)
            {
                item.Promoted = true;
                TraceWakeDecision("aged", item.Reason, "priority-after-500ms", item.Bots[0], age * 1000f);
            }
            if (item.Started < oldest) { oldest = item.Started; selected = i; }
        }
        if (selected < 0 || !_wakeFrameBudget.TryBegin(Time.frameCount, Stopwatch.GetTimestamp())) return;
        var next = _stagedWakes[selected];
        _stagedWakes.RemoveAt(selected); _stagedWakes.Add(next);
        try
        {
            var bot = next.Bots[next.Next];
            WakeStagedMember(next, next.Next, next.Reason);
            // Ground certification can defer one unsafe member. Retain its queue slot and
            // let other groups run; never wait for activation of a body we kept inactive.
            if (next.Squad != null ? next.Agents[next.Next].IsDormant : _vanillaDormant.Contains(bot))
            { next.GroundRetryAt = Time.realtimeSinceStartup + .25f; return; }
            next.Next++;
            if (bot != null && !bot.IsDead && bot.GetPlayer?.HealthController is { IsAlive: true }
                && bot.BotState is EBotState.PreActive or EBotState.NonActive)
            {
                _activatingBot = bot; _activatingGroup = next;
                _activationStarted = Stopwatch.GetTimestamp();
            }
            else if (next.Next == next.Bots.Count) FinishStagedWake(next, next.Reason, forced: false);
        }
        finally
        {
            // PostActivate only requests PreActive. Wait for the engine's later activation tick.
            if (_activatingBot == null) _wakeFrameBudget.Complete(Stopwatch.GetTimestamp(), _cfg.WakeIntervalMs);
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
                    if (!agent.IsDormant) pending.Activated++;
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
            TraceWakeDecision("urgent", reason, reason.Message, pending.Bots[0], (Time.realtimeSinceStartup - pending.Started) * 1000f);
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
        if (forced) _wakeFrameBudget.Complete(Stopwatch.GetTimestamp(), _cfg.WakeIntervalMs);
        TraceWakeDecision("complete", reason, forced ? "immediate" : "paced", pending.Bots[0], (Time.realtimeSinceStartup - pending.Started) * 1000f);
        _windowWakes++;
        RecordWake(reason.Cause);
        Log.Info($"GHOST WAKE QUEUE: completed members={pending.Activated}/{pending.Bots.Count} forced={forced} cause={reason.Cause} elapsedMs={(Time.realtimeSinceStartup - pending.Started) * 1000f:F1} first={pending.Bots[0].ProfileId}");
    }
}
