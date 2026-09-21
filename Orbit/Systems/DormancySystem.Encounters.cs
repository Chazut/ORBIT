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
            if (reason != null) WakeSquad(squad, reason);
        }
        foreach (var kv in _vanillaGroups)
        {
            var dormant = VanillaDormantCount(kv.Value);
            if (dormant == 0) continue;
            var reason = dormant != kv.Value.Count ? "group membership changed"
                : VanillaWakeReason(kv.Key, kv.Value, proximity: false);
            if (reason != null) WakeVanillaGroup(kv.Key, kv.Value, reason);
        }

        var totalStandard = 0;
        var awakeStandard = 0;
        foreach (var agent in liveAgents)
        {
            if (agent == null || IsDefaultDormant(agent.Bot)) continue;
            totalStandard++;
            if (!agent.IsDormant) awakeStandard++;
        }
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
            if (VanillaDormantCount(kv.Value) != 0 || !CanVanillaSleep(kv.Key, kv.Value)) continue;
            var scoped = false;
            foreach (var bot in kv.Value)
                if (InScopedView(bot.Position, out _)) { scoped = true; break; }
            if (scoped) continue;
            var unit = _encounterPlan.Add(kv.Key);
            foreach (var bot in kv.Value) _encounterPlan.Member(unit, bot, bot.Position);
        }
        foreach (var player in _gameWorld.AllAlivePlayersList)
        {
            if (player == null || !player.AIData.IsAI || player.HealthController is not { IsAlive: true }
                || DormantProfileIds.Contains(player.ProfileId)
                || player.Profile?.Info?.Settings?.Role == WildSpawnType.shooterBTR) continue;
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
                _wakeByAwakeBot++;
                WakeSquad(squad, $"awake bot near {agent}");
                break;
            }
        }
        foreach (var kv in _vanillaGroups)
        {
            if (VanillaDormantCount(kv.Value) == 0) continue;
            if (_vanillaGroupSleptAt.TryGetValue(kv.Key, out var sleptAt)
                && Time.time - sleptAt < SleepGraceSeconds) continue;
            foreach (var bot in kv.Value)
            {
                if (!AnyAwakeBotNear(bot.Position, null)) continue;
                _wakeByAwakeBot++;
                WakeVanillaGroup(kv.Key, kv.Value, $"awake bot near {bot.GetPlayer.Profile?.Nickname}");
                break;
            }
        }
        _lastAwakeStandard = 0;
        foreach (var agent in liveAgents)
            if (agent != null && !agent.IsDormant && !IsDefaultDormant(agent.Bot)) _lastAwakeStandard++;
    }

    private int VanillaDormantCount(List<BotOwner> bots)
    {
        var count = 0;
        foreach (var bot in bots) if (_vanillaDormant.Contains(bot)) count++;
        return count;
    }
}
