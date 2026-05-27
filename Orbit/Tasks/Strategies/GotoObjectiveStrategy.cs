using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Inventory;
using Orbit.Navigation;
using Orbit.Systems;
using Orbit.Tasks.Actions;
using UnityEngine;
using Random = UnityEngine.Random;
using Range = Orbit.Config.Range;

namespace Orbit.Tasks.Strategies;

/// <summary>
/// The squad-level dispatch loop. For every active squad it ticks main
/// objectives (Kills roam timer, LootValue cell entry / cleanup / engaged-
/// time timeout, Quest completion), drives the combat-convergence
/// override, refreshes the per-squad unreachability cache as the leader
/// crosses cells, scans for opportunistic-corpse interrupts, then
/// (re-)dispatches each member onto either the squad anchor, a roam
/// splinter, or a loot splinter as appropriate.
/// </summary>
public class GotoObjectiveStrategy(SquadData squadData, WaypointSystem waypointSystem, float hysteresis) : Task<Squad>(hysteresis)
{
    private static Range _moveTimeout = new(400, 600);
    private Range _guardDuration = new(Plugin.ObjectiveGuardDuration.Value.x, Plugin.ObjectiveGuardDuration.Value.y);
    private Range _guardDurationCut = new(Plugin.ObjectiveGuardDurationCut.Value.x, Plugin.ObjectiveGuardDurationCut.Value.y);
    private Range _adjustedGuardDuration = new(Plugin.ObjectiveAdjustedGuardDuration.Value.x, Plugin.ObjectiveAdjustedGuardDuration.Value.y);
    // Scav-only idle pause at a Synthetic POI. The default 3-7s adjusted
    // guard makes scavs look hyperactive, chaining waypoints without ever
    // stopping. 10s-120s gaussian (mean ~65s) gives a mix of short
    // glances and longer corner-camping pauses. During the wait the
    // agent runs GuardAction (cover point + area sweeps + watch
    // direction rotation), so it's not a frozen stand-still.
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

            // Deferred SAIN personality resolution. PMC squads spawn
            // before SAIN attaches its BotComponent (1-2s delay), so
            // SquadRegistry deferred the lookup + the main-objective roll.
            // Retry here every tick until the brain resolves or the 5 s
            // deadline lapses (then lock to Average and generate mains
            // anyway).
            if (squad.SainResolutionPending)
            {
                Singleton<OrbitManager>.Instance?.SquadRegistry?.TryResolvePersonality(squad);
            }

            // Rolling per-squad unreachability refresh. _squadUnreachable
            // is populated from the leader's CURRENT position via
            // NavMesh.CalculatePath; verdicts cached when the leader was
            // 500m away become wrong once they walk into the area. On
            // every cell transition we re-evaluate the 3x3 window around
            // the leader's new cell, gated by a per-cell cooldown so
            // oscillating leaders don't thrash the cache.
            RefreshUnreachabilityAroundLeader(squad);

            // Tick main objectives (Kills timer / LootValue completion /
            // extract trigger when all done). Independent of the per-squad
            // objective dispatch flow below — mains drive the long-term
            // force attraction, the dispatch picks tick-level secondary
            // POIs.
            TickMainObjectives(squad);

            // Combat-convergence: when an active main (Kills roam or
            // LootValue) is engaged, scan members for combat. First
            // detected member becomes "caller" and squad.Objective.Location
            // gets swapped to a virtual Waypoint at their position so
            // realign converges everyone there. Grace keeps the override
            // stable through reload pauses.
            if (ShouldUseIndependentDispatch(squad))
            {
                DetectAndUpdateCombatCaller(squad);
                if (squad.CombatCallerMemberIdx >= 0)
                {
                    // Refresh the virtual override each tick — cheap, and
                    // ensures squad.Objective.Location keeps pointing at
                    // the caller even if some other code mutated it.
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
                // Squad no longer in an independent-dispatch phase but
                // caller flag was stuck on — clear it defensively.
                squad.CombatCallerMemberIdx = -1;
            }

            CheckTimeExtractTrigger(squad);

            // Opportunistic corpse interrupt: any squad member who sees
            // an unlooted corpse within DetectCorpseDistance drops the
            // current objective to investigate. Real-Tarkov behaviour —
            // a bot walking past a body always checks it. We gate the
            // scan on:
            //   * squad is not already on a Corpse objective
            //   * squad is not already mid-interrupt
            //   * squad has not flipped ExtractRequested (extract bee-line
            //     beats opportunistic loot — they've decided to leave)
            //   * pacing throttle elapsed (the raycast loop is non-trivial)
            //
            // When we DO interrupt, we save the current objective only if
            // it's worth resuming — Synthetic patrol fillers get cleared
            // so the post-loot flow runs a fresh AssignNewObjective.
            if (!squad.ExtractRequested
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
                // Honour an explicit post-rescue (or other) cooldown set
                // by a patch that nulled the location: if the wait window
                // hasn't elapsed yet, don't immediately hand the squad a
                // new POI. Without this gate, the rescue-prune cooldown
                // gets bypassed and the rescued bot is sent chasing the
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
                    // Every member's path attempt came back Failed without
                    // anyone reaching the POI. After enough in a row the
                    // POI is effectively unreachable for this squad —
                    // blacklist it so the next pick goes somewhere else.
                    //
                    // Previously this branch hard-teleported the squad to
                    // the POI's navmesh sample point. That created two
                    // failure modes:
                    //  - TP dropped the bot inside locked rooms (Resort /
                    //    Sanatorium navmesh interiors had a sample point
                    //    even though the door was a one-way breach), bot
                    //    stuck inside for the rest of the raid.
                    //  - On Kills mains anchored at config zone centers
                    //    sitting in walls / cliffs / water, the navmesh
                    //    sample failed → TP fall back to "normal dispatch"
                    //    → en-route fail again → infinite TP attempt loop.
                    // Drop the teleport entirely. Force-unlock probability
                    // rolls on locked doors stay intact via the door-
                    // handling path in MovementSystem.
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
                            // The looting pipeline already takes 6-25s per
                            // POI to simulate inspection / pickup. No need
                            // to add a guard wait on top — that just makes
                            // squads stand around at empty containers.
                            // Force the timer to expire immediately.
                            AdjustDuration(squadObjective, 0f, Time.time);
                            Log.Debug($"{squad} skipping guard wait at loot POI {squadObjective.Location} (loot action already provided its own delay)");
                            break;
                        case WaypointCategory.Quest:
                            // Quest triggers still benefit from a short
                            // guard wait (no inner delay).
                            AdjustDuration(squadObjective, squadObjective.Duration * _guardDurationCut.SampleGaussian());
                            Log.Debug($"{squad} adjusted {squadObjective.Location} wait duration to {squadObjective.Duration}");
                            break;
                        case WaypointCategory.Synthetic:
                            // Bot scavs (assault / assaultGroup, not
                            // PlayerScavs) get a much longer randomised
                            // idle pause at a Synthetic — vanilla scavs
                            // frequently stop and stand around their
                            // patrol points for a minute or more, while
                            // ours were chaining Synthetic→Synthetic with
                            // only a 3-7s pause. PMCs / raiders / bosses /
                            // PlayerScavs keep the short pause so they
                            // stay aggressive on their patrol path.
                            //
                            // PlayerScavs share WildSpawnType.assault with
                            // bot scavs so IsScav() can't distinguish them
                            // — the canonical detection is the
                            // Profile.WillBeAPlayerScav extension.
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

    // Tracks splinter waypoint ids assigned earlier in the same UpdateAgents
    // pass so two followers in the same squad don't get handed the same POI.
    private readonly HashSet<int> _splinterScratch = new(8);

    private int UpdateAgents(Squad squad)
    {
        var squadObjective = squad.Objective;
        var finishedCount = 0;
        _splinterScratch.Clear();

        // Independent-dispatch mode: each member picks their own roam
        // splinter (extended radius + category mask) instead of all
        // converging on the squad anchor. Triggered during Kills roam and
        // LootValue active phases, but disabled when a combat caller is
        // set (everyone converges on the caller instead), AND disabled
        // once the squad has decided to extract — letting members pick
        // splinter loot on the way to the exfil delays the whole extract
        // chain indefinitely.
        var useRoam = ShouldUseIndependentDispatch(squad)
                      && squad.CombatCallerMemberIdx < 0
                      && !squad.ExtractRequested;
        // Anchor for the roam splinter search: the active main's Position
        // (Kills zone centre / LootValue cell centre). Without this, the
        // splinter radius would be centred on each bot's drifting current
        // position and they'd wander out of the zone over a few re-picks.
        var activeMain = useRoam ? ActiveIndependentMain(squad) : null;
        var activeType = activeMain?.Type;
        // Per-type category mask. Kills roam = wide net (loot + corpse +
        // synthetic) so members "look for action"; LootValue = loot +
        // corpse only.
        var roamLooseLoot = useRoam;
        var roamContainerLoot = useRoam;
        var roamCorpse = useRoam;
        var roamSynthetic = useRoam && activeType == MainObjectiveType.Kills;

        for (var i = 0; i < squad.Size; i++)
        {
            var agent = squad.Members[i];
            var agentObjective = agent.Objective;

            // An agent is "aligned" with the squad if their location IS
            // the squad's main objective, OR if they're working a splinter
            // that was picked around the squad's current main objective.
            // Without the SplinterParent check, followers on a splinter
            // would look misaligned every tick and get re-dispatched in
            // a loop.
            //
            // Exception: an agent whose splinter is already done (loot
            // succeeded → Finished, or arrival kept failing → Failed) is
            // treated as misaligned so UpdateAgents picks a FRESH splinter.
            // Without this, the agent freezes on the completed splinter
            // and the squad anchor's surrounding POIs never get worked
            // through.
            var splinterAlreadyDone = agentObjective.SplinterParent != null
                                      && (agentObjective.Status == ObjectiveStatus.Finished
                                          || agentObjective.Status == ObjectiveStatus.Failed);
            var aligned = !splinterAlreadyDone
                          && (agentObjective.Location == squadObjective.Location
                              || (agentObjective.SplinterParent != null
                                  && agentObjective.SplinterParent == squadObjective.Location));

            if (aligned && agentObjective.Location != null)
            {
                // Track existing splinters so we don't hand the same POI
                // to another follower below.
                if (agentObjective.SplinterParent != null)
                    _splinterScratch.Add(agentObjective.Location.Id);
                // Reset Failed back to None so Goto picks the agent back
                // up and re-submits a move order. Without this, an Exfil
                // dispatch that stops short (partial nav-path 400m+ off
                // an exfil) leaves the agent pinned at Status=Failed
                // forever: ExtractRequested keeps re-confirming the SAME
                // exfil reference each tick, alignment stays true, and
                // no per-agent re-assignment fires to clear the failed
                // flag. Agent never tries to move again.
                if (agentObjective.Status == ObjectiveStatus.Failed)
                    agentObjective.Status = ObjectiveStatus.None;
            }

            if (!aligned)
            {
                // Dispatch priority:
                //   1. Own-kill direct: the specific agent who landed the
                //      fresh corpse kill goes straight to that corpse,
                //      not a random splinter around it. Cleared after
                //      first use.
                //   2. Anchor-first for the leader (i=0): exactly one
                //      member works the anchor itself, others get
                //      splinters. Solo squads naturally end up here too,
                //      so the bot loots the anchor before its splinters.
                //      Falls through to the splinter branch once the
                //      anchor is in CompletedPoiIds.
                //   3. Roam splinter (members on Kills/LootValue in
                //      progress, non-killer non-leader).
                //   4. Loot splinter (non-roam followers).
                //   5. Fallback to squad anchor (no splinter found).
                Waypoint targetLoc;
                Waypoint splinterParent;
                var ownKillAgentId = squad.PendingOwnKillKillerAgentId;
                var anchorReservedForOwnKill = ownKillAgentId >= 0
                                               && squadObjective.Location != null
                                               && squadObjective.Location.Id == squad.PendingOwnKillCorpseLocId;
                if (anchorReservedForOwnKill && agent.Id == ownKillAgentId)
                {
                    targetLoc = squadObjective.Location;
                    splinterParent = null;
                    squad.PendingOwnKillKillerAgentId = -1;
                    squad.PendingOwnKillCorpseLocId = 0;
                    Log.Debug($"{agent} own-kill direct-route to {targetLoc} (skipped splinter)");
                }
                else if (i == 0
                         && !anchorReservedForOwnKill
                         && squadObjective.Location != null
                         && !squad.CompletedPoiIds.Contains(squadObjective.Location.Id))
                {
                    // Leader → anchor itself. For solo squads this is the
                    // only member, so they always tackle the anchor before
                    // any splinter; once the anchor is done it ends up in
                    // CompletedPoiIds and the next pass falls through to
                    // the splinter branches.
                    targetLoc = squadObjective.Location;
                    splinterParent = null;
                }
                else if (useRoam)
                {
                    // Drift-libre by default: search centred on the bot's
                    // current position so they can naturally wander up to
                    // ~50m at each re-pick. Leash: if the bot has drifted
                    // further than the search radius from the active
                    // Main's anchor (e.g. chased an enemy out of a Kills
                    // zone), swap the search centre to the anchor so the
                    // next pick snaps them back.
                    var roamRadius = Plugin.MainObjectivesRoamSplinterRadius.Value;
                    var searchCenter = agent.Position;
                    if (activeMain != null
                        && WaypointSystem.XzDistanceSqr(agent.Position, activeMain.Position) > roamRadius * roamRadius)
                    {
                        // XZ-only: activeMain.Position.Y is 0 for
                        // LootValue mains (CellToWorld) and custom-zone
                        // Kills mains, so 3D distance would inflate by
                        // the vertical mismatch with the agent's real Y.
                        searchCenter = activeMain.Position;
                    }
                    var roamSplinter = waypointSystem.FindRoamSplinterForMember(
                        agent.Position, searchCenter, squad, _splinterScratch, roamRadius,
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
                    var splinter = waypointSystem.FindLootSplinterForFollower(
                        squadObjective.Location, squad, _splinterScratch,
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

                agentObjective.Location = targetLoc;
                agentObjective.SplinterParent = splinterParent;

                // Distance check / already-in-radius short-circuit is per-
                // AGENT (against their splinter or the squad anchor —
                // whichever they got), not per-squad. Without this
                // followers with a splinter would inherit the squad-
                // anchor distance check and deadlock.
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

                // Cover points are computed around the squad anchor —
                // followers on a splinter still rally back here once
                // they're done.
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

                    // A member actually reached — clear the en-route
                    // failure streak so a future bad run starts at zero.
                    squad.ConsecutiveFailedDispatches = 0;

                    // First squad member to reach the objective. If it
                    // was a Quest, mark the POI as permanently consumed
                    // for this squad — doing the same quest trigger
                    // twice doesn't make sense. Loot and Synthetic have
                    // their own mechanisms (Loot via the loot routine;
                    // Synthetic via the cooldown set up in
                    // AssignNewObjective below).
                    if (squadObjective.Location != null
                        && squadObjective.Location.Category == WaypointCategory.Quest)
                    {
                        squad.CompletedPoiIds.Add(squadObjective.Location.Id);
                        Log.Debug($"{squad} completed Quest {squadObjective.Location} — permanent squad blacklist");

                        // Mark the matching Quest main objective Completed.
                        // PickFromCell's owner-only gate guarantees only
                        // the squad whose main owns this trigger ID could
                        // have picked the POI — so finding a matching
                        // main here is expected.
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

                    // (Opportunistic-corpse interrupt resume lives in the
                    // loot routine's completion path — it must fire when
                    // the looter actually finishes the corpse, not when
                    // the first follower hits Finished after claim
                    // failure. Triggering here would swap squad.Objective
                    // away from the corpse mid-loot, realign the looter
                    // off-target and abort the loot animation.)

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
    // Walks the squad's pending main objectives each tick. Per type:
    //   Kills: enter roam phase when any member is in the anchor cell;
    //          complete when the rolled duration elapses (timer runs
    //          continuously, doesn't reset if members wander out — the
    //          constant force pulls them back).
    //   LootValue: complete when all loot POIs in the anchor cell are
    //          looted globally or blacklisted by the squad, OR all
    //          members have full inventory, OR the timeout fires.
    //   Quest: completion is handled by the arrival path in UpdateAgents.
    //
    // When ALL mains are Completed (or the list is null/empty for boss /
    // raider squads), flips ExtractRequested so the next dispatch bee-
    // lines to the nearest eligible exfil.
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
                // Phase 1: roam starts as soon as ANY member is in the
                // anchor cell. Same semantic as LootValue cell entry —
                // every cell guarantees at least one reachable POI, so
                // the bot has something to roam onto immediately.
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
                // Unified cell-entry detection. Both LootValueStartedAt
                // (arms the timeout) and LootValueEnteredAt (gates cell-
                // clean completion + raid-review "in progress" visual)
                // flip together when ANY member enters the main's cell.
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
                            // Apply the per-POI coverage roll exactly
                            // once, on cell entry.
                            waypointSystem.ApplyLootCoverageRollForCell(squad, main.CellCoords);
                            // Force the squad to re-pick on the very next
                            // strategy tick. Without this the bot can
                            // keep walking toward whatever intermediate
                            // POI it was assigned BEFORE entering the
                            // cell for the full guard-duration — wasting
                            // the engagement window. After the re-pick
                            // the main-anchor priority pick will grab the
                            // best loot POI within 5m of the cell centre.
                            squad.Objective.Duration = 0;
                            break;
                        }
                    }
                }
                // Engaged-time accounting. "Engaged" = at least one
                // member in the anchor cell AND the squad isn't in
                // combat. The timeout ticks down ONLY during engaged
                // time — a firefight mid-loot pauses the counter and
                // resumes when SAIN hands the bot back over, simulating
                // a player who gets interrupted, fights, then returns
                // to looting.
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
                // Cell-clean: all loot POIs in the anchor cell are either
                // looted globally (removed) or blacklisted by this squad.
                // GATED on the squad having actually entered the cell —
                // without this gate a Main loot can complete "by cell-
                // clean" even when no member ever set foot in the cell.
                if (main.LootValueEnteredAt > 0f
                    && IsLootCellCleaned(squad, main.CellCoords))
                {
                    main.Completed = true;
                    Log.Info($"{squad} LootValue main at {main.CellCoords} completed by cell-clean");
                    return;
                }
                break;

            case MainObjectiveType.Quest:
                // Completion is handled exclusively in UpdateAgents when
                // a squad member reaches the Quest trigger POI. No
                // timeout fallback here — quests are binary: reached or
                // not. The generation-time reachability gate already
                // filters out quests on disconnected navmesh fragments.
                // If the squad genuinely can't reach the trigger mid-
                // raid, CheckTimeExtractTrigger takes over.
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
            // Each remaining loot POI must be blacklisted by this squad
            // (visited + skipped, or visited + looted with item removed
            // — looted items remove the POI from cell.Waypoints globally,
            // so they wouldn't appear here in the first place).
            if (!squad.CompletedPoiIds.Contains(loc.Id)) return false;
        }
        return true;
    }

    // Minimum interval between two refreshes of the same cell for the
    // same squad. Set high enough that a leader oscillating between two
    // adjacent cells doesn't keep re-running NavMesh.CalculatePath on
    // the shared 6 neighbours on every transition.
    private const float UnreachabilityRefreshCooldownSeconds = 300f;

    private void RefreshUnreachabilityAroundLeader(Squad squad)
    {
        var leader = squad.Leader?.Bot;
        if (leader == null) return;

        var currentCell = waypointSystem.WorldToCell(leader.Position);
        if (squad.LastKnownCell.HasValue && squad.LastKnownCell.Value == currentCell) return;

        // Leader just entered a new cell — re-evaluate stale
        // unreachability verdicts for the 3x3 neighbourhood. Cells we
        // already refreshed within the cooldown are skipped.
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
    /// The squad's currently "active" main objective in the independent-
    /// dispatch sense — Kills main in roam phase OR LootValue main with
    /// at least one member in the cell. Kills wins if both are active.
    /// Used as the ANCHOR for roam splinter searches so the bot
    /// oscillates around the main's anchor position, not around its own
    /// drifting current position.
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
        // Eligibility: same gate as the loot-value trigger — only factions
        // permitted to extract bother to roll a threshold.
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
        var isFactory = false;
        var locationId = Singleton<GameWorld>.Instance?.LocationId;
        if (!string.IsNullOrEmpty(locationId))
            isFactory = locationId.StartsWith("factory", StringComparison.OrdinalIgnoreCase);
        var isPlayerScav = leaderBot?.Profile != null && leaderBot.Profile.WillBeAPlayerScav();

        Vector2 window;
        if (isPlayerScav)
            window = isFactory ? Plugin.TimeExtractWindowPlayerScavFactory.Value : Plugin.TimeExtractWindowPlayerScav.Value;
        else
            window = isFactory ? Plugin.TimeExtractWindowPmcFactory.Value : Plugin.TimeExtractWindowPmc.Value;
        return Random.Range(window.x, window.y);
    }

    // After this many consecutive "all members failed en-route" branches
    // on the same objective the squad gives up on that POI and adds it
    // to CompletedPoiIds. Previously the same threshold triggered a
    // teleport-rescue snapping every member onto the POI's navmesh
    // sample point; removed because it dropped bots into locked rooms
    // and caused infinite TP attempt loops on mains whose anchor was
    // off-navmesh.
    private const int UnreachableBlacklistThreshold = 5;

    /// <summary>
    /// Anchor position of the first in-progress main objective on the
    /// squad's list, or <see langword="null"/> if no main is engaged.
    /// "In progress" = LootValue cell entered or Kills roam phase
    /// started. Used by <see cref="AssignNewObjective"/> to pin the
    /// nearest-POI search to the main's cell instead of drifting to the
    /// leader's current position.
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

        // Synthetic POIs get a short-term visit cooldown so the squad
        // doesn't ping-pong on the same patrol-filler coordinate between
        // wait timers. Quest is handled separately (permanent squad
        // blacklist via CompletedPoiIds). Loot POIs already have their
        // own mechanisms.
        if (objective.Location != null
            && objective.Location.Category == WaypointCategory.Synthetic)
        {
            squad.RecentlyVisitedPoiCooldowns[objective.Location.Id] =
                Time.time + Plugin.SyntheticVisitCooldownSeconds.Value;
        }

        Waypoint newLocation;
        // Loot-value extract: a squad that's hit its threshold ignores
        // normal cell dispatch and bee-lines to the nearest eligible
        // exfil. ALWAYS re-route to the exfil while ExtractRequested is
        // set. Re-picking the same exfil is a no-op alignment-wise
        // (UpdateAgents sees the same reference and skips reassignment),
        // and any agent already in Status=Extracting keeps its own
        // action running — wait-timer reset doesn't affect
        // ExtractAction's countdown.
        if (squad.ExtractRequested)
        {
            newLocation = waypointSystem.FindNearestEligibleExfil(squad);
            if (newLocation != null)
            {
                Log.Debug($"{squad} ExtractRequested → routing to nearest eligible exfil {newLocation}");
            }
            else
            {
                // No eligible exfil left on the map. Fall back to normal
                // dispatch so the squad doesn't stall forever.
                Log.Warning($"{squad} ExtractRequested but no eligible exfil found, falling back to normal dispatch");
                newLocation = waypointSystem.RequestNear(squad, squad.Leader.Bot.Position, objective.LocationPrevious);
            }
        }
        else
        {
            // Leash dispatch to the in-progress main's anchor only when
            // the leader has drifted outside the roam radius. Inside the
            // leash the leader's current position drives RequestNear so
            // the squad keeps drifting freely on splinter loot / nearby
            // POIs. Outside the leash, bias the next dispatch toward the
            // main's anchor so the squad doesn't permanently wander off.
            var leaderPos = squad.Leader.Bot.Position;
            var pinAnchor = GetInProgressMainAnchor(squad);
            Vector3 requestPos;
            if (pinAnchor.HasValue)
            {
                var leash = Plugin.MainObjectivesRoamSplinterRadius.Value;
                // XZ-only — anchor.Y is 0 by construction (cell centre /
                // custom zone), so 3D Euclidean would wrongly snap the
                // leader back the moment a height mismatch crosses the
                // leash threshold (a bot looting in a Resort basement
                // is "50m away" from the cell anchor at Y=0 in 3D, but
                // 0m horizontally).
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

        // Successful dispatch — reset the islanded counter so we don't
        // pin a squad to its cell forever after one good streak of
        // failures.
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

        // Runtime Corpse waypoints (corpse-registration patch) ship with
        // an empty cover-points list because we don't run the cover
        // sampler mid-raid. Without this guard the Random.Range / indexer
        // / modulo below all explode and AssignNewObjective throws.
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
