using System;
using System.Collections.Generic;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Sain;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private const int SniperCandidatesPerFrame = 12;
    private const float SniperSightInterval = .1f, SniperSweepInterval = 1f, SniperContactTimeout = 8f;
    private readonly SniperContactBridge _sniperContactBridge = new();
    private Agent _sniperObserver;
    private SniperPlan _sniperPlan;
    private int _sniperSquadCursor, _sniperPlayerCursor, _sniperPlayersLeft, _sniperFrame = -1;
    private float _nextSniperSweep, _nextSniperSight, _nextSniperReport;
    private int _sniperRangeRejects, _sniperFriendlyRejects, _sniperBlocked, _sniperSightChecks;
    private PendingSniperContact _sniperContact;

    private sealed class PendingSniperContact
    {
        internal Agent Observer;
        internal SniperPlan Plan;
        internal Player Target;
        internal float Until;
    }

    private void ClearSniperContacts()
    {
        ClearSniperTracking();
        _sniperContact = null; _sniperObserver = null; _sniperPlan = null;
        _sniperSquadCursor = _sniperPlayerCursor = _sniperPlayersLeft = 0;
        _nextSniperSweep = _nextSniperSight = 0;
    }

    private bool AwakeSniperTarget(Player target, Agent observer)
        => target != null && target != observer.Player && target.HealthController is { IsAlive: true }
            && target.AIData is { IsAI: true } && !DormantProfileIds.Contains(target.ProfileId)
            && target.Profile?.Info?.Settings?.Role != WildSpawnType.shooterBTR
            && IsActivatedNeighbour(target) && !IsMemberBot(observer.Squad, target.AIData.BotOwner);

    private bool SniperHostile(BotOwner observer, BotOwner target)
    {
        var a = observer?.BotsGroup; var b = target?.BotsGroup;
        if (a == null || b == null || ReferenceEquals(a, b)) return false;
        var pa = _gameWorld.GetAlivePlayerBridgeByProfileID(observer.ProfileId)?.iPlayer;
        var pb = _gameWorld.GetAlivePlayerBridgeByProfileID(target.ProfileId)?.iPlayer;
        if (pa == null || pb == null) return false;
        if (a.IsEnemy(pb) || b.IsEnemy(pa)) return true;
        var key = string.CompareOrdinal(observer.ProfileId, target.ProfileId) < 0
            ? observer.ProfileId + "|" + target.ProfileId : target.ProfileId + "|" + observer.ProfileId;
        // Share the existing raid cache: never reroll a chanced hostility every frame.
        if (_hostilityCache.TryGetValue(key, out var hostile)) return hostile;
        hostile = a.IsPlayerEnemy(pb) || b.IsPlayerEnemy(pa);
        _hostilityCache[key] = hostile;
        return hostile;
    }

    private bool SniperRange(Agent observer, Player target, out float distance, out float reach)
    {
        distance = Vector3.Distance(observer.Position, target.Position);
        reach = 0;
        if (distance > SkirmishReachCap) return false;
        _darkness = CurrentDarkness();
        reach = MemberVisibility(observer.Bot, target.AIData.BotOwner).Reach;
        return reach > 0 && distance <= reach;
    }

    private void PumpSniperContacts(List<Squad> squads)
    {
        if (_sniperFrame == Time.frameCount) return;
        _sniperFrame = Time.frameCount;
        try
        {
            using var timing = TransitionPerformance.Measure(TransitionPhase.GhostContacts);
            PumpSniperTracking();
            if (_sniperContact != null) { CompleteSniperContact(); return; }
            if (!ServerConfig.Rush.Enabled) { _sniperObserver = null; return; }
            if (_sniperObserver == null)
            {
                if (Time.time < _nextSniperSweep || squads.Count == 0) return;
                if (_sniperSquadCursor >= squads.Count) _sniperSquadCursor = 0;
                var squad = squads[_sniperSquadCursor++];
                if (_sniperSquadCursor >= squads.Count) _nextSniperSweep = Time.time + SniperSweepInterval;
                _sniperPlan = squad?.Rush?.Sniper;
                var observer = _sniperPlan?.ContactObserver;
                if (observer == null || !observer.IsDormant || !IsSquadDormant(squad)
                    || IsWaking(squad) || squad.GhostFightUntil > Time.time) return;
                _sniperObserver = observer;
                _sniperPlayerCursor = 0; _sniperPlayersLeft = _gameWorld.AllAlivePlayersList.Count;
            }
            var agent = _sniperObserver;
            if (_sniperPlan.ContactObserver != agent || !agent.IsDormant || agent.Bot.IsDead
                || agent.Player.HealthController is not { IsAlive: true } || IsWaking(agent.Squad)
                || agent.Squad.GhostFightUntil > Time.time)
            { _sniperObserver = null; return; }
            var players = _gameWorld.AllAlivePlayersList;
            for (var checkedCount = 0; checkedCount < SniperCandidatesPerFrame && _sniperPlayersLeft > 0; checkedCount++)
            {
                if (players.Count == 0) break;
                if (_sniperPlayerCursor >= players.Count) _sniperPlayerCursor = 0;
                var target = players[_sniperPlayerCursor];
                // Retain this candidate until the global ray budget is available.
                if (AwakeSniperTarget(target, agent))
                {
                    if (Time.time < _nextSniperSight) return;
                    if (!SniperRange(agent, target, out var distance, out var reach)) _sniperRangeRejects++;
                    else if (!SniperHostile(agent.Bot, target.AIData.BotOwner)) _sniperFriendlyRejects++;
                    else
                    {
                        _nextSniperSight = Time.time + SniperSightInterval;
                        _sniperSightChecks++;
                        if (ClearFightLos(agent.Position, target.Position))
                        {
                            _sniperContact = new() { Observer = agent, Plan = _sniperPlan, Target = target,
                                Until = Time.time + SniperContactTimeout };
                            _sniperObserver = null;
                            Log.Info($"GHOST SNIPER AWAKE: {agent} state=detected target={target.ProfileId} name={target.Profile?.Nickname} distance={distance:F1}m reach={reach:F1}m los=clear");
                            WakeSquad(agent.Squad, new(GhostWakeCause.RealFight, "sniper saw awake enemy",
                                target, agent.Bot.ProfileId, distance));
                            return;
                        }
                        _sniperBlocked++;
                        _sniperPlayerCursor++; _sniperPlayersLeft--;
                        ReportSniperScan();
                        return; // At most one three-ray test globally per frame, and ten per second.
                    }
                }
                _sniperPlayerCursor++; _sniperPlayersLeft--;
            }
            if (_sniperPlayersLeft <= 0 || players.Count == 0) _sniperObserver = null;
            ReportSniperScan();
        }
        catch (Exception error)
        {
            _sniperObserver = null; _sniperContact = null;
            _nextSniperSweep = Time.time + SniperSweepInterval;
            if (Time.time >= _nextSniperReport)
            {
                _nextSniperReport = Time.time + 15f;
                Log.Warning($"GHOST SNIPER AWAKE: state=error detail={error.Message}");
            }
        }
    }

    private void CompleteSniperContact()
    {
        var contact = _sniperContact;
        var agent = contact.Observer; var target = contact.Target;
        string reason = null;
        if (Time.time > contact.Until) reason = "activation-timeout";
        else if (!ServerConfig.Rush.Enabled || contact.Plan.ContactObserver != agent
            || !agent.Squad.Members.Contains(agent)
            || agent.Bot.IsDead || agent.Player.HealthController is not { IsAlive: true }) reason = "post-no-longer-active";
        else if (!AwakeSniperTarget(target, agent) || !_gameWorld.AllAlivePlayersList.Contains(target)) reason = "target-no-longer-awake";
        if (reason == null && (agent.IsDormant || agent.Bot.BotState != EBotState.Active
            || !agent.Bot.gameObject.activeInHierarchy)) return;
        if (reason == null && Time.time < _nextSniperSight) return;
        if (reason == null)
        {
            if (!SniperRange(agent, target, out _, out _)) reason = "target-left-range";
            else if (!SniperHostile(agent.Bot, target.AIData.BotOwner)) reason = "target-no-longer-hostile";
            else
            {
                _nextSniperSight = Time.time + SniperSightInterval;
                _sniperSightChecks++;
                if (!ClearFightLos(agent.Position, target.Position)) reason = "sight-lost-during-wake";
            }
        }
        _sniperContact = null;
        if (reason != null)
        {
            Log.Info($"GHOST SNIPER AWAKE: {agent} state=cancelled target={target?.ProfileId} reason={reason}");
            return;
        }
        var current = agent.Bot.Memory.GoalEnemy;
        if (current?.Person?.ProfileId != target.ProfileId && GhostTargetingThreat.IsImmediate(current))
        {
            Log.Info($"GHOST SNIPER AWAKE: {agent} state=cancelled target={target.ProfileId} reason=existing-immediate-threat");
            return;
        }
        var person = _gameWorld.GetAlivePlayerBridgeByProfileID(target.ProfileId)?.iPlayer;
        if (person == null)
        {
            Log.Warning($"GHOST SNIPER AWAKE: {agent} state=handoff-failed target={target.ProfileId} reason=player-bridge-unavailable");
            return;
        }
        var bot = agent.Bot;
        if (!bot.EnemiesController.EnemyInfos.TryGetValue(person, out var enemy))
        {
            bot.BotsGroup.AddEnemy(person, EBotEnemyCause.checkAddTODO);
            if (!bot.EnemiesController.EnemyInfos.TryGetValue(person, out enemy))
            {
                Log.Warning($"GHOST SNIPER AWAKE: {agent} state=handoff-failed target={target.ProfileId} reason=native-enemy-unavailable");
                return;
            }
        }
        enemy.PersonalLastPos = target.Position;
        enemy.PersonalLastSeenTime = Time.time;
        enemy.SetVisible(true);
        bot.Memory.GoalEnemy = enemy;
        bot.Memory.SetLastTimeSeeEnemy();
        bot.Memory.CheckIsPeace();
        // Seed the confirmed contact once. Native perception, aiming and CanShoot own subsequent ticks.
        var sain = _sniperContactBridge.Assign(_gameWorld, bot, person, target.Position);
        if (SniperRange(agent, target, out _, out var reach))
            TrackSniperContact(new SniperContactRange(agent, contact.Plan, target, enemy, reach));
        Log.Info($"GHOST SNIPER AWAKE: {agent} state=target-assigned target={target.ProfileId} name={target.Profile?.Nickname} distance={Vector3.Distance(agent.Position, target.Position):F1}m sain={sain}");
    }

    private void ReportSniperScan()
    {
        if (Time.time < _nextSniperReport || _sniperRangeRejects + _sniperFriendlyRejects + _sniperSightChecks == 0) return;
        _nextSniperReport = Time.time + 15f;
        Log.Debug($"GHOST SNIPER AWAKE: state=scan sightChecks={_sniperSightChecks} blocked={_sniperBlocked} outOfRange={_sniperRangeRejects} friendly={_sniperFriendlyRejects} candidateBudget={SniperCandidatesPerFrame} sightInterval={SniperSightInterval:F1}s");
        _sniperRangeRejects = _sniperFriendlyRejects = _sniperBlocked = _sniperSightChecks = 0;
    }
}
