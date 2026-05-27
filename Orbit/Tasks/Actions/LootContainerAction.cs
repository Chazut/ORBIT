using System.Collections.Generic;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Inventory;
using Orbit.Navigation;
using Orbit.Sain;
using Orbit.Systems;
using UnityEngine;

namespace Orbit.Tasks.Actions;

/// <summary>
/// Dispatch-layer entry point for the loot pipeline. When the goto
/// routine flips an agent into <see cref="ObjectiveStatus.Looting"/>,
/// this task takes over: spins up a <see cref="BotLootState"/>, ticks
/// the per-bot inventory engine, then on completion blacklists the POI
/// for the squad, fires the extract trigger, and either chains into a
/// scavenge sweep on a nearby item or hands back to the dispatcher.
/// </summary>
public class LootContainerAction(AgentData dataset, WaypointSystem waypointSystem, float hysteresis) : Task<Agent>(hysteresis)
{
    // Must beat GotoObjectiveAction's max (0.65) so the dispatcher keeps
    // the agent in this task while the loot is in progress.
    private const float UtilityScore = 0.75f;

    // Watchdog for the gap between Status=Looting being set and this
    // task actually picking the agent up. The dispatcher normally
    // switches within a frame; without this watchdog an agent can sit
    // silent for minutes if the brain layer's post-combat cooldown holds
    // it inert. 25s = 15s cooldown + ~10s scheduling margin.
    private const float LootingWatchdogSeconds = 25f;

    private readonly Dictionary<int, BotLootState> _states = new();
    private readonly Dictionary<int, float> _lootingStartTime = new();

    public override void UpdateScore(int ordinal)
    {
        var agents = dataset.Entities.Values;
        var now = Time.time;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            var isLooting = agent.Objective.Status == ObjectiveStatus.Looting;
            agent.TaskScores[ordinal] = isLooting ? UtilityScore : 0f;

            if (!isLooting)
            {
                _lootingStartTime.Remove(agent.Id);
                continue;
            }

            // Once we've started looting (BotLootState exists), the
            // watchdog stands down — BotLootState manages its own
            // completion/cancellation.
            if (_states.ContainsKey(agent.Id))
            {
                _lootingStartTime.Remove(agent.Id);
                continue;
            }

            if (!_lootingStartTime.TryGetValue(agent.Id, out var startedAt))
            {
                _lootingStartTime[agent.Id] = now;
                continue;
            }

            if (now - startedAt >= LootingWatchdogSeconds)
            {
                Log.Warning($"{agent} stuck at Status=Looting on {agent.Objective.Location} for {now - startedAt:F1}s without LootContainerAction taking over — failing objective");
                agent.Objective.Status = ObjectiveStatus.Failed;
                _lootingStartTime.Remove(agent.Id);
                agent.TaskScores[ordinal] = 0f;
            }
        }
    }

    public override void Update()
    {
        for (var i = 0; i < ActiveEntities.Count; i++)
            UpdateAgent(ActiveEntities[i]);
    }

    private void UpdateAgent(Agent agent)
    {
        var objective = agent.Objective;
        var location = objective.Location;

        // Defensive: the dispatcher shouldn't put us in this task
        // without a lootable Waypoint, but if something invalidated it,
        // fail clean.
        if (location == null || location.Target == null)
        {
            Log.Debug($"{agent} LootContainerAction: location/target null, failing");
            objective.Status = ObjectiveStatus.Failed;
            return;
        }

        if (!_states.TryGetValue(agent.Id, out var state))
        {
            Log.Debug($"{agent} LootContainerAction: starting loot on {location} (target={location.Target?.GetType().Name ?? "null"})");
            state = new BotLootState(agent, location);
            _states[agent.Id] = state;
            state.Begin();
        }

        state.Tick();

        if (!state.IsDone) return;

        if (state.Success)
        {
            switch (location.Category)
            {
                case WaypointCategory.LooseLoot:
                    if (state.ItemsTaken)
                    {
                        // Item physically picked up — purge the waypoint so
                        // no other bot tries (the world item is gone) and
                        // unblock squadmates already en-route.
                        waypointSystem.RemoveWaypoint(location.Id);
                        Log.Debug($"{agent} consumed LooseLoot {location}, waypoint removed globally");
                        if (agent.Squad != null)
                        {
                            agent.Squad.Objective.Duration = 0;
                            Log.Debug($"{agent.Squad} wait timer forced to expire for immediate redirect");
                        }
                    }
                    else
                    {
                        // Bot inspected the item but skipped it (below
                        // value threshold, inventory full, gear-pickup
                        // filter). The item is still in the world — another
                        // squad with a lower threshold might want it. Just
                        // blacklist for this squad so we don't loop on it.
                        agent.Squad?.CompletedPoiIds.Add(location.Id);
                        Log.Debug($"{agent} skipped LooseLoot {location} (no items taken), squad-blacklisted only");
                    }
                    break;
                case WaypointCategory.ContainerLoot:
                case WaypointCategory.Corpse:
                    // Squad has checked this container/corpse; whether
                    // something was taken or not, the squad knows what's
                    // in it now and shouldn't revisit.
                    agent.Squad?.CompletedPoiIds.Add(location.Id);
                    Log.Debug($"{agent} blacklisted {location} for {agent.Squad} (itemsTaken={state.ItemsTaken}, squad memory size={agent.Squad?.CompletedPoiIds.Count})");
                    break;
            }

            // Loot-value extract trigger: a single bot crossing the
            // faction-dependent loot threshold flips the whole squad
            // into ExtractRequested mode. From the next dispatch on,
            // the strategy bypasses normal POI selection and routes
            // straight to the nearest eligible exfil. One-way switch.
            CheckExtractTrigger(agent);
        }
        else
        {
            // Failed loot — blacklist the POI for the squad anyway. The
            // bot tried and couldn't complete (couldn't reach the item,
            // animation aborted, target became invalid, combat interrupt,
            // whatever). Without this blacklist the main-anchor priority
            // pick keeps grabbing the same broken POI every strategy
            // tick and the squad loops forever.
            if (agent.Squad != null && !agent.Squad.CompletedPoiIds.Contains(location.Id))
            {
                agent.Squad.CompletedPoiIds.Add(location.Id);
                Log.Debug($"{agent} blacklisted {location} for {agent.Squad} after FAILED loot (squad memory size={agent.Squad.CompletedPoiIds.Count})");
            }
        }

        // Scavenge sweep: if a LooseLoot/Corpse is sitting within ~10m
        // of where the bot just finished, chain to it directly instead
        // of releasing back to the dispatcher (which would pick a random
        // faraway POI). Mirrors the way a player naturally picks up
        // adjacent items before walking away.
        if (state.Success && TryScavengeSweep(agent, location))
        {
            waypointSystem.ReleaseClaim(location.Id, agent.Id);
            return;
        }

        waypointSystem.ReleaseClaim(location.Id, agent.Id);
        _states.Remove(agent.Id);
        objective.Status = state.Success ? ObjectiveStatus.Finished : ObjectiveStatus.Failed;
        Log.Debug($"{agent} loot complete: success={state.Success}, location={location}, claim released, status={objective.Status}");

        // Opportunistic-corpse interrupt resume. Only fires here (post-
        // ReleaseClaim) so we're certain the loot animation has actually
        // finished — triggering this from the strategy's first-Finished
        // branch would swap the squad's objective away from the corpse
        // mid-loot (followers claim-fail before the looter completes
        // their BotLootState anim) and abort the loot.
        //
        // Runs on BOTH success and failure: on combat-interrupt failure
        // the squad needs to point somewhere coherent post-combat, and
        // leaving PreInterrupt set would also block all future
        // opportunistic scans for this squad.
        if (location.Category == WaypointCategory.Corpse
            && agent.Squad?.PreInterruptObjectiveLocation != null)
        {
            var squad = agent.Squad;
            var resume = squad.PreInterruptObjectiveLocation;
            squad.PreInterruptObjectiveLocation = null;
            squad.Objective.LocationPrevious = squad.Objective.Location;
            squad.Objective.Location = resume;
            // Status=Active (not Wait) so the strategy's wait-timer
            // check doesn't fire AssignNewObjective and overwrite our
            // restored Location. UpdateAgents next tick realigns members.
            squad.Objective.Status = SquadObjectiveState.Active;
            squad.Objective.StartTime = Time.time;
            squad.Objective.Duration = 30f; // generous travel window
            squad.Objective.DurationAdjusted = false;
            Log.Info($"{agent} corpse interrupt {(state.Success ? "looted" : "aborted")} — {squad} resuming pre-interrupt objective {resume}");
        }
    }

    // Max sweep candidates considered per chain. 5 keeps the loop bounded
    // if every nearby item loses the coverage roll; at default 70%
    // coverage gives ~99.5% chance the chain ends on a kept item when
    // items are abundant.
    private const int ScavengeSweepMaxCandidates = 5;

    /// <summary>
    /// Sum the per-bot Looted across every currently-alive member of the
    /// squad. Used by the extract trigger so a 3-PMC squad with each
    /// member ~200k looted still extracts once the squad-total crosses
    /// the threshold. Dead members are silently excluded — they're
    /// already removed from squad.Members by the death pipeline.
    /// </summary>
    private static float SumSquadLootedAlive(Squad squad, out int aliveCount)
    {
        aliveCount = 0;
        if (squad == null) return 0f;
        var total = 0f;
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var m = squad.Members[i];
            if (m == null) continue;
            var go = m.Player?.gameObject;
            if (go == null) continue;
            var brain = go.GetComponent<LootBrain>();
            var looted = brain?.Stats?.Looted ?? 0f;
            total += looted;
            aliveCount++;
        }
        return total;
    }

    /// <summary>
    /// Re-evaluate the extract threshold for a squad even when no
    /// surviving member is currently looting. Called from the death
    /// hook so the squad doesn't get stuck at "almost-extract" because
    /// the threshold member died.
    /// </summary>
    public static void ReevaluateExtractForSquad(Squad squad)
    {
        if (squad == null || squad.ExtractRequested) return;
        if (squad.Members.Count == 0) return;
        CheckExtractTrigger(squad.Members[0]);
    }

    private static void CheckExtractTrigger(Agent agent)
    {
        var squad = agent.Squad;
        if (squad == null || squad.ExtractRequested) return;

        var totalLooted = SumSquadLootedAlive(squad, out var aliveCount);

        Log.Debug($"CheckExtractTrigger: {agent} squad-total Stats.Looted={totalLooted:N0}₽ across {aliveCount} alive members");

        var role = agent.Bot?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return;
        // Don't even arm the loot-value extract trigger for factions
        // that aren't allowed to extract at all.
        if (!(LootConfig.ExtractAllowedFor?.Value ?? ExtractFaction.All).IsBotEnabled(role.Value)) return;

        // Sum each alive member's OWN rolled threshold (per-brain
        // archetype for PMC, faction-default for PlayerScav). A mixed
        // Rat (200-500k) + Chad (1.5-2.5M) squad needs Rat-range +
        // Chad-range total, not 2× one of them. Death of a member drops
        // their contribution; survivors recalibrate.
        var sumThreshold = 0f;
        var resolvedCount = 0;
        var unresolvedCount = 0;
        var perMemberSummary = new System.Text.StringBuilder();
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var m = squad.Members[i];
            if (m?.Player?.gameObject == null) continue;
            var memberThreshold = GetOrResolveAgentExtractThreshold(m);
            if (memberThreshold <= 0f)
            {
                unresolvedCount++;
                continue;
            }
            sumThreshold += memberThreshold;
            resolvedCount++;
            if (perMemberSummary.Length > 0) perMemberSummary.Append(" + ");
            perMemberSummary.Append($"{memberThreshold / 1000f:F0}k");
        }
        // Wait until at least one alive member has a resolved threshold.
        // The next CheckExtractTrigger call (triggered by another loot,
        // by the squad-loot reevaluate hook, or by a death) will retry.
        if (resolvedCount == 0) return;
        if (sumThreshold <= 0f) return; // feature fully disabled (everyone has 0)
        if (totalLooted < sumThreshold) return;

        squad.ExtractRequested = true;
        squad.ExtractRequestedReason = $"loot ≥ {sumThreshold / 1000f:F0}k₽";
        Log.Info($"{squad}: {agent} hit extract threshold ({totalLooted:N0}₽ >= {sumThreshold:N0}₽ = {perMemberSummary} from {resolvedCount} resolved + {unresolvedCount} pending members) — squad will bee-line to nearest eligible exfil");
    }

    /// <summary>
    /// Lazily resolve the per-agent extract-loot threshold. Reads the
    /// agent's SAIN brain (PMC only, when SAIN personality is enabled)
    /// and rolls the archetype's range once; falls back to the global
    /// PMC / PlayerScav knob otherwise. Returns 0 when SAIN hasn't
    /// attached yet so the caller can retry on the next loot — avoids a
    /// forever-zero threshold for a bot that joined before SAIN finished
    /// attaching its BotComponent.
    /// </summary>
    private static float GetOrResolveAgentExtractThreshold(Agent agent)
    {
        if (agent.OwnExtractLootThreshold > 0f) return agent.OwnExtractLootThreshold;
        var role = agent.Bot?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return 0f;

        var isPmc = role.Value.IsPMC();
        var isPlayerScav = agent.Bot.Profile != null && agent.Bot.Profile.WillBeAPlayerScav();

        if (isPmc && (Plugin.SainPersonalityEnabled?.Value ?? false))
        {
            var brainName = SainPersonality.GetBrainName(agent.Bot);
            if (string.IsNullOrEmpty(brainName)) return 0f; // SAIN async-attach pending — retry later
            var archetype = SainPersonality.MapBrainToArchetype(brainName);
            agent.OwnExtractLootThreshold = PersonalityProfile.RollExtractThresholdFor(archetype);
            Log.Info($"{agent} resolved own extract threshold: brain='{brainName}' → {archetype} → {agent.OwnExtractLootThreshold:N0}₽");
            return agent.OwnExtractLootThreshold;
        }
        if (isPmc)
        {
            agent.OwnExtractLootThreshold = LootConfig.ExtractAtLootValuePmc;
            return agent.OwnExtractLootThreshold;
        }
        if (isPlayerScav)
        {
            agent.OwnExtractLootThreshold = LootConfig.ExtractAtLootValuePlayerScav?.Value ?? 0f;
            return agent.OwnExtractLootThreshold;
        }
        return 0f; // non-PMC non-playerscav — extract feature off
    }

    private bool TryScavengeSweep(Agent agent, Waypoint justLooted)
    {
        // ExtractRequested override: once the squad has decided to leave
        // (loot-value or time threshold hit, or all mains done), chaining
        // to a nearby loot is wrong — the bot should immediately route
        // to exfil via the next AssignNewObjective.
        if (agent.Squad != null && agent.Squad.ExtractRequested)
        {
            Log.Debug($"{agent} scavenge sweep: skipping — squad has ExtractRequested set");
            return false;
        }

        // Per-candidate coverage roll, retry-with-blacklist on miss.
        // Each nearby sweep candidate gets a Plugin.LootCoveragePct roll
        // — same dice as the main-loot cell-entry roll. A loser is
        // permanently blacklisted for the squad (CompletedPoiIds) so
        // the next FindNearbySweepTarget call sees it as already-
        // consumed and returns the next closest item. Caps at a few
        // retries to keep the loop bounded.
        var coverage = Mathf.Clamp01(Plugin.LootCoveragePct.Value);
        Waypoint next = null;
        var coverageDisabled = coverage >= 0.9999f;
        for (var attempt = 0; attempt < ScavengeSweepMaxCandidates; attempt++)
        {
            var sweepRadius = agent.Squad?.Personality != null
                ? agent.Squad.Personality.ScavengeSweepRadius
                : Plugin.ScavengeSweepRadius.Value;
            var candidate = waypointSystem.FindNearbySweepTarget(agent.Position, agent.Squad, sweepRadius);
            if (candidate == null) return false;
            if (coverageDisabled || Random.value < coverage)
            {
                next = candidate;
                break;
            }
            // Lost the roll — squad doesn't see this item. Permanent
            // squad blacklist so the dispatcher (and the next sweep
            // candidate lookup) skips it from now on.
            agent.Squad?.CompletedPoiIds.Add(candidate.Id);
            Log.Debug($"{agent} scavenge sweep coverage roll missed on {candidate} (coverage={coverage:P0}) — blacklisting and retrying");
        }
        if (next == null) return false;
        if (!waypointSystem.TryClaim(next.Id, agent.Id)) return false;

        // Hijack the squad objective so UpdateAgents doesn't snap the
        // agent back to the old POI on the next strategy tick. Keep the
        // wait timer long enough that the squad doesn't ask for a fresh
        // objective while we're chaining (60s covers travel + a second
        // loot animation).
        if (agent.Squad != null)
        {
            agent.Squad.Objective.LocationPrevious = agent.Squad.Objective.Location;
            agent.Squad.Objective.Location = next;
            agent.Squad.Objective.Duration = 60f;
            agent.Squad.Objective.StartTime = Time.time;
        }
        agent.Objective.Location = next;
        // Status=None (NOT Looting). The old behaviour set Status=Looting
        // and immediately spun up a new BotLootState at the bot's
        // current position — so the loot animation played in place even
        // when the next sweep target was 8-10m away, totally unrealistic.
        // Setting Status=None makes GotoObjectiveAction.Update submit a
        // normal MoveToByPath toward `next` on the next tick; the bot
        // physically walks there, then the in-radius shortcircuit (or
        // the normal arrival branch) flips Status=Looting and we take
        // over again. For sweeps where the bot is already within the
        // 1m arrival radius the in-radius shortcircuit is immediate.
        agent.Objective.Status = ObjectiveStatus.None;
        // Drop any prior BotLootState — the new loot pass will allocate
        // its own once arrival fires.
        _states.Remove(agent.Id);

        var dist = Vector3.Distance(justLooted.Position, next.Position);
        Log.Debug($"{agent} scavenge sweep: chaining {justLooted} → {next} ({dist:F1}m, walking there)");
        return true;
    }

    protected override void Deactivate(Agent entity)
    {
        // Combat takeover or external task switch — abort the loot
        // operation cleanly (close container if open, cancel pending
        // async work).
        if (_states.TryGetValue(entity.Id, out var state))
        {
            Log.Debug($"{entity} LootContainerAction.Deactivate: cancelling in-flight loot");
            state.Cancel();
            _states.Remove(entity.Id);
        }

        var location = entity.Objective.Location;
        if (location != null)
            waypointSystem.ReleaseClaim(location.Id, entity.Id);

        // Reset to None so the bot can re-attempt later (or pick a new
        // objective) if this task gets re-activated. Terminal statuses
        // are left alone.
        if (entity.Objective.Status == ObjectiveStatus.Looting)
        {
            Log.Debug($"{entity} LootContainerAction.Deactivate: resetting Looting → None");
            entity.Objective.Status = ObjectiveStatus.None;
        }
    }
}

// ── Per-bot loot state ──────────────────────────────────────────────
//
// Wraps the MonoBehaviour LootBrain attached to the bot's GameObject,
// sets up the target, kicks off the async loot, and reports completion
// back to the dispatch-layer action.

internal class BotLootState
{
    // Hard cap on how long we'll wait for LootBrain.LootTaskRunning to
    // clear. The brain has its own internal timeout (LootConfig.LootTimeout
    // default 180s) backed by a CancellationTokenSource — but if
    // something inside doesn't honour the cancel (a hung NavMesh
    // callback, a coroutine waiting on a never-fired event),
    // LootTaskRunning stays true forever and the bot is pinned mid-loot.
    // Add 30s of buffer over the brain's own timeout and treat anything
    // past that as a fatal cancel.
    private static float StuckLootTimeoutSeconds => LootConfig.LootTimeout + 30f;

    private readonly Agent _agent;
    private readonly Waypoint _location;
    private readonly LootBrain _brain;
    private float _lootedAtStart;
    private float _beganAt;
    private bool _started;

    public bool IsDone { get; private set; }
    public bool Success { get; private set; }

    /// <summary>
    /// True iff the bot actually took at least one item during this
    /// operation. Used by LootContainerAction to distinguish "consumed
    /// the item" from "skipped because below value threshold" — the
    /// latter must leave the world POI alone so other squads with
    /// different thresholds can still try.
    /// </summary>
    public bool ItemsTaken { get; private set; }

    public BotLootState(Agent agent, Waypoint location)
    {
        _agent = agent;
        _location = location;

        var go = agent.Player.gameObject;
        _brain = go.GetComponent<LootBrain>();
        if (_brain == null)
        {
            _brain = go.AddComponent<LootBrain>();
            _brain.Init(agent.Bot);
        }
    }

    public void Begin()
    {
        var lootKind = _location.Category switch
        {
            WaypointCategory.Corpse => LootKind.Corpse,
            WaypointCategory.ContainerLoot => LootKind.Container,
            WaypointCategory.LooseLoot => LootKind.Item,
            _ => LootKind.None
        };

        // LootBrain expects an InteractableObject — Exfil targets share
        // a common MonoBehaviour base with lootables but aren't
        // InteractableObject, so cast explicitly and bail if it fails.
        var loot = _location.Target as InteractableObject;
        if (lootKind == LootKind.None || loot == null)
        {
            Log.Debug($"{_agent} BotLootState.Begin: invalid lootKind ({lootKind}) or non-InteractableObject target");
            IsDone = true;
            Success = false;
            return;
        }

        // Locked containers (key/keycard required, e.g. weapon cases
        // inside locked resort rooms) can't be opened by a bot. The
        // LootBrain would sit on the open animation forever. Skip
        // cleanly — POI gets squad-blacklisted upstream so we don't
        // keep returning to it.
        if (loot is LootableContainer container
            && container.DoorState == EDoorState.Locked)
        {
            Log.Info($"{_agent} BotLootState.Begin: {container.name} is Locked, skipping (squad will blacklist)");
            IsDone = true;
            Success = false;
            return;
        }

        _brain.ActiveLoot = loot;
        _brain.ActiveLootType = lootKind;
        _brain.Destination = _location.Position;
        _brain.LootObjectPosition = loot.transform.position;
        _brain.ForceBrainEnabled = true;

        // Snapshot cumulative looted value so we can tell at the end
        // whether the bot actually pocketed anything (vs. opening the
        // container and skipping everything because items were below
        // the value threshold).
        _lootedAtStart = _brain.Stats != null ? _brain.Stats.Looted : 0f;

        // Immersion: crouch and face the loot before kicking off the
        // transaction. Wrapped defensively because some BotOwner
        // specialisations (boss followers, event bots) don't expose
        // every steering/mover hook.
        try
        {
            _agent.Bot.SetPose(0.25f);
            _agent.Bot.Steering.LookToPoint(loot.transform.position);
            if (_agent.Bot.Mover != null) _agent.Bot.Mover.Sprint(false);
        }
        catch (System.Exception e)
        {
            Log.Debug($"{_agent} BotLootState.Begin: posture/look setup failed (non-fatal): {e.Message}");
        }

        _brain.StartLooting();
        _started = true;
        _beganAt = Time.time;
        Log.Debug($"{_agent} BotLootState.Begin: StartLooting() invoked, kind={lootKind}, target={_location.Target.name}, lootedAtStart={_lootedAtStart:N0}₽");
    }

    public void Tick()
    {
        if (IsDone) return;
        if (!_started) return;

        // Target destroyed mid-loot — e.g. another player picked up the
        // loose item, a corpse despawned, a container was disabled by a
        // scripted event. Bail cleanly so we don't kneel forever at a
        // ghost.
        if (_location.Target == null)
        {
            Log.Info($"{_agent} BotLootState: target destroyed mid-loot — cancelling");
            Cancel();
            return;
        }

        // Brain-watchdog: LootBrain's own CancellationTokenSource caps
        // the async task at LootConfig.LootTimeout, but if a coroutine
        // inside doesn't honour cancel (hung handbook lookup, vanished
        // interaction hook), LootTaskRunning stays true and the bot is
        // pinned. Force-cancel 30s past the brain's own timeout.
        if (Time.time - _beganAt > StuckLootTimeoutSeconds)
        {
            Log.Warning($"{_agent} BotLootState: brain still running after {Time.time - _beganAt:F0}s (>{StuckLootTimeoutSeconds}s cap) — force-cancelling");
            Cancel();
            return;
        }

        // Defensive combat interrupt: if the bot's BSG memory becomes
        // aware of an enemy mid-loot, bail immediately. The brain layer
        // will also return !IsActive on the next tick and the layer
        // change will route through Deactivate → Cancel anyway, but the
        // layer-change path takes 1-2 frames during which the loot
        // animation keeps the bot pinned in the open. Cancelling here
        // closes the container right away so SAIN takes over a free
        // agent, not a kneeling target.
        var mem = _agent.Bot?.Memory;
        if (mem != null && (mem.HaveEnemy || mem.IsUnderFire))
        {
            Log.Info($"{_agent} BotLootState: combat interrupt mid-loot (HaveEnemy={mem.HaveEnemy}, IsUnderFire={mem.IsUnderFire}) — cancelling");
            Cancel();
            return;
        }

        // LootBrain clears LootTaskRunning when the async loot finishes
        // (success OR timeout/cancellation). Compare Stats.Looted before
        // and after to determine whether anything was actually taken.
        if (!_brain.LootTaskRunning)
        {
            var lootedNow = _brain.Stats != null ? _brain.Stats.Looted : 0f;
            ItemsTaken = lootedNow > _lootedAtStart;
            Log.Debug($"{_agent} BotLootState.Tick: brain.LootTaskRunning=false → done, lootedDelta={(lootedNow - _lootedAtStart):N0}₽, ItemsTaken={ItemsTaken}");
            IsDone = true;
            Success = true;
            CleanupBrainTarget();
        }
    }

    public void Cancel()
    {
        // If combat or any other task is hijacking the bot mid-loot, the
        // container we cracked open stays visually open forever — looks
        // weird and confuses both players and the squad blacklist
        // heuristic (a passing teammate would see "already opened, must
        // be empty"). Close it explicitly before bailing.
        if (_brain.ActiveLoot is LootableContainer container)
        {
            try
            {
                var log = new BotLog(_agent.Bot);
                LootHelpers.InteractContainer(container, _agent.Bot, EFT.EInteractionType.Close, log);
            }
            catch (System.Exception e)
            {
                Log.Warning($"BotLootState.Cancel: failed to force-close container {container.name}: {e.Message}");
            }
        }
        _brain.StopLooting();
        CleanupBrainTarget();
        IsDone = true;
        Success = false;
    }

    private void CleanupBrainTarget()
    {
        _brain.ActiveLoot = null;
        _brain.ActiveLootType = LootKind.None;
        _brain.ForceBrainEnabled = false;
    }
}
