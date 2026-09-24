using EFT;
using Orbit.Entities;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private readonly GhostSleepPlan _encounterPlan = new();

    private void UpdateSleepPreferred(List<Agent> liveAgents, List<Squad> squads)
    {
        CollectVanillaGroups();
        // Damage, targeting, player proximity, scope and native fallbacks always take precedence.
        foreach (var squad in squads)
        {
            if (squad == null || squad.Members.Count == 0 || !IsSquadDormant(squad)) continue;
            var reason = WakeReason(squad, proximity: false);
            if (reason != null) WakeSquad(squad, reason.Value);
        }
        foreach (var kv in _vanillaGroups)
        {
            var dormant = VanillaDormantCount(kv.Value);
            if (dormant == 0) continue;
            // A late spawn can join an existing Ghost group. Check real wake causes first,
            // then let the sleep plan absorb its awake members without resetting the sleepers.
            var reason = VanillaWakeReason(kv.Key, kv.Value, proximity: false);
            if (reason != null) WakeVanillaGroup(kv.Key, kv.Value, reason.Value);
        }

        var totalStandard = 0;
        var awakeStandard = 0;
        foreach (var agent in liveAgents)
        {
            if (agent == null || IsDefaultDormant(agent.Bot)) continue;
            totalStandard++;
            if (!agent.IsDormant) awakeStandard++;
        }
        CountNativeStandard(ref totalStandard, ref awakeStandard);
        var floor = Mathf.Min(_minAwakeBots, (totalStandard + 1) / 2);
        _encounterPlan.Clear();
        foreach (var squad in squads)
        {
            if (squad == null || squad.Members.Count == 0 || IsSquadDormant(squad)) continue;
            UpdateHpTracking(squad);
            if (!CanSleep(squad)) continue;
            var scoped = false;
            foreach (var agent in squad.Members)
                if (InScopedView(agent.Position, out _)) { scoped = true; break; }
            if (scoped) continue;
            var standard = !IsDefaultDormantSquad(squad);
            if (standard && awakeStandard - squad.Members.Count < floor) { _blockedFloor++; continue; }
            var unit = _encounterPlan.Add(squad);
            foreach (var agent in squad.Members) _encounterPlan.Member(unit, agent.Bot, agent.Position);
            if (standard) awakeStandard -= squad.Members.Count;
        }
        foreach (var kv in _vanillaGroups)
        {
            var dormant = VanillaDormantCount(kv.Value);
            if (dormant == kv.Value.Count || NativeAwakeCount(kv.Value) == 0
                || !CanVanillaSleep(kv.Key, kv.Value, joining: dormant > 0)) continue;
            var standard = NativeAwakeCount(kv.Value, standardOnly: true);
            if (standard > 0 && awakeStandard - standard < floor) { _blockedFloor++; continue; }
            var unit = _encounterPlan.Add(kv.Key);
            foreach (var bot in kv.Value) _encounterPlan.Member(unit, bot, bot.Position);
            awakeStandard -= standard;
        }
        foreach (var player in _gameWorld.AllAlivePlayersList)
        {
            if (player == null || !player.AIData.IsAI || player.HealthController is not { IsAlive: true }
                || DormantProfileIds.Contains(player.ProfileId)
                || player.Profile?.Info?.Settings?.Role == WildSpawnType.shooterBTR) continue;
            if (!IsActivatedNeighbour(player)) continue;
            _encounterPlan.Awake(player.AIData.BotOwner, player.Position);
        }
        _encounterPlan.Resolve(_hostileWakeDistanceSqr);
        for (var i = 0; i < _encounterPlan.Count; i++)
        {
            var unit = _encounterPlan[i];
            if (!unit.Allowed) { _blockedProximity++; continue; }
            if (unit.Key is Squad squad) SleepSquad(squad);
            else SleepVanillaGroup(unit.Key, _vanillaGroups[unit.Key]);
        }

        // An ineligible awake neighbour retains the existing wake behaviour and sleep grace.
        foreach (var squad in squads)
        {
            if (squad == null || squad.Members.Count == 0 || !IsSquadDormant(squad)) continue;
            if (Time.time - squad.DormancySleptAt < SleepGraceSeconds) continue;
            foreach (var agent in squad.Members)
            {
                if (!AnyAwakeBotNear(agent.Position, squad)) continue;
                WakeSquad(squad, new(GhostWakeCause.BotProximity, $"awake bot near {agent}"));
                break;
            }
        }
        foreach (var kv in _vanillaGroups)
        {
            if (VanillaDormantCount(kv.Value) == 0) continue;
            // An activated newcomer that could not join the plan still needs the group's
            // bodies. PreActive/NonActive spawns wait for initialization instead.
            if (NativeAwakeCount(kv.Value) > 0)
            {
                WakeVanillaGroup(kv.Key, kv.Value, new(GhostWakeCause.GroupChanged,
                    "new members cannot enter Ghost"));
                continue;
            }
            if (_vanillaGroupSleptAt.TryGetValue(kv.Key, out var sleptAt)
                && Time.time - sleptAt < SleepGraceSeconds) continue;
            foreach (var bot in kv.Value)
            {
                if (!AnyAwakeBotNear(bot.Position, null)) continue;
                WakeVanillaGroup(kv.Key, kv.Value, new(GhostWakeCause.BotProximity, $"awake bot near {bot.GetPlayer.Profile?.Nickname}"));
                break;
            }
        }
        _lastAwakeStandard = 0;
        foreach (var agent in liveAgents)
            if (agent != null && !agent.IsDormant && !IsDefaultDormant(agent.Bot)) _lastAwakeStandard++;
        var nativeTotal = 0;
        CountNativeStandard(ref nativeTotal, ref _lastAwakeStandard);
    }

    private int VanillaDormantCount(List<BotOwner> bots)
    {
        var count = 0;
        foreach (var bot in bots) if (_vanillaDormant.Contains(bot)) count++;
        return count;
    }

    private int NativeAwakeCount(List<BotOwner> bots, bool standardOnly = false)
    {
        var count = 0;
        foreach (var bot in bots)
            if (!_vanillaDormant.Contains(bot) && bot.BotState == EBotState.Active
                && (!standardOnly || !IsDefaultDormant(bot))) count++;
        return count;
    }

    // A player is listed before its BotOwner/brain finishes spawning. It cannot sleep yet,
    // but must not block the plan or wake nearby ghosts during that transient state.
    private static bool IsActivatedNeighbour(Player player)
        => player.AIData.BotOwner is { BotState: EBotState.Active };
}
