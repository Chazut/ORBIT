using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Looting;
using Orbit.Navigation;
using Orbit.Systems;
using Orbit.Tasks.Actions;
using UnityEngine;
using Random = UnityEngine.Random;
using Range = Orbit.Config.Range;

namespace Orbit.Tasks.Strategies;

/// <summary>
/// The squad-level dispatch loop. For every active squad it ticks main objectives (Kills roam timer,
/// LootValue cell entry / cleanup / engaged- time timeout, Quest completion), drives the combat-convergence
/// override, refreshes the per-squad unreachability cache as the leader crosses cells, scans for
/// opportunistic-corpse interrupts, then (re-)dispatches each member onto either the squad anchor, a roam
/// splinter, or a loot splinter as appropriate.
/// </summary>
public class GotoObjectiveStrategy(SquadData squadData, WaypointSystem waypointSystem, float hysteresis) : Task<Squad>(hysteresis)
{
    private static Range _moveTimeout = new(400, 600);
    private Range _guardDuration = new(Plugin.ObjectiveGuardDuration.Value.x, Plugin.ObjectiveGuardDuration.Value.y);
    private Range _guardDurationCut = new(Plugin.ObjectiveGuardDurationCut.Value.x, Plugin.ObjectiveGuardDurationCut.Value.y);
    private Range _adjustedGuardDuration = new(Plugin.ObjectiveAdjustedGuardDuration.Value.x, Plugin.ObjectiveAdjustedGuardDuration.Value.y);
    // Scav-only idle pause at a Synthetic POI. The default 3-7s adjusted guard makes scavs look hyperactive,
    // chaining waypoints without ever stopping. 10s-120s gaussian (mean ~65s) gives a mix of short glances
    // and longer corner-camping pauses. During the wait the agent runs GuardAction (cover point + area sweeps
    // + watch direction rotation), so it's not a frozen stand-still.
    private readonly Range _scavSyntheticIdleDuration = new(10f, 120f);

    public override void UpdateScore(int ordinal)
    {
        var squads = squadData.Entities.Values;
        for (var i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            squad.TaskScores[ordinal] = 0.5f;
        }
    }

    public override void Activate(Squad entity)
    {
        base.Activate(entity);

        if (entity.Objective.Location == null) return;

        // If we have an objective, reset the timer on activation.
        var timeout = entity.Objective.Status == SquadObjectiveState.Wait
            ? _guardDuration.SampleGaussian()
            : _moveTimeout.SampleGaussian();

        ResetDuration(entity.Objective, timeout);
    }

    public override void Deactivate(Entity entity)
    {
        // Return any assignments before deactivating.
        waypointSystem.Return(entity);
        base.Deactivate(entity);
    }

    public override void Update()
    {
        for (var i = 0; i < ActiveEntities.Count; i++)
        {
            var squad = ActiveEntities[i];
            var squadObjective = squad.Objective;

            // Degraded tickrate (opt-in): squads far from every living human re-run their decision loop less
            // often to reclaim CPU on lower-end machines. MovementSystem (path-follow, look, doors, stuck) runs
            // every frame regardless, so a throttled squad keeps walking its current path and just defers NEW
            // decisions (dispatch, rally, extract, objective completion) until the far interval elapses.
            if (Plugin.DegradedTickrateEnabled.Value && ShouldDeferDecisionTick(squad)) continue;
            squad.LastDecisionTickTime = Time.time;

            // Deferred SAIN personality resolution. PMC squads spawn before SAIN attaches its BotComponent
            // (1-2s delay), so SquadRegistry deferred the lookup + the main-objective roll. Retry here every
            // tick until the brain resolves or the 5 s deadline lapses (then lock to Average and generate
            // mains anyway).
            if (squad.SainResolutionPending)
            {
                Singleton<OrbitManager>.Instance?.SquadRegistry?.TryResolvePersonality(squad);
            }

            // Corpse-stuck watchdog. A squad glued to one corpse it never completes would otherwise sit on it
            // forever. Normal corpse loot resolves well under the timeout and blacklists the corpse on
            // success / fail / empty, so this only bites when nothing completes: the corpse is unreachable, the
            // loot hangs, or a stuck member keeps finishedCount < Size so the en-route 3-fail blacklist below
            // never fires (reported: Team 39 / 33 pinning on a body after a failed loot and never moving). It
            // never triggers mid-combat — the objective is a CombatCaller waypoint then, not a Corpse.
            {
                var corpseObj = squadObjective.Location;
                if (corpseObj != null && corpseObj.Category == WaypointCategory.Corpse)
                {
                    if (squad.CorpseWatchdogLocId != corpseObj.Id)
                    {
                        squad.CorpseWatchdogLocId = corpseObj.Id;
                        squad.CorpseWatchdogSince = Time.time;
                    }
                    else if (SquadAnyMemberInCombat(squad))
                    {
                        // Pause the clock while a member is in SAIN combat / healing (IsActive=false → ORBIT
                        // can't move anyone toward the body). Otherwise a bot that kills, gets dragged into
                        // another fight right beside the corpse, and only frees up ~25s later has its OWN kill
                        // blacklisted before it ever approaches (observed: roman killed Bot17, fought + killed
                        // Bot9 next to it, the watchdog blacklisted Bot17 mid-fight and the body was abandoned).
                        squad.CorpseWatchdogSince = Time.time;
                    }
                    else if (Time.time - squad.CorpseWatchdogSince > CorpseStuckTimeoutSeconds)
                    {
                        squad.CompletedPoiIds.Add(corpseObj.Id);
                        squad.PreInterruptObjectiveLocation = null; // drop any stale opportunistic resume so it can't re-pin the corpse
                        squad.CorpseWatchdogLocId = -1;
                        Log.Info($"{squad} corpse-stuck watchdog: blacklisting {corpseObj} after {CorpseStuckTimeoutSeconds:F0}s glued to it without completing — re-dispatching");
                        AssignNewObjective(squad);
                        continue;
                    }
                }
                else
                {
                    squad.CorpseWatchdogLocId = -1;
                }
            }

            // Rolling per-squad unreachability refresh. _squadUnreachable is populated from the leader's
            // CURRENT position via NavMesh.CalculatePath; verdicts cached when the leader was
            // 500m away become wrong once they walk into the area. On
            // every cell transition we re-evaluate the 3x3 window around the leader's new cell, gated by a
            // per-cell cooldown so oscillating leaders don't thrash the cache.
            RefreshUnreachabilityAroundLeader(squad);

            // Tick main objectives (Kills timer / LootValue completion / extract trigger when all done).
            // Independent of the per-squad objective dispatch flow below — mains drive the long-term force
            // attraction, the dispatch picks tick-level secondary POIs.
            TickMainObjectives(squad);

            // Squad rally: scan members for combat EVERY tick (any squad, any objective, gated only by the
            // SquadRally toggle). The first member to take fire / engage becomes the "caller" and
            // squad.Objective.Location is swapped to a virtual Waypoint at their position so realign converges
            // everyone there to support. No SAIN handoff — ORBIT just routes the supporters toward the
            // contact; SAIN's combat layer (priority 20 > ORBIT 19) preempts each one as it acquires an enemy.
            // Grace keeps the override stable through brief LoS breaks; when it lapses the squad picks a fresh
            // objective and resumes normal play.
            if (Plugin.SquadRally.Value)
            {
                DetectAndUpdateCombatCaller(squad);
                if (squad.CombatCallerMemberIdx >= 0)
                {
                    // Refresh the virtual override each tick — cheap, and keeps squad.Objective.Location
                    // pointing at the (moving) caller even if other code mutated it.
                    squad.Objective.Location = waypointSystem.CreateVirtualWaypoint(
                        squad.CombatCallerPosition, "CombatCaller");
                    squad.Objective.Status = SquadObjectiveState.Active;
                    squad.Objective.StartTime = Time.time;
                    squad.Objective.Duration = 30f;
                    squad.Objective.DurationAdjusted = false;
                }
            }
            else if (squad.CombatCallerMemberIdx >= 0)
            {
                // Rally toggled off mid-raid — clear any active caller defensively.
                squad.CombatCallerMemberIdx = -1;
            }

            CheckTimeExtractTrigger(squad);

            // Opportunistic corpse interrupt: any squad member who sees an unlooted corpse within
            // DetectCorpseDistance drops the current objective to investigate. Real-Tarkov behaviour — a bot
            // walking past a body always checks it. We gate the scan on:
            //   * squad is not already on a Corpse objective
            //   * squad is not already mid-interrupt
            //   * squad has not flipped ExtractRequested (extract bee-line
            // beats opportunistic loot — they've decided to leave)
            //   * pacing throttle elapsed (the raycast loop is non-trivial)
            //
            // When we DO interrupt, we save the current objective only if it's worth resuming — Synthetic
            // patrol fillers get cleared so the post-loot flow runs a fresh AssignNewObjective.
            if (!squad.ExtractRequested
                && squad.CombatCallerMemberIdx < 0
                && squad.PreInterruptObjectiveLocation == null
                && (squadObjective.Location == null || squadObjective.Location.Category != WaypointCategory.Corpse)
                && Time.time >= squad.LastOpportunisticCorpseScanTime + Plugin.OpportunisticCorpseScanIntervalSeconds.Value)
            {
                squad.LastOpportunisticCorpseScanTime = Time.time;
                var opportunistic = waypointSystem.TryFindOpportunisticCorpse(squad);
                if (opportunistic != null)
                {
                    var previous = squadObjective.Location;
                    var preInterrupt = previous != null
                                       && previous.Category != WaypointCategory.Synthetic
                                       ? previous
                                       : null;
                    squad.PreInterruptObjectiveLocation = preInterrupt;
                    squadObjective.LocationPrevious = previous;
                    squadObjective.Location = opportunistic;
                    squadObjective.Status = SquadObjectiveState.Active;
                    ShufflePickCoverPoints(squadObjective, Math.Max(squad.TargetMembersCount, squad.Size));
                    ResetDuration(squadObjective, _moveTimeout.SampleGaussian());
                    Log.Info($"{squad} opportunistic-corpse interrupt: spotted {opportunistic}, was on {previous} (resume={(preInterrupt != null ? "yes" : "no — synthetic or null")})");
                    continue; // re-enter on next tick; UpdateAgents will realign members
                }
            }

            if (squadObjective.Location == null)
            {
                // Honour an explicit post-rescue (or other) cooldown set by a patch that nulled the location:
                // if the wait window hasn't elapsed yet, don't immediately hand the squad a new POI. Without
                // this gate, the rescue-prune cooldown gets bypassed and the rescued bot is sent chasing the
                // next POI before BSG re-anchors it on the navmesh.
                if (squadObjective.Status == SquadObjectiveState.Wait
                    && Time.time < squadObjective.StartTime + squadObjective.Duration)
                {
                    continue;
                }
                Log.Debug($"{squad} objective is null, requesting new assignment");
                AssignNewObjective(squad);
                continue;
            }

            var finishedCount = UpdateAgents(squad);

            if (finishedCount == squad.Size)
            {
                if (squadObjective.Status == SquadObjectiveState.Active)
                {
                    // Every member's path attempt came back Failed without anyone reaching the POI. After
                    // enough in a row the POI is effectively unreachable for this squad — blacklist it so the
                    // next pick goes somewhere else.
                    //
                    // Previously this branch hard-teleported the squad to the POI's navmesh sample point.
                    // That created two failure modes:
                    //  - TP dropped the bot inside locked rooms (Resort /
                    // Sanatorium navmesh interiors had a sample point even though the door was a one-way
                    // breach), bot stuck inside for the rest of the raid.
                    //  - On Kills mains anchored at config zone centers
                    // sitting in walls / cliffs / water, the navmesh sample failed → TP fall back to "normal
                    // dispatch" → en-route fail again → infinite TP attempt loop. Drop the teleport entirely.
                    // Force-unlock probability rolls on locked doors stay intact via the door- handling path
                    // in MovementSystem.
                    squad.ConsecutiveFailedDispatches++;
                    if (squad.ConsecutiveFailedDispatches >= UnreachableBlacklistThreshold
                        && squadObjective.Location != null
                        && squadObjective.Location.Category != WaypointCategory.Exfil)
                    {
                        squad.CompletedPoiIds.Add(squadObjective.Location.Id);
                        Log.Info($"{squad} blacklisting unreachable {squadObjective.Location} after {UnreachableBlacklistThreshold} consecutive en-route failures (squad memory size={squad.CompletedPoiIds.Count})");
                        squad.ConsecutiveFailedDispatches = 0;
                        AssignNewObjective(squad);
                        continue;
                    }
                    Log.Debug($"{squad} all members failed their objective en-route, requesting new assignment (streak={squad.ConsecutiveFailedDispatches})");
                    AssignNewObjective(squad);
                    continue;
                }

                if (!squadObjective.DurationAdjusted)
                {
                    switch (squadObjective.Location.Category)
                    {
                        case WaypointCategory.ContainerLoot:
                        case WaypointCategory.LooseLoot:
                        case WaypointCategory.Corpse:
                            // The looting pipeline already takes 6-25s per POI to simulate inspection /
                            // pickup. No need to add a guard wait on top — that just makes squads stand
                            // around at empty containers. Force the timer to expire immediately.
                            AdjustDuration(squadObjective, 0f, Time.time);
                            Log.Debug($"{squad} skipping guard wait at loot POI {squadObjective.Location} (loot action already provided its own delay)");
                            break;
                        case WaypointCategory.Quest:
                            // Quest triggers still benefit from a short guard wait (no inner delay).
                            AdjustDuration(squadObjective, squadObjective.Duration * _guardDurationCut.SampleGaussian());
                            Log.Debug($"{squad} adjusted {squadObjective.Location} wait duration to {squadObjective.Duration}");
                            break;
                        case WaypointCategory.Synthetic:
                            // Bot scavs (assault / assaultGroup, not PlayerScavs) get a much longer
                            // randomised idle pause at a Synthetic — vanilla scavs frequently stop and stand
                            // around their patrol points for a minute or more, while ours were chaining
                            // Synthetic→Synthetic with only a 3-7s pause. PMCs / raiders / bosses /
                            // PlayerScavs keep the short pause so they stay aggressive on their patrol path.
                            //
                            // PlayerScavs share WildSpawnType.assault with bot scavs so IsScav() can't
                            // distinguish them — the canonical detection is the Profile.WillBeAPlayerScav
                            // extension.
                            var leaderBotAtSynthetic = squad.Leader?.Bot;
                            var roleAtSynthetic = leaderBotAtSynthetic?.Profile?.Info?.Settings?.Role;
                            var isBotScavAtSynthetic = roleAtSynthetic.HasValue
                                && roleAtSynthetic.Value.IsScav()
                                && leaderBotAtSynthetic?.Profile != null
                                && !leaderBotAtSynthetic.Profile.WillBeAPlayerScav();
                            if (isBotScavAtSynthetic)
                            {
                                AdjustDuration(squadObjective, _scavSyntheticIdleDuration.SampleGaussian(), Time.time);
                                Log.Debug($"{squad} scav idle pause at {squadObjective.Location} for {squadObjective.Duration:F1}s");
                            }
                            else
                            {
                                AdjustDuration(squadObjective, _adjustedGuardDuration.SampleGaussian(), Time.time);
                                Log.Debug($"{squad} adjusted {squadObjective.Location} wait duration to {squadObjective.Duration}");
                            }
                            break;
                        case WaypointCategory.Exfil:
                        default:
                            break;
                    }
                }
            }

            if (Time.time < squadObjective.StartTime + squadObjective.Duration)
                continue;

            Log.Debug($"{squad} wait timer ran out, requesting new assignment");
            AssignNewObjective(squad);
        }
    }

    // Tracks splinter waypoint ids assigned earlier in the same UpdateAgents pass so two followers in the
    // same squad don't get handed the same POI.
    private readonly HashSet<int> _splinterScratch = new(8);

    // Reusable union buffer for splinter-dispatch agent-skip filtering.
    private readonly HashSet<int> _agentSkipScratch = new(16);

    /// <summary>
    /// Combines the shared splinter scratch with the agent's personal value-skip set. Returns the base set
    /// unchanged when the agent has no skips to add.
    /// </summary>
    private HashSet<int> UnionWithAgentSkips(HashSet<int> baseSet, Agent agent)
    {
        var skips = agent?.ValueSkippedPoiIds;
        if (skips == null || skips.Count == 0) return baseSet;
        _agentSkipScratch.Clear();
        foreach (var id in baseSet) _agentSkipScratch.Add(id);
        foreach (var id in skips) _agentSkipScratch.Add(id);
        Log.Debug($"{agent} splinter dispatch: filtering +{skips.Count} agent value-skipped POIs (total exclude={_agentSkipScratch.Count})");
        return _agentSkipScratch;
    }

    // Body parts summed for the emergency-extract HP check.
    private static readonly EBodyPart[] _emergencyHpParts =
    {
        EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
        EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg
    };

    // HP-trend emergency-extract tuning (BSG-independent). Two triggers: (1) ACTIVE DECLINE — HP is ≥MinDrop
    // below where it was DropWindow seconds ago AND is STILL falling across two consecutive recent sub-windows
    // (a genuine ongoing bleed, not a single hit that then stabilised); (2) STAGNANT LOW — HP sat below
    // StagnantLowFraction for StagnantLowSeconds without climbing out (crippled, out of meds, sitting wounded).
    // Either way, cancel once HP climbs RecoverFraction back above its low.
    private const float EmergencyDropWindowSeconds = 8f;
    private const float EmergencyMinDropFraction = 0.12f;
    private const float EmergencyRecoverFraction = 0.10f;
    private const float EmergencyFallSubWindowSeconds = 2.5f;
    private const float EmergencyFallEps = 0.01f;
    // If this method didn't run for longer than this (the bot was in SAIN combat / inactive, so HP wasn't
    // sampled), the rolling buffer holds stale pre-gap HP — drop it and rebuild, so active-decline never reads
    // a pre-combat 100% sample as "8s ago" and mistakes post-combat damage for a fresh bleed.
    private const float EmergencyMaxSampleGapSeconds = 2f;
    private const float EmergencyStagnantLowFraction = 0.50f;
    private const float EmergencyStagnantLowSeconds = 60f;

    /// <summary>Only PMCs and PlayerScavs extract via exfils in ORBIT; everyone else despawns / leaves on a
    /// script, so a solo exfil run is meaningless for them. Gates the emergency trigger to avoid churning
    /// FindNearestEligibleExfil for factions that can never get a result.</summary>
    private static bool CanSoloExtract(Agent agent)
    {
        var profile = agent.Bot?.Profile;
        var role = profile?.Info?.Settings?.Role;
        if (!role.HasValue) return false;
        return role.Value.IsPMC() || profile.WillBeAPlayerScav();
    }

    /// <summary>
    /// HP-trend emergency extract. Samples the bot's own HP each decision tick (no BSG Medecine/FirstAid
    /// dependency — those flags didn't reliably catch a bleeding, med-less bot, which then died picking POIs
    /// instead of extracting). Two triggers: an ACTIVE DECLINE (HP dropped ≥12% below a trailing reference and
    /// stayed down ≥5s — a fast bleed) and a STAGNANT LOW (HP stuck below 50% for a full minute without
    /// climbing out — crippled, out of meds). If HP climbs back up while the emergency extract is active (it
    /// healed), cancel it so the bot rejoins the squad — a bot that DOES have meds naturally heals → HP rises →
    /// cancel, so no explicit med check is needed.
    ///
    /// Active decline is read off a rolling HP-sample buffer, NOT a high-water mark: it fires only when HP is
    /// ≥12% below where it was DropWindow seconds ago AND is still actively falling across two consecutive
    /// recent sub-windows. That two-window check is the fix for the false positives where a single hit dropped
    /// HP once and it then sat STABLE (e.g. "HP at 81% down from 100%" while parked at 81%) — a single hit fills
    /// only one sub-window, a real bleed keeps dropping across both, so only the latter triggers.
    /// </summary>
    private static void UpdateEmergencyExtract(Agent agent)
    {
        if (Orbit.Looting.LootConfig.EmergencyExtractEnabled is { Value: false }) return;
        if (!CanSoloExtract(agent)) return;
        var cur = HpFraction(agent);

        // Already emergency-extracting: cancel if HP recovered since its low; otherwise track the new low.
        if (agent.SoloExtractRequested && agent.SoloExtractIsEmergency)
        {
            if (cur >= agent.EmergencyHpLow + EmergencyRecoverFraction)
            {
                agent.SoloExtractRequested = false;
                agent.SoloExtractIsEmergency = false;
                agent.SoloExtractReason = null;
                agent.SoloExtractTarget = null;
                agent.EmergencyHpRef = cur;
                agent.EmergencyHpRefTime = Time.time;
                agent.EmergencyLowSince = -1f;
                Log.Info($"{agent} emergency extract cancelled — HP recovered to {cur:P0}, rejoining squad");
                return;
            }
            agent.EmergencyHpLow = Mathf.Min(agent.EmergencyHpLow, cur);
            return;
        }

        // Stagnant-low timer: how long the bot has been continuously below the low-HP floor. Reset the moment
        // it climbs back out.
        if (cur < EmergencyStagnantLowFraction)
        {
            if (agent.EmergencyLowSince < 0f) agent.EmergencyLowSince = Time.time;
        }
        else
        {
            agent.EmergencyLowSince = -1f;
        }

        // Record this HP sample into the agent's rolling history for trend detection. If sampling stalled (the
        // bot was in SAIN combat / inactive, so this method didn't run), the buffer holds stale pre-gap HP —
        // discard it so active-decline only ever reads a continuous recent run, never a pre-combat 100% sample
        // treated as "8s ago" (that made post-combat damage look like a fresh 8s bleed and false-triggered the
        // extract on bots that were actually stable post-fight).
        var now = Time.time;
        if (agent.EmergencyHpHistCount > 0)
        {
            var prevSlot = (agent.EmergencyHpHistCount - 1) % agent.EmergencyHpHist.Length;
            if (now - agent.EmergencyHpHistTime[prevSlot] > EmergencyMaxSampleGapSeconds)
                agent.EmergencyHpHistCount = 0;
        }
        var slot = agent.EmergencyHpHistCount % agent.EmergencyHpHist.Length;
        agent.EmergencyHpHist[slot] = cur;
        agent.EmergencyHpHistTime[slot] = now;
        agent.EmergencyHpHistCount++;

        // Active decline: HP is ≥MinDropFraction below where it was DropWindowSeconds ago AND is still falling
        // across BOTH the older and the most-recent sub-window. The two-window "still falling" test is what
        // rejects a single hit that then stabilised (the hit lands in one sub-window only; the other reads flat),
        // while a genuine bleed keeps dropping across both. Needs DropWindowSeconds of history first.
        var hpWindowAgo = HpFromAtLeastAgo(agent, now, EmergencyDropWindowSeconds);
        var hpMidAgo = HpFromAtLeastAgo(agent, now, EmergencyFallSubWindowSeconds * 2f);
        var hpRecentAgo = HpFromAtLeastAgo(agent, now, EmergencyFallSubWindowSeconds);
        var activeDecline = hpWindowAgo >= 0f && hpMidAgo >= 0f && hpRecentAgo >= 0f
                            && hpWindowAgo - cur >= EmergencyMinDropFraction   // ≥12% lower than DropWindow ago
                            && hpMidAgo - hpRecentAgo >= EmergencyFallEps      // fell during the older sub-window
                            && hpRecentAgo - cur >= EmergencyFallEps;          // and still falling in the latest one
        var stagnantLow = agent.EmergencyLowSince >= 0f
                          && Time.time - agent.EmergencyLowSince >= EmergencyStagnantLowSeconds;

        if (agent.SoloExtractRequested || (!activeDecline && !stagnantLow)) return;

        agent.SoloExtractRequested = true;
        agent.SoloExtractIsEmergency = true;
        agent.EmergencyHpLow = cur;
        agent.EmergencyExtractRequestedAt = Time.time;
        agent.EmergencyExtractStillSince = Time.time;
        agent.EmergencyExtractLastPos = agent.Position;
        agent.EmergencyExtractLastHp = cur;
        if (activeDecline)
        {
            agent.SoloExtractReason = $"emergency (HP {cur:P0}, dropping with no recovery)";
            Log.Info($"{agent} emergency extract — HP at {cur:P0} (down from {hpWindowAgo:P0} over the last {EmergencyDropWindowSeconds:F0}s, still falling), bee-lining to exfil");
        }
        else
        {
            agent.SoloExtractReason = $"emergency (HP {cur:P0}, stuck below 50% with no recovery)";
            Log.Info($"{agent} emergency extract — HP stuck at {cur:P0} below 50% for {Time.time - agent.EmergencyLowSince:F0}s with no recovery, bee-lining to exfil");
        }
    }

    /// <summary>True once any squad member has run a loot session on this corpse and value-skipped it (i.e.
    /// left sub-threshold loot for softer-gated teammates). Before that the corpse is FRESH and only the killer
    /// should approach; after, a Rat-tier member is allowed to clean up the leftovers — so the anti-pile null
    /// must NOT fire. Keeps the Chad-loots-then-Rat-cleans-up flow intact.</summary>
    private static bool CorpseValueSkippedByAnyMember(Squad squad, int locId)
    {
        if (squad?.Members == null) return false;
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var m = squad.Members[i];
            if (m != null && m.ValueSkippedPoiIds.Contains(locId)) return true;
        }
        return false;
    }

    /// <summary>True if any ALIVE squad member is currently outside ORBIT control (IsActive=false → SAIN combat
    /// / healing has the bot). Used to pause the corpse-stuck watchdog: time spent fighting beside a body
    /// shouldn't count against reaching it.</summary>
    private static bool SquadAnyMemberInCombat(Squad squad)
    {
        if (squad?.Members == null) return false;
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var m = squad.Members[i];
            if (m != null && !m.IsActive && m.Player?.HealthController is { IsAlive: true }) return true;
        }
        return false;
    }

    /// <summary>Most recent HP sample in the agent's rolling history that is at least <paramref name="ago"/>
    /// seconds old, or -1 if the history doesn't reach back that far yet.</summary>
    private static float HpFromAtLeastAgo(Agent agent, float now, float ago)
    {
        var target = now - ago;
        var best = -1f;
        var bestTime = -1f;
        var n = Mathf.Min(agent.EmergencyHpHistCount, agent.EmergencyHpHist.Length);
        for (var i = 0; i < n; i++)
        {
            var t = agent.EmergencyHpHistTime[i];
            if (t <= target && t > bestTime)
            {
                bestTime = t;
                best = agent.EmergencyHpHist[i];
            }
        }
        return best;
    }

    internal static float HpFraction(Agent agent)
    {
        var hc = agent.Player?.HealthController;
        if (hc == null || !hc.IsAlive) return 1f;
        float cur = 0f, max = 0f;
        for (var i = 0; i < _emergencyHpParts.Length; i++)
        {
            var h = hc.GetBodyPartHealth(_emergencyHpParts[i]);
            cur += h.Current;
            max += h.Maximum;
        }
        return max > 0f ? cur / max : 1f;
    }

    /// <summary>
    /// Degraded-tickrate gate: true when this squad should SKIP its decision loop this strategy tick. A squad
    /// within DegradedTickrateNearDistance of any living human always runs (keep it crisp where it's visible);
    /// a squad beyond that re-decides only every DegradedTickrateFarIntervalSeconds. Movement still runs every
    /// frame in MovementSystem, so a deferred squad keeps executing its current path/objective.
    /// </summary>
    private bool ShouldDeferDecisionTick(Squad squad)
    {
        var leader = squad.Leader?.Bot;
        if (leader == null) return false;
        var near = Plugin.DegradedTickrateNearDistance.Value;
        var far = waypointSystem.NearestHumanDistanceSqr(leader.Position) > near * near;
        // Log only the near<->far transition (once each) so the dashboard can confirm throttling engaged,
        // without spamming a line every deferred tick.
        if (far != squad.DecisionThrottled)
        {
            squad.DecisionThrottled = far;
            Log.Info(far
                ? $"{squad} degraded tickrate ON — far from all players, re-deciding every {Plugin.DegradedTickrateFarIntervalSeconds.Value:F0}s"
                : $"{squad} degraded tickrate OFF — back near a player, full-rate decisions");
        }
        if (!far) return false;
        return Time.time - squad.LastDecisionTickTime < Plugin.DegradedTickrateFarIntervalSeconds.Value;
    }

    private int UpdateAgents(Squad squad)
    {
        var squadObjective = squad.Objective;
        var finishedCount = 0;
        _splinterScratch.Clear();

        // Independent-dispatch mode: each member picks their own roam splinter (extended radius + category
        // mask) instead of all converging on the squad anchor. Triggered during Kills roam and LootValue
        // active phases, but disabled when a combat caller is set (everyone converges on the caller instead),
        // AND disabled once the squad has decided to extract — letting members pick splinter loot on the way
        // to the exfil delays the whole extract chain indefinitely.
        var useRoam = ShouldUseIndependentDispatch(squad)
                      && squad.CombatCallerMemberIdx < 0
                      && !squad.ExtractRequested;
        // Anchor for the roam splinter search: the active main's Position (Kills zone centre / LootValue cell
        // centre). Without this, the splinter radius would be centred on each bot's drifting current position
        // and they'd wander out of the zone over a few re-picks.
        var activeMain = useRoam ? ActiveIndependentMain(squad) : null;
        var activeType = activeMain?.Type;
        // Per-type category mask. Kills roam = wide net (loot + corpse + synthetic) so members "look for
        // action"; LootValue = loot + corpse only.
        var roamLooseLoot = useRoam;
        var roamContainerLoot = useRoam;
        var roamCorpse = useRoam;
        var roamSynthetic = useRoam && activeType == MainObjectiveType.Kills;

        for (var i = 0; i < squad.Size; i++)
        {
            var agent = squad.Members[i];
            var agentObjective = agent.Objective;

            // During a combat-caller window, skip every member who is themselves engaged — the caller AND
            // any other in-combat member. Reasons:
            //  * Caller: squad.Objective is a virtual CombatCaller waypoint AT his own position, refreshed
            //    every tick. Realigning would assign him a waypoint at his own feet → "reached" immediately
            //    → reassign next tick, ad infinitum. SAIN's combat layer is driving him; leave his agent
            //    objective alone.
            //  * Other in-combat members: each has their own enemy and SAIN sequence in flight. Realigning
            //    to the caller's position would queue a stale destination behind their SAIN combat — when
            //    SAIN finally exits, ORBIT would route them to the caller's spot where they may also be
            //    self-pinned by SAIN's sticky HaveEnemy. Only members NOT in combat are eligible supporters.
            if (squad.CombatCallerMemberIdx == i) continue;
            var memberBot = agent.Bot;
            if (squad.CombatCallerMemberIdx >= 0
                && memberBot?.Memory != null
                && (memberBot.Memory.HaveEnemy || memberBot.Memory.IsUnderFire))
            {
                continue;
            }

            // Solo / emergency extract. EMERGENCY: a wounded member with no usable meds left peels off to
            // extract on its own. (The LOOT-threshold trigger is set elsewhere, in LootContainerAction.) When
            // SoloExtractRequested, route the member straight to its exfil and skip the squad alignment +
            // splinter logic so the rest of the squad keeps playing. GotoObjectiveAction's exfil-arrival
            // handler then flips it to Extracting and ExtractAction despawns it — no squad-wide
            // ExtractRequested needed.
            // HP-trend emergency extract (trigger if bleeding out + no recovery, cancel if HP climbs back).
            UpdateEmergencyExtract(agent);
            if (agent.SoloExtractRequested)
            {
                if (agent.SoloExtractTarget == null)
                    agent.SoloExtractTarget = waypointSystem.FindNearestEligibleExfil(squad);
                if (agent.SoloExtractTarget != null)
                {
                    // Keep the bee-line ALIVE — don't point it just once. The move order is otherwise issued a
                    // single time (only when Location changes); a SAIN-combat detour drops the destination
                    // (OrbitBrainLayer hand-off calls SetPlayerToNavMesh) and an arrival stall flips the
                    // objective to Failed, either of which strands the bot near the exfil with no fresh move and
                    // no way back into the arrival→Extracting handler. Re-arm whenever it isn't already moving
                    // to / extracting at the exfil.
                    var firstDispatch = agentObjective.Location != agent.SoloExtractTarget;
                    var stalled = !firstDispatch
                                  && agentObjective.Status != ObjectiveStatus.Moving
                                  && agentObjective.Status != ObjectiveStatus.Extracting;
                    if (firstDispatch || stalled)
                    {
                        var prevStatus = agentObjective.Status;
                        agentObjective.Location = agent.SoloExtractTarget;
                        agentObjective.SplinterParent = null;
                        agentObjective.Status = ObjectiveStatus.None;
                        agentObjective.DispatchTime = Time.time;
                        if (firstDispatch)
                            Log.Info($"{agent} solo extract ({agent.SoloExtractReason}) → bee-lining to {agent.SoloExtractTarget}");
                        else
                            Log.Debug($"{agent} solo extract re-arming bee-line to {agent.SoloExtractTarget} (was {prevStatus} — lost its move order)");
                    }
                    // Count the departing member as "finished" w.r.t. squad objectives so the squad keeps
                    // advancing (finishedCount == squad.Size) instead of stalling until this member despawns.
                    finishedCount++;
                    continue;
                }
                // No eligible exfil for this faction / map → can't solo extract. Drop it and rejoin the squad.
                agent.SoloExtractRequested = false;
                agent.SoloExtractReason = null;
            }

            // An agent is "aligned" with the squad if their location IS the squad's main objective, OR if
            // they're working a splinter that was picked around the squad's current main objective. Without
            // the SplinterParent check, followers on a splinter would look misaligned every tick and get
            // re-dispatched in a loop.
            //
            // Exception: an agent whose splinter is already done (loot succeeded → Finished, or arrival kept
            // failing → Failed) is treated as misaligned so UpdateAgents picks a FRESH splinter. Without
            // this, the agent freezes on the completed splinter and the squad anchor's surrounding POIs never
            // get worked through.
            var splinterAlreadyDone = agentObjective.SplinterParent != null
                                      && (agentObjective.Status == ObjectiveStatus.Finished
                                          || agentObjective.Status == ObjectiveStatus.Failed);
            // Roam continuation for the leader: anchor-first parks i=0 on the squad anchor, and
            // "Location == squad objective" keeps them aligned forever once Finished — the leader stood
            // guarding beside his completed patrol point for a minute while followers walked out their
            // splinters (observed during Kills roam). During roam, a leader who has
            // FINISHED the anchor is treated as misaligned so he falls into the splinter branches like
            // everyone else; followers' targets are untouched.
            var leaderFinishedAnchorInRoam = i == 0 && useRoam
                                             && agentObjective.SplinterParent == null
                                             && agentObjective.Location != null
                                             && agentObjective.Location == squadObjective.Location
                                             && agentObjective.Status == ObjectiveStatus.Finished;
            // Cross-anchor sticky: when the squad anchor flips (current main completed, leader switched
            // targets, etc.), keep every member on their CURRENT splinter until they physically reach it or it
            // gets blacklisted. The previous radius gate (only sticky if the splinter sits within
            // SplinterSearchRadius of the new anchor) was too aggressive — it ripped members off live loot
            // routes mid-run as soon as the leader moved on, producing the rapid "switch without reaching"
            // pattern. Each agent now runs their own splinter pipeline independently: arrive → finish → pick
            // next; squadmates don't synchronise on the leader's progress.
            var splinterStickyAcrossAnchor = !splinterAlreadyDone
                                             && agentObjective.SplinterParent != null
                                             && agentObjective.Location != null
                                             && squadObjective.Location != null
                                             && agentObjective.SplinterParent != squadObjective.Location
                                             && !squad.CompletedPoiIds.Contains(agentObjective.Location.Id);
            var aligned = !splinterAlreadyDone && !leaderFinishedAnchorInRoam
                          && (agentObjective.Location == squadObjective.Location
                              || (agentObjective.SplinterParent != null
                                  && agentObjective.SplinterParent == squadObjective.Location)
                              || splinterStickyAcrossAnchor);
            if (leaderFinishedAnchorInRoam)
            {
                Log.Debug($"{agent} leader roam continuation: finished anchor {agentObjective.Location}, picking a splinter instead of guarding");
                // Don't roam off while we still have another of our OWN kills unlooted nearby. A double-kill
                // arms the single PendingOwnKill direct-route slot for only the LATEST corpse, so the earlier
                // kill stays tagged but is never re-picked here — roam splinters bypass RequestNear's own-kill
                // pre-scan, so the leader wanders off and abandons the first body (observed: a bot got 2 kills
                // but only looted the second). Force a squad re-dispatch so RequestNear's pre-scan re-anchors
                // onto the remaining tagged corpse before we wander away.
                if (waypointSystem.TryPickOwnKillCorpse(squad) != null)
                {
                    squad.Objective.Duration = 0;
                    Log.Info($"{agent} finished an own-kill corpse but another tagged own-kill is still unlooted nearby — forcing squad re-anchor onto it before roaming");
                }
            }

            if (aligned && agentObjective.Location != null)
            {
                // Track existing splinters so we don't hand the same POI to another follower below.
                if (agentObjective.SplinterParent != null)
                    _splinterScratch.Add(agentObjective.Location.Id);
                // Rebase SplinterParent to the squad's current anchor so the next tick aligns via the
                // standard path.
                if (splinterStickyAcrossAnchor)
                {
                    Log.Debug($"{agent} splinter sticky across anchor flip: kept {agentObjective.Location}, rebased parent {agentObjective.SplinterParent} → {squadObjective.Location}");
                    agentObjective.SplinterParent = squadObjective.Location;
                }
                // Reset Failed back to None so Goto picks the agent back up and re-submits a move order.
                // Without this, an Exfil dispatch that stops short (partial nav-path 400m+ off an exfil)
                // leaves the agent pinned at Status=Failed forever: ExtractRequested keeps re-confirming the
                // SAME exfil reference each tick, alignment stays true, and no per-agent re-assignment fires
                // to clear the failed flag. Agent never tries to move again.
                if (agentObjective.Status == ObjectiveStatus.Failed)
                    agentObjective.Status = ObjectiveStatus.None;
            }

            if (!aligned)
            {
                // Dispatch priority:
                //   1. Own-kill direct: the specific agent who landed the
                // fresh corpse kill goes straight to that corpse, not a random splinter around it. Cleared
                // after first use.
                //   2. Anchor-first for the leader (i=0): exactly one
                // member works the anchor itself, others get splinters. Solo squads naturally end up here
                // too, so the bot loots the anchor before its splinters. Falls through to the splinter branch
                // once the anchor is in CompletedPoiIds.
                //   3. Roam splinter (members on Kills/LootValue in
                // progress, non-killer non-leader).
                //   4. Loot splinter (non-roam followers).
                //   5. Fallback to squad anchor (no splinter found).
                Waypoint targetLoc;
                Waypoint splinterParent;
                var tookOwnKillCorpse = false;
                var ownKillAgentId = squad.PendingOwnKillKillerAgentId;
                var anchorReservedForOwnKill = ownKillAgentId >= 0
                                               && squadObjective.Location != null
                                               && squadObjective.Location.Id == squad.PendingOwnKillCorpseLocId;
                if (anchorReservedForOwnKill && agent.Id == ownKillAgentId)
                {
                    targetLoc = squadObjective.Location;
                    splinterParent = null;
                    tookOwnKillCorpse = true;
                    squad.PendingOwnKillKillerAgentId = -1;
                    squad.PendingOwnKillCorpseLocId = 0;
                    Log.Debug($"{agent} own-kill direct-route to {targetLoc} (skipped splinter)");
                }
                else if (i == 0
                         && !anchorReservedForOwnKill
                         && squadObjective.Location != null
                         && !squad.CompletedPoiIds.Contains(squadObjective.Location.Id)
                         && !agent.ValueSkippedPoiIds.Contains(squadObjective.Location.Id)
                         // A synthetic anchor still under its visit cooldown was just patrolled (by this
                         // leader, typically — the roam-continuation path lands here right after his
                         // Finished). Re-taking it would ping-pong anchor ↔ splinter every other pick;
                         // roam a splinter instead until the cooldown lapses.
                         && !(squadObjective.Location.Category == WaypointCategory.Synthetic
                              && squad.RecentlyVisitedPoiCooldowns.TryGetValue(squadObjective.Location.Id, out var anchorVisitExpiry)
                              && Time.time < anchorVisitExpiry))
                {
                    // Leader takes the anchor itself. Falls through to the splinter branches when the anchor
                    // is squad-blacklisted or in the leader's personal value-skip set.
                    targetLoc = squadObjective.Location;
                    splinterParent = null;
                }
                else if (useRoam)
                {
                    // Drift-libre by default: search centred on the bot's current position so they can
                    // naturally wander up to ~50m at each re-pick. Leash: if the bot has drifted further than
                    // the search radius from the active Main's anchor (e.g. chased an enemy out of a Kills
                    // zone), swap the search centre to the anchor so the next pick snaps them back.
                    var roamRadius = Plugin.MainObjectivesRoamSplinterRadius.Value;
                    var searchCenter = agent.Position;
                    if (activeMain != null
                        && WaypointSystem.XzDistanceSqr(agent.Position, activeMain.Position) > roamRadius * roamRadius)
                    {
                        // XZ-only: activeMain.Position.Y is 0 for LootValue mains (CellToWorld) and
                        // custom-zone Kills mains, so 3D distance would inflate by the vertical mismatch with
                        // the agent's real Y.
                        searchCenter = activeMain.Position;
                    }
                    var excludeForRoam = UnionWithAgentSkips(_splinterScratch, agent);
                    var roamSplinter = waypointSystem.FindRoamSplinterForMember(
                        agent.Position, searchCenter, squad, excludeForRoam, roamRadius,
                        roamLooseLoot, roamContainerLoot, roamCorpse, roamSynthetic);
                    if (roamSplinter != null)
                    {
                        targetLoc = roamSplinter;
                        splinterParent = squadObjective.Location;
                        _splinterScratch.Add(roamSplinter.Id);
                    }
                    else
                    {
                        targetLoc = squadObjective.Location;
                        splinterParent = null;
                    }
                }
                else if (squadObjective.Location == null)
                {
                    targetLoc = null;
                    splinterParent = null;
                }
                else
                {
                    var excludeForLoot = UnionWithAgentSkips(_splinterScratch, agent);
                    var splinter = waypointSystem.FindLootSplinterForFollower(
                        squadObjective.Location, squad, excludeForLoot,
                        squad.Personality != null
                            ? squad.Personality.SplinterSearchRadius
                            : Plugin.SplinterSearchRadius.Value);
                    if (splinter != null)
                    {
                        targetLoc = splinter;
                        splinterParent = squadObjective.Location;
                        _splinterScratch.Add(splinter.Id);
                    }
                    else
                    {
                        targetLoc = squadObjective.Location;
                        splinterParent = null;
                    }
                }

                // A FRESH body has a single claim — only the own-kill killer (routed via the branch above)
                // targets a Corpse SQUAD anchor directly. Any other member that landed on it through the
                // leader-anchor or splinter fallback would just pile on and fail the claim, so guard in place
                // instead (observed: all 3 squad members converging on one kill). The null is gated on the
                // corpse being FRESH: once a member has actually run a loot session and value-skipped it
                // (left sub-threshold loot for softer-gated teammates), a Rat-tier member is meant to come back
                // and clean up the leftovers, so we must NOT block that. A Corpse picked as a SPLINTER (a
                // different body) is also left untouched — only the squad-anchor corpse is single-claimed here.
                if (!tookOwnKillCorpse && targetLoc != null
                    && targetLoc.Category == WaypointCategory.Corpse
                    && squadObjective.Location != null && targetLoc.Id == squadObjective.Location.Id
                    && !CorpseValueSkippedByAnyMember(squad, targetLoc.Id))
                {
                    targetLoc = null;
                    splinterParent = null;
                }

                agentObjective.Location = targetLoc;
                agentObjective.SplinterParent = splinterParent;
                agentObjective.DispatchTime = Time.time;

                // Distance check / already-in-radius short-circuit is per- AGENT (against their splinter or
                // the squad anchor — whichever they got), not per-squad. Without this followers with a
                // splinter would inherit the squad- anchor distance check and deadlock.
                if (targetLoc != null)
                {
                    var distSqr = (targetLoc.Position - agent.Position).sqrMagnitude;
                    if (distSqr <= targetLoc.RadiusSqr)
                    {
                        if (GotoObjectiveAction.IsLootableForAgent(agent, targetLoc)
                            && waypointSystem.TryClaim(targetLoc.Id, agent.Id))
                        {
                            agentObjective.Status = ObjectiveStatus.Looting;
                            Log.Debug($"{agent} new objective {targetLoc} already in radius, claim OK → Looting (skipped Goto)");
                        }
                        else
                        {
                            agentObjective.Status = ObjectiveStatus.Finished;
                            Log.Debug($"{agent} new objective {targetLoc} already in radius → Finished (skipped Goto)");
                        }
                    }
                    else
                    {
                        agentObjective.Status = ObjectiveStatus.None;
                    }
                }
                else
                {
                    agentObjective.Status = ObjectiveStatus.None;
                }

                // Cover points are computed around the squad anchor — followers on a splinter still rally
                // back here once they're done.
                if (squadObjective.Location != null && squadObjective.CoverPoints.Count > 0)
                {
                    var coverPointIdx = i % squadObjective.CoverPoints.Count;
                    agent.Guard.CoverPoint = squadObjective.CoverPoints[coverPointIdx];
                }

                Log.Debug($"{agent} assigned objective {targetLoc}{(splinterParent != null ? $" (splinter of {splinterParent})" : "")}");
            }

            if (agentObjective.Location == null)
                continue;

            switch (agent.Objective.Status)
            {
                case ObjectiveStatus.Failed:
                    finishedCount++;
                    break;
                case ObjectiveStatus.Finished:
                {
                    finishedCount++;

                    if (squadObjective.Status == SquadObjectiveState.Wait)
                        break;

                    // A member actually reached — clear the en-route failure streak so a future bad run
                    // starts at zero.
                    squad.ConsecutiveFailedDispatches = 0;

                    // First squad member to reach the objective. If it was a Quest, mark the POI as
                    // permanently consumed for this squad — doing the same quest trigger twice doesn't make
                    // sense. Loot and Synthetic have their own mechanisms (Loot via the loot routine;
                    // Synthetic via the cooldown set up in AssignNewObjective below).
                    if (squadObjective.Location != null
                        && squadObjective.Location.Category == WaypointCategory.Quest)
                    {
                        squad.CompletedPoiIds.Add(squadObjective.Location.Id);
                        Log.Debug($"{squad} completed Quest {squadObjective.Location} — permanent squad blacklist");

                        // Mark the matching Quest main objective Completed. PickFromCell's owner-only gate
                        // guarantees only the squad whose main owns this trigger ID could have picked the POI
                        // — so finding a matching main here is expected.
                        if (squad.MainObjectives != null)
                        {
                            var triggerId = squadObjective.Location.Name;
                            for (var m = 0; m < squad.MainObjectives.Count; m++)
                            {
                                var main = squad.MainObjectives[m];
                                if (main.Type != MainObjectiveType.Quest) continue;
                                if (main.QuestTriggerId != triggerId) continue;
                                if (main.Completed) continue;
                                main.Completed = true;
                                Log.Info($"{squad} Quest main '{triggerId}' completed (arrived at trigger)");
                                break;
                            }
                        }
                    }

                    // (Opportunistic-corpse interrupt resume lives in the loot routine's completion path — it
                    // must fire when the looter actually finishes the corpse, not when the first follower
                    // hits Finished after claim failure. Triggering here would swap squad.Objective away from
                    // the corpse mid-loot, realign the looter off-target and abort the loot animation.)

                    Log.Debug($"{agent} reached squad objective {squadObjective.Location}");
                    var waitDuration = _guardDuration.SampleGaussian();
                    squadObjective.Status = SquadObjectiveState.Wait;
                    ResetDuration(squadObjective, waitDuration);
                    Log.Debug($"{squad} engaging wait mode for {waitDuration} seconds");
                    break;
                }
                case ObjectiveStatus.None:
                case ObjectiveStatus.Moving:
                default:
                    break;
            }
        }

        return finishedCount;
    }

    // ── Main objectives: tick + completion + extract trigger ────────
    //
    // Walks the squad's pending main objectives each tick. Per type: Kills: enter roam phase when any member
    // is in the anchor cell; complete when the rolled duration elapses (timer runs continuously, doesn't
    // reset if members wander out — the constant force pulls them back). LootValue: complete when all loot
    // POIs in the anchor cell are looted globally or blacklisted by the squad, OR all members have full
    // inventory, OR the timeout fires. Quest: completion is handled by the arrival path in UpdateAgents.
    //
    // When ALL mains are Completed (or the list is null/empty for boss / raider squads), flips
    // ExtractRequested so the next dispatch bee- lines to the nearest eligible exfil.
    private void TickMainObjectives(Squad squad)
    {
        if (squad.MainObjectives == null || squad.MainObjectives.Count == 0) return;
        var allDone = true;
        var now = Time.time;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var main = squad.MainObjectives[i];
            if (main.Completed) continue;
            allDone = false;
            CheckMainCompletion(squad, main, now);
        }
        if (allDone && !squad.ExtractRequested && Plugin.MainObjectivesExtractOnAllCompleted.Value)
        {
            squad.ExtractRequested = true;
            squad.ExtractRequestedReason = "all mains done";
            Log.Info($"{squad} all main objectives completed — flipping ExtractRequested");
        }
    }

    private void CheckMainCompletion(Squad squad, MainObjective main, float now)
    {
        switch (main.Type)
        {
            case MainObjectiveType.Kills:
                // Phase 1: roam starts as soon as ANY member is in the anchor cell. Same semantic as
                // LootValue cell entry — every cell guarantees at least one reachable POI, so the bot has
                // something to roam onto immediately.
                if (main.KillsRoamStartedAt <= 0f)
                {
                    for (var i = 0; i < squad.Size; i++)
                    {
                        if (waypointSystem.WorldToCell(squad.Members[i].Position) == main.CellCoords)
                        {
                            main.KillsRoamStartedAt = now;
                            Log.Info($"{squad} Kills main at {main.CellCoords} entered roam phase (member {i} in cell, {main.KillsRoamTargetDuration:F0}s)");
                            break;
                        }
                    }
                }
                // Phase 2: timer-based completion
                if (main.KillsRoamStartedAt > 0f
                    && now - main.KillsRoamStartedAt >= main.KillsRoamTargetDuration)
                {
                    main.Completed = true;
                    Log.Info($"{squad} Kills main at {main.CellCoords} completed after {now - main.KillsRoamStartedAt:F0}s roam");
                }
                break;

            case MainObjectiveType.LootValue:
                // Unified cell-entry detection. Both LootValueStartedAt (arms the timeout) and
                // LootValueEnteredAt (gates cell- clean completion + raid-review "in progress" visual) flip
                // together when ANY member enters the main's cell.
                //
                // Any member (not just leader) counts.
                if (main.LootValueStartedAt <= 0f)
                {
                    for (var i = 0; i < squad.Size; i++)
                    {
                        if (waypointSystem.WorldToCell(squad.Members[i].Position) == main.CellCoords)
                        {
                            main.LootValueStartedAt = now;
                            main.LootValueEnteredAt = now;
                            Log.Info($"{squad} LootValue main at {main.CellCoords} cell entered (member {i}) — {Plugin.MainObjectivesLootValueTimeoutSeconds.Value:F0}s timeout armed, cleanup engaged");
                            // Apply the per-POI coverage roll exactly once, on cell entry.
                            waypointSystem.ApplyLootCoverageRollForCell(squad, main.CellCoords);
                            // Force the squad to re-pick on the very next strategy tick. Without this the bot
                            // can keep walking toward whatever intermediate POI it was assigned BEFORE
                            // entering the cell for the full guard-duration — wasting the engagement window.
                            // After the re-pick the main-anchor priority pick will grab the best loot POI
                            // within 5m of the cell centre.
                            squad.Objective.Duration = 0;
                            break;
                        }
                    }
                }
                // Engaged-time accounting. "Engaged" = at least one member in the anchor cell AND the squad
                // isn't in combat. The timeout ticks down ONLY during engaged time — a firefight mid-loot
                // pauses the counter and resumes when SAIN hands the bot back over, simulating a player who
                // gets interrupted, fights, then returns to looting.
                if (main.LootValueStartedAt > 0f)
                {
                    var anyMemberInCell = false;
                    for (var i = 0; i < squad.Size; i++)
                    {
                        if (waypointSystem.WorldToCell(squad.Members[i].Position) == main.CellCoords)
                        {
                            anyMemberInCell = true;
                            break;
                        }
                    }
                    var engaged = anyMemberInCell && squad.CombatCallerMemberIdx < 0;
                    if (engaged)
                    {
                        if (main.LootValueLastEngagedAt > 0f)
                            main.LootValueElapsedEngaged += now - main.LootValueLastEngagedAt;
                        main.LootValueLastEngagedAt = now;
                        if (main.LootValueInterrupted)
                        {
                            main.LootValueInterrupted = false;
                            Log.Info($"{squad} LootValue main at {main.CellCoords} resumed (engaged-time so far {main.LootValueElapsedEngaged:F0}s / {Plugin.MainObjectivesLootValueTimeoutSeconds.Value:F0}s)");
                        }
                    }
                    else
                    {
                        main.LootValueLastEngagedAt = 0f;
                        if (!main.LootValueInterrupted && main.LootValueEnteredAt > 0f)
                        {
                            main.LootValueInterrupted = true;
                            var cause = !anyMemberInCell ? "out of cell" : "in combat (SAIN took over)";
                            Log.Info($"{squad} LootValue main at {main.CellCoords} interrupted — {cause} (engaged-time so far {main.LootValueElapsedEngaged:F0}s)");
                        }
                    }

                    if (main.LootValueElapsedEngaged >= Plugin.MainObjectivesLootValueTimeoutSeconds.Value)
                    {
                        main.Completed = true;
                        Log.Info($"{squad} LootValue main at {main.CellCoords} completed by engaged-time timeout ({main.LootValueElapsedEngaged:F0}s of in-cell non-combat looting)");
                        return;
                    }
                }
                // Cell-clean: all loot POIs in the anchor cell are either looted globally (removed) or
                // blacklisted by this squad. GATED on the squad having actually entered the cell — without
                // this gate a Main loot can complete "by cell- clean" even when no member ever set foot in
                // the cell.
                if (main.LootValueEnteredAt > 0f
                    && IsLootCellCleaned(squad, main.CellCoords))
                {
                    main.Completed = true;
                    Log.Info($"{squad} LootValue main at {main.CellCoords} completed by cell-clean");
                    return;
                }
                break;

            case MainObjectiveType.Quest:
                // Completion is handled exclusively in UpdateAgents when a squad member reaches the Quest
                // trigger POI. No timeout fallback here — quests are binary: reached or not. The
                // generation-time reachability gate already filters out quests on disconnected navmesh
                // fragments. If the squad genuinely can't reach the trigger mid- raid,
                // CheckTimeExtractTrigger takes over.
                break;
        }
    }

    private bool IsLootCellCleaned(Squad squad, Vector2Int cellCoords)
    {
        if (cellCoords.x < 0 || cellCoords.x >= waypointSystem.GridSize.x
            || cellCoords.y < 0 || cellCoords.y >= waypointSystem.GridSize.y) return true;
        ref var cell = ref waypointSystem.Cells[cellCoords.x, cellCoords.y];
        if (!cell.HasWaypoints) return true;
        for (var i = 0; i < cell.Waypoints.Count; i++)
        {
            var loc = cell.Waypoints[i];
            if (loc.Category != WaypointCategory.ContainerLoot
                && loc.Category != WaypointCategory.LooseLoot
                && loc.Category != WaypointCategory.Corpse) continue;
            // Each remaining loot POI must be blacklisted by this squad (visited + skipped, or visited +
            // looted with item removed — looted items remove the POI from cell.Waypoints globally, so they
            // wouldn't appear here in the first place).
            if (!squad.CompletedPoiIds.Contains(loc.Id)) return false;
        }
        return true;
    }

    // Minimum interval between two refreshes of the same cell for the same squad. Set high enough that a
    // leader oscillating between two adjacent cells doesn't keep re-running NavMesh.CalculatePath on the
    // shared 6 neighbours on every transition.
    private const float UnreachabilityRefreshCooldownSeconds = 300f;

    private void RefreshUnreachabilityAroundLeader(Squad squad)
    {
        var leader = squad.Leader?.Bot;
        if (leader == null) return;

        var currentCell = waypointSystem.WorldToCell(leader.Position);
        if (squad.LastKnownCell.HasValue && squad.LastKnownCell.Value == currentCell) return;

        // Leader just entered a new cell — re-evaluate stale unreachability verdicts for the 3x3
        // neighbourhood. Cells we already refreshed within the cooldown are skipped.
        var now = Time.time;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                var c = currentCell + new Vector2Int(dx, dy);
                if (squad.RecentlyRefreshedCells.TryGetValue(c, out var lastRefresh)
                    && now - lastRefresh < UnreachabilityRefreshCooldownSeconds)
                {
                    continue;
                }
                waypointSystem.ClearSquadUnreachabilityForCell(squad, c);
                squad.RecentlyRefreshedCells[c] = now;
            }
        }
        squad.LastKnownCell = currentCell;
    }

    private bool ShouldUseIndependentDispatch(Squad squad)
    {
        if (squad?.MainObjectives == null) return false;
        var leader = squad.Leader?.Bot;
        if (leader == null) return false;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var main = squad.MainObjectives[i];
            if (main.Completed) continue;
            if (main.Type == MainObjectiveType.Kills && main.KillsRoamStartedAt > 0f) return true;
            if (main.Type == MainObjectiveType.LootValue)
            {
                for (var k = 0; k < squad.Size; k++)
                {
                    var mCell = waypointSystem.WorldToCell(squad.Members[k].Position);
                    if (mCell == main.CellCoords) return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The squad's currently "active" main objective in the independent- dispatch sense — Kills main in roam
    /// phase OR LootValue main with at least one member in the cell. Kills wins if both are active. Used as
    /// the ANCHOR for roam splinter searches so the bot oscillates around the main's anchor position, not
    /// around its own drifting current position.
    /// </summary>
    private MainObjective ActiveIndependentMain(Squad squad)
    {
        if (squad?.MainObjectives == null) return null;
        MainObjective lootValueActive = null;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var main = squad.MainObjectives[i];
            if (main.Completed) continue;
            if (main.Type == MainObjectiveType.Kills && main.KillsRoamStartedAt > 0f)
                return main;
            if (main.Type == MainObjectiveType.LootValue && lootValueActive == null)
            {
                for (var k = 0; k < squad.Size; k++)
                {
                    var mCell = waypointSystem.WorldToCell(squad.Members[k].Position);
                    if (mCell == main.CellCoords) { lootValueActive = main; break; }
                }
            }
        }
        return lootValueActive;
    }

    private void DetectAndUpdateCombatCaller(Squad squad)
    {
        // Solo squads can't rally — no supporters to call. Without this gate, a lone bot whose
        // Memory.HaveEnemy gets stuck true (SAIN keeps the flag on for an extended window after losing LoS
        // — Search / SeekCover sub-states) self-registers as caller every tick, which pins squad.Objective
        // to a virtual CombatCaller waypoint at his own position and resets StartTime so the wait timer
        // never expires and AssignNewObjective never fires. The bot is then physically stranded once SAIN's
        // mover stops producing motion (raid trace: AiKunCCTV / Xust1ed frozen 10+ min at the spot SAIN
        // reached on Search arrival).
        if (squad.Size <= 1)
        {
            if (squad.CombatCallerMemberIdx >= 0)
            {
                Log.Info($"{squad} combat caller cleared (squad is solo — nobody to rally)");
                squad.CombatCallerMemberIdx = -1;
            }
            return;
        }

        var now = Time.time;
        var anyInCombat = false;
        var callerIdx = squad.CombatCallerMemberIdx;
        for (var i = 0; i < squad.Size; i++)
        {
            var member = squad.Members[i];
            var bot = member?.Bot;
            if (bot?.Memory == null) continue;
            if (bot.Memory.HaveEnemy || bot.Memory.IsUnderFire)
            {
                anyInCombat = true;
                callerIdx = i;
                squad.CombatCallerPosition = member.Position;
                squad.CombatCallerLastSeenAt = now;
                break;
            }
        }
        if (anyInCombat)
        {
            if (squad.CombatCallerMemberIdx != callerIdx)
            {
                Log.Info($"{squad} combat caller = {squad.Members[callerIdx]} at {squad.CombatCallerPosition}");
                squad.CombatCallerMemberIdx = callerIdx;
            }
        }
        else if (squad.CombatCallerMemberIdx >= 0
                 && now - squad.CombatCallerLastSeenAt > Plugin.MainObjectivesCombatCallerGraceSeconds.Value)
        {
            Log.Info($"{squad} combat caller cleared (grace elapsed)");
            squad.CombatCallerMemberIdx = -1;
        }
    }

    private static void CheckTimeExtractTrigger(Squad squad)
    {
        if (squad.ExtractRequested) return;
        var leaderBot = squad?.Leader?.Bot;
        if (leaderBot?.Profile?.Info?.Settings == null) return;
        var role = leaderBot.Profile.Info.Settings.Role;
        // Eligibility: same gate as the loot-value trigger — only factions permitted to extract bother to
        // roll a threshold.
        if (!(LootConfig.ExtractAllowedFor?.Value ?? ExtractFaction.All).IsBotEnabled(role)) return;

        // Lazy-roll the threshold the first time we evaluate this squad.
        if (float.IsNaN(squad.TimeExtractThresholdSeconds))
            squad.TimeExtractThresholdSeconds = RollExtractThreshold(leaderBot);

        var gameTimer = Singleton<AbstractGame>.Instance?.GameTimer;
        if (gameTimer == null) return;
        if (!gameTimer.SessionTime.HasValue) return;
        var remaining = (float)(gameTimer.SessionTime.Value.TotalSeconds - gameTimer.PastTime.TotalSeconds);
        if (remaining > squad.TimeExtractThresholdSeconds) return;

        squad.ExtractRequested = true;
        squad.ExtractRequestedReason = $"raid time low ({remaining:F0}s left)";
        Log.Info($"{squad}: raid time low ({remaining:F0}s remaining <= {squad.TimeExtractThresholdSeconds:F0}s threshold for role {role}) — squad will bee-line to nearest eligible exfil");
    }

    private static float RollExtractThreshold(BotOwner leaderBot)
    {
        var isPlayerScav = leaderBot?.Profile != null && leaderBot.Profile.WillBeAPlayerScav();
        var windowPct = isPlayerScav ? Plugin.TimeExtractWindowPlayerScav.Value : Plugin.TimeExtractWindowPmc.Value;
        var totalRaidSeconds = (float)(Singleton<AbstractGame>.Instance?.GameTimer?.SessionTime?.TotalSeconds ?? 0d);
        if (totalRaidSeconds <= 0f) return 0f;
        return totalRaidSeconds * Random.Range(windowPct.x, windowPct.y) / 100f;
    }

    // After this many consecutive "all members failed en-route" branches on the same objective the squad
    // gives up on that POI and adds it to CompletedPoiIds. Previously the same threshold triggered a
    // teleport-rescue snapping every member onto the POI's navmesh sample point; removed because it dropped
    // bots into locked rooms and caused infinite TP attempt loops on mains whose anchor was off-navmesh.
    private const int UnreachableBlacklistThreshold = 5;

    // Corpse-stuck watchdog timeout. A normal corpse loot resolves in well under this (6-25s inspection +
    // pickup, then the corpse is blacklisted on success/fail/empty). If the squad stays glued to the SAME
    // corpse longer than this without it completing, the strategy force-blacklists + re-dispatches.
    private const float CorpseStuckTimeoutSeconds = 45f;

    /// <summary>
    /// Anchor position of the first in-progress main objective on the squad's list, or <see langword="null"/>
    /// if no main is engaged. "In progress" = LootValue cell entered or Kills roam phase started. Used by
    /// <see cref="AssignNewObjective"/> to pin the nearest-POI search to the main's cell instead of drifting
    /// to the leader's current position.
    /// </summary>
    private static Vector3? GetInProgressMainAnchor(Squad squad)
    {
        if (squad?.MainObjectives == null) return null;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var m = squad.MainObjectives[i];
            if (m.Completed) continue;
            if (m.Type == MainObjectiveType.LootValue && m.LootValueEnteredAt > 0f) return m.Position;
            if (m.Type == MainObjectiveType.Kills && m.KillsRoamStartedAt > 0f) return m.Position;
        }
        return null;
    }

    private void AssignNewObjective(Squad squad)
    {
        var objective = squad.Objective;

        // Synthetic POIs get a short-term visit cooldown so the squad doesn't ping-pong on the same
        // patrol-filler coordinate between wait timers. Quest is handled separately (permanent squad
        // blacklist via CompletedPoiIds). Loot POIs already have their own mechanisms.
        if (objective.Location != null
            && objective.Location.Category == WaypointCategory.Synthetic)
        {
            squad.RecentlyVisitedPoiCooldowns[objective.Location.Id] =
                Time.time + Plugin.SyntheticVisitCooldownSeconds.Value;
        }

        Waypoint newLocation;
        // Loot-value extract: a squad that's hit its threshold ignores normal cell dispatch and bee-lines to
        // the nearest eligible exfil. ALWAYS re-route to the exfil while ExtractRequested is set. Re-picking
        // the same exfil is a no-op alignment-wise (UpdateAgents sees the same reference and skips
        // reassignment), and any agent already in Status=Extracting keeps its own action running — wait-timer
        // reset doesn't affect ExtractAction's countdown.
        if (squad.ExtractRequested)
        {
            newLocation = waypointSystem.FindNearestEligibleExfil(squad);
            if (newLocation != null)
            {
                Log.Debug($"{squad} ExtractRequested → routing to nearest eligible exfil {newLocation}");
            }
            else
            {
                // No eligible exfil left on the map. Fall back to normal dispatch so the squad doesn't stall
                // forever.
                Log.Warning($"{squad} ExtractRequested but no eligible exfil found, falling back to normal dispatch");
                newLocation = waypointSystem.RequestNear(squad, squad.Leader.Bot.Position, objective.LocationPrevious);
            }
        }
        else
        {
            // Leash dispatch to the in-progress main's anchor only when the leader has drifted outside the
            // roam radius. Inside the leash the leader's current position drives RequestNear so the squad
            // keeps drifting freely on splinter loot / nearby POIs. Outside the leash, bias the next dispatch
            // toward the main's anchor so the squad doesn't permanently wander off.
            var leaderPos = squad.Leader.Bot.Position;
            var pinAnchor = GetInProgressMainAnchor(squad);
            Vector3 requestPos;
            if (pinAnchor.HasValue)
            {
                var leash = Plugin.MainObjectivesRoamSplinterRadius.Value;
                // XZ-only — anchor.Y is 0 by construction (cell centre / custom zone), so 3D Euclidean would
                // wrongly snap the leader back the moment a height mismatch crosses the leash threshold (a
                // bot looting in a Resort basement is "50m away" from the cell anchor at Y=0 in 3D, but 0m
                // horizontally).
                var distSqr = WaypointSystem.XzDistanceSqr(leaderPos, pinAnchor.Value);
                requestPos = distSqr > leash * leash ? pinAnchor.Value : leaderPos;
            }
            else
            {
                requestPos = leaderPos;
            }
            newLocation = waypointSystem.RequestNear(squad, requestPos, objective.LocationPrevious);
        }

        if (newLocation == null)
        {
            squad.ConsecutiveDispatchFailures++;
            Log.Debug($"{squad} received null objective location (consecutive failures: {squad.ConsecutiveDispatchFailures})");
            return;
        }

        // Successful dispatch — reset the islanded counter so we don't pin a squad to its cell forever after
        // one good streak of failures.
        squad.ConsecutiveDispatchFailures = 0;

        objective.LocationPrevious = objective.Location;
        objective.Location = newLocation;
        objective.Status = SquadObjectiveState.Active;

        ShufflePickCoverPoints(objective, Math.Max(squad.TargetMembersCount, squad.Size));

        ResetDuration(objective, _moveTimeout.SampleGaussian());

        Log.Debug($"{squad} assigned objective {objective.Location}");
    }

    private static void ShufflePickCoverPoints(SquadObjective objective, int count)
    {
        var location = objective.Location;

        objective.CoverPoints.Clear();

        // Runtime Corpse waypoints (corpse-registration patch) ship with an empty cover-points list because
        // we don't run the cover sampler mid-raid. Without this guard the Random.Range / indexer / modulo
        // below all explode and AssignNewObjective throws.
        if (location.CoverPoints.Count == 0) return;

        var randIdx = Random.Range(0, location.CoverPoints.Count);

        for (var i = 0; i < count; i++)
        {
            objective.CoverPoints.Add(location.CoverPoints[randIdx]);
            randIdx = (randIdx + 1) % location.CoverPoints.Count;
            Log.Debug($"Getting cover point at {randIdx}/{location.CoverPoints.Count}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ResetDuration(SquadObjective objective, float duration)
    {
        objective.StartTime = Time.time;
        objective.Duration = duration;
        objective.DurationAdjusted = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AdjustDuration(SquadObjective objective, float duration)
    {
        objective.Duration = duration;
        objective.DurationAdjusted = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AdjustDuration(SquadObjective objective, float duration, float startTime)
    {
        objective.StartTime = startTime;
        objective.Duration = duration;
        objective.DurationAdjusted = true;
    }
}
