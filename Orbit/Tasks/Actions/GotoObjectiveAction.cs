using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Looting;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

namespace Orbit.Tasks.Actions;

/// <summary>
/// Drives a bot from wherever they are to their currently-assigned
/// <see cref="Waypoint"/>. Owns the arrival logic: strict-radius gate
/// with line-of-sight check (no looting through walls), nav-snap rescue for the case where BSG's mover
/// stopped 1.5-2.5m off the exact point, status transition to Looting/Extracting/Finished, and per-agent
/// blacklist when the same POI keeps failing.
/// </summary>
public class GotoObjectiveAction(AgentData dataset, MovementSystem movementSystem, WaypointSystem waypointSystem, float hysteresis) : Task<Agent>(hysteresis)
{
    private const float UtilityBase = 0.5f;
    private const float UtilityBoost = 0.15f;
    private const float UtilityBoostMaxDistSqr = 50f * 50f;

    // Sprint only when far enough that running is worth the awareness penalty. Within 50m of the objective we
    // walk: the bot's vision sensor updates more often when not sprinting, letting it spot enemies it would
    // otherwise blast past.
    private const float WalkApproachDistanceSqr = 50f * 50f;

    // BSG nav-snap arrival rescue. The bot's path consumes at the nearest navmesh node, which can sit
    // 1.5-2.5m off the exact Waypoint.Position. With the strict 1m POI arrival radius we'd otherwise time out
    // at
    // 1.0-2.5m without ever flipping to Looting. 4m is the upper bound
    // where the LoS check still makes sense.
    private const float NavSnapArrivalRadius = 4f;
    private const float NavSnapArrivalRadiusSqr = NavSnapArrivalRadius * NavSnapArrivalRadius;

    /// <summary>
    /// Grace window after dispatch before any "stopped outside arrival radius" failure can fire. BSG's
    /// BotMover takes a frame or two to begin the new move; if the agent was Stopped (Guard / loot
    /// session / etc.) right before dispatch, our Status==Stopped check would fire immediately and
    /// register a fail count without the bot having moved at all. 2 s is generous enough to give BotMover
    /// time to start moving, conservative enough that a genuinely-failed path still fails quickly.
    /// </summary>
    private const float DispatchGraceSeconds = 2f;

    /// <summary>
    /// Seconds an agent can spend "within the loose 15 m exfil radius but outside the actual trigger
    /// collider" before we force the despawn from their current position. 15 s gives the bot plenty of
    /// chances to descend a hatch / walk around the entry, but caps the worst case (Илюха Травмат on
    /// Customs stood 2 m 30 s before SAIN combat took over).
    /// </summary>
    private const float ExfilOutsideTriggerForceExtractSeconds = 15f;

    // Per-agent stuck watchdog on Exfil objectives: if the bot's position barely moves for this duration
    // while targeting an exfil, blacklist the exfil and re-pick. Walking from across the map is fine — only
    // genuinely stationary bots trip the watchdog.
    private const float ExfilStuckThresholdSeconds = 30f;
    private const float ExfilStuckMoveDistSqr = 2f * 2f;
    private readonly System.Collections.Generic.Dictionary<int, (int locId, Vector3 lastPos, float lastMoveTime)> _exfilStuckTracker = new();

    public override void UpdateScore(int ordinal)
    {
        var agents = dataset.Entities.Values;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            var location = agent.Objective.Location;

            // Release control when the objective has terminated OR moved into a phase owned by another action
            // (LootContainerAction handles Looting, ExtractAction handles Extracting). Without these two
            // extra exclusions the nav-snap arrival rescue accepts arrival 1-4m off the POI, flips status to
            // Looting, but our distSqr is still > RadiusSqr — so utilityDecay clamps to 1.0 and our score
            // (0.65) + Hysteresis (0.15) =
            // 0.80 still beats LootContainerAction's 0.75. Bot then sits
            // frozen at Status=Looting until the loot watchdog times out
            // 25s later, producing a "bot stops at a point without an
            // objective" symptom.
            if (location == null || agent.Objective.Status is ObjectiveStatus.Failed
                                                          or ObjectiveStatus.Finished
                                                          or ObjectiveStatus.Looting
                                                          or ObjectiveStatus.Extracting)
            {
                agent.TaskScores[ordinal] = 0;
                continue;
            }

            // Baseline 0.5f, boosted to 0.65f as the bot gets nearer. Once within the objective radius,
            // utility falls off sharply.
            var distSqr = (location.Position - agent.Position).sqrMagnitude;

            var utilityBoostFactor = Mathf.InverseLerp(UtilityBoostMaxDistSqr, location.RadiusSqr, distSqr);
            // Exfil keeps full score inside the arrival radius. The proximity decay exists so the
            // arrival handover (Loot / Guard) can outbid Goto, but an exfil has no handover: either
            // the bot gets inside the trigger (status flips to Extracting → score 0 above) or it must
            // keep pushing in while the outside-trigger force-extract timer runs — and that timer
            // ticks in Update(), ActiveEntities only. With decay, entering the generous 15 m radius
            // collapsed the score under GuardAction's in-radius 0.65, Goto deactivated, and the timer
            // froze forever. User-reported case: rodentmessiah armed the timer at
            // exit_var2_constant_tunnel then guard-swept beside the exfil until raid end.
            var utilityDecay = location.Category == WaypointCategory.Exfil
                ? 1f
                : Mathf.InverseLerp(0f, location.RadiusSqr, distSqr);

            agent.TaskScores[ordinal] = utilityDecay * (UtilityBase + utilityBoostFactor * UtilityBoost);
        }
    }

    public override void Update()
    {
        for (var i = 0; i < ActiveEntities.Count; i++)
        {
            var agent = ActiveEntities[i];
            var objective = agent.Objective;

            if (objective.Location == null) continue;

            switch (objective.Status)
            {
                case ObjectiveStatus.None:
                    Log.Debug($"{agent} received new objective {agent.Objective.Location}, submitting move order");
                    var startDistSqr = (objective.Location.Position - agent.Position).sqrMagnitude;
                    var shouldSprint = ShouldSprintToObjective(agent, startDistSqr);
                    movementSystem.MoveToByPath(agent, objective.Location.Position, sprint: shouldSprint);
                    objective.Status = ObjectiveStatus.Moving;
                    break;
                case ObjectiveStatus.Moving:
                    if (agent.Movement.HasPath && objective.ArrivalPath != agent.Movement.Path)
                        objective.ArrivalPath = agent.Movement.Path;

                    var distanceSqr = (objective.Location.Position - agent.Position).sqrMagnitude;

                    // Exfil stuck watchdog: blacklist + re-pick when the bot has stopped moving while
                    // targeting an exfil.
                    if (objective.Location.Category == WaypointCategory.Exfil)
                    {
                        if (!_exfilStuckTracker.TryGetValue(agent.Id, out var t)
                            || t.locId != objective.Location.Id)
                        {
                            _exfilStuckTracker[agent.Id] = (objective.Location.Id, agent.Position, Time.time);
                        }
                        else if ((agent.Position - t.lastPos).sqrMagnitude > ExfilStuckMoveDistSqr)
                        {
                            _exfilStuckTracker[agent.Id] = (t.locId, agent.Position, Time.time);
                        }
                        else if (Time.time - t.lastMoveTime > ExfilStuckThresholdSeconds)
                        {
                            if (agent.Squad != null && !agent.Squad.CompletedPoiIds.Contains(objective.Location.Id))
                            {
                                agent.Squad.CompletedPoiIds.Add(objective.Location.Id);
                            }
                            objective.Status = ObjectiveStatus.Failed;
                            _exfilStuckTracker.Remove(agent.Id);
                            Log.Info($"{agent} stuck en-route to {objective.Location} for {ExfilStuckThresholdSeconds:F0}s — blacklisted for {agent.Squad}, rerouting");
                            break;
                        }
                    }
                    else if (_exfilStuckTracker.ContainsKey(agent.Id))
                    {
                        _exfilStuckTracker.Remove(agent.Id);
                    }

                    // Stop sprinting once inside the 50m "scan" radius so the bot has time to spot enemies on
                    // the final approach. Personality override: agents with sprint propensity ~1.0 (e.g.
                    // VeryAggressive GigaChad) keep sprinting all the way in.
                    var sprintPropensity = agent.Squad?.Personality?.SprintPropensity ?? 0.5f;
                    if (agent.Movement.Sprint
                        && distanceSqr < WalkApproachDistanceSqr
                        && sprintPropensity < 0.999f)
                    {
                        MovementSystem.ResetGait(agent);
                    }

                    // Arrival gate — two-stage check that both prevents looting through walls AND tolerates
                    // BSG's nav-snap drift:
                    //
                    //   1. Within the strict 1m radius → still require a
                    // Physics.Raycast LoS clear between the bot's head and the POI. A bot standing 0.9m from
                    // a loot pile on the OTHER side of a wall would otherwise pass the euclidean check and
                    // loot through the wall. Uses the same HighPolyWithTerrainMask BSG uses natively — tests
                    // real 3D world geometry, not navmesh edges.
                    //
                    //   2. Between 1m and 4m, AND bot is Stopped (BSG
                    // reports "destination reached" but stopped on the nearest navmesh node, 1.5-2.5m off the
                    // exact Waypoint.Position) → same Physics raycast LoS check. BSG nav-snap rescue path.
                    //
                    // Lootables only — Synthetic / Exfil still use the wide radius without LoS (those don't
                    // suffer from through-wall validation).
                    // Exfil stuck timer hygiene: if the bot has walked OUT of the exfil radius (combat
                    // pulled them away, etc.) clear the outside-trigger timer so the next re-entry gets
                    // a fresh 15 s window. Only the Exfil category uses the timer; other categories' field
                    // stays at -1 forever.
                    if (objective.Location.Category == WaypointCategory.Exfil
                        && distanceSqr > objective.Location.RadiusSqr)
                    {
                        objective.ExfilOutsideTriggerSince = -1f;
                    }

                    var inRadius = false;
                    if (distanceSqr <= objective.Location.RadiusSqr)
                    {
                        if (RequiresArrivalLoSCheck(objective.Location.Category))
                        {
                            if (HasArrivalLineOfSight(agent, objective.Location.Position))
                            {
                                inRadius = true;
                                ClearLoSBlockedTracking(agent);
                            }
                            else
                            {
                                Log.Debug($"{agent} within {Mathf.Sqrt(distanceSqr):F1}m of {objective.Location} but Physics raycast BLOCKED — wall in between, holding off arrival");
                                if (TrackLoSBlocked(agent, objective.Location)) continue;
                            }
                        }
                        else
                        {
                            inRadius = true;
                            ClearLoSBlockedTracking(agent);
                        }
                    }
                    else if (agent.Movement.Status == MovementStatus.Stopped
                             && distanceSqr <= NavSnapArrivalRadiusSqr
                             && RequiresArrivalLoSCheck(objective.Location.Category)
                             && HasArrivalLineOfSight(agent, objective.Location.Position))
                    {
                        Log.Debug($"{agent} BSG nav-snap arrival rescue: stopped {Mathf.Sqrt(distanceSqr):F1}m off {objective.Location} but Physics raycast clear → accepting arrival");
                        inRadius = true;
                        ClearLoSBlockedTracking(agent);
                    }
                    else
                    {
                        ClearLoSBlockedTracking(agent);
                    }
                    if (inRadius)
                    {
                        // If this is a lootable POI and we can grab the claim, chain straight into Looting
                        // state. Otherwise (claim held, or non-lootable category) fall through to Finished —
                        // the squad waits here and another task can run.
                        if (IsLootableForAgent(agent, objective.Location))
                        {
                            if (waypointSystem.TryClaim(objective.Location.Id, agent.Id))
                            {
                                objective.Status = ObjectiveStatus.Looting;
                                // Looting keeps the bot stationary for several seconds. If we leave the path
                                // pointing at the previous destination, BSG's BotMover eventually decides the
                                // bot is "stuck" relative to its last good cast point and teleports it away —
                                // sometimes 200m+. Re-aim at current pos so BSG re-links here and resets its
                                // rescue timer.
                                movementSystem.MoveToByPath(agent, agent.Position, sprint: false, urgency: MovementUrgency.Low);
                                Log.Debug($"{agent} arrived at lootable {objective.Location}, claim OK → Looting (path refreshed to {agent.Position})");
                            }
                            else
                            {
                                objective.Status = ObjectiveStatus.Finished;
                                Log.Debug($"{agent} arrived at {objective.Location}, claim DENIED (already held), staying as backup");
                            }
                        }
                        else if (objective.Location.Category == WaypointCategory.Exfil
                                 && objective.Location.Target is ExfiltrationPoint exfil)
                        {
                            // The 15 m exfil radius is generous on purpose (BSG nav-snap drift, large
                            // trigger volumes), but it lets bots "extract" while standing at the surface
                            // above an underground exfil whose registered transform.position is at the
                            // hatch but whose actual trigger volume goes below ground. Add a collider-
                            // contains check so the bot has to be PHYSICALLY inside the trigger volume,
                            // not just XZ-close. If the bot is outside the volume, keep walking — the
                            // arrival radius still bounded re-dispatch via TrackArrivalFailure when the
                            // BSG nav can't get them in (3-fail blacklist still applies, the squad picks
                            // a different exfil).
                            if (!IsAgentInsideExfilTrigger(agent, exfil))
                            {
                                // Bot is within the loose arrival radius but outside the actual trigger
                                // volume. The TrackArrivalFailure path is fragile here — if the agent
                                // happens to re-dispatch between misses (sweep, splinter, anything that
                                // touches LastFailedPoiId) the consecutive counter resets to 1 and never
                                // reaches the 3-fail force-extract trigger; field repro: Илюха Травмат
                                // on Customs, 1 arrival miss logged, then bot stood at the exfil for 2 m
                                // 30 s before SAIN combat took over. Replace with a dedicated stuck
                                // timer: start counting on first miss, force-extract after N s of being
                                // continuously "within radius + outside trigger". Resets only when the
                                // agent actually exits the radius or enters the trigger.
                                if (objective.ExfilOutsideTriggerSince < 0f)
                                {
                                    objective.ExfilOutsideTriggerSince = Time.time;
                                    Log.Debug($"{agent} within {Mathf.Sqrt(distanceSqr):F1}m of {objective.Location} but outside the trigger collider — counting as arrival miss (force-extract timer armed)");
                                }
                                else if (Time.time - objective.ExfilOutsideTriggerSince >= ExfilOutsideTriggerForceExtractSeconds)
                                {
                                    ActivateExfilForBot(exfil, agent);
                                    objective.Status = ObjectiveStatus.Extracting;
                                    objective.ExfilOutsideTriggerSince = -1f;
                                    Log.Info($"{agent} couldn't reach inside of {objective.Location} after {ExfilOutsideTriggerForceExtractSeconds:F0}s outside trigger — forcing extract from current position (exfil status={exfil.Status})");
                                    break;
                                }
                                break;
                            }
                            // Bot actually inside the trigger — reset the stuck timer if it was armed
                            // from a previous bounce-around-the-edge attempt.
                            objective.ExfilOutsideTriggerSince = -1f;
                            // Activate the exfil if still gated behind requirements (V-Ex / pay-to-leave),
                            // then hand off to ExtractAction which waits the countdown and despawns the bot.
                            ActivateExfilForBot(exfil, agent);
                            objective.Status = ObjectiveStatus.Extracting;
                            movementSystem.MoveToByPath(agent, agent.Position, sprint: false, urgency: MovementUrgency.Low);
                            Log.Info($"{agent} arrived at {objective.Location} → Extracting (exfil status={exfil.Status})");
                        }
                        else
                        {
                            objective.Status = ObjectiveStatus.Finished;
                            Log.Debug($"{agent} arrived at non-lootable {objective.Location} → Finished");
                        }
                        break;
                    }

                    if (agent.Movement.Status == MovementStatus.Failed)
                    {
                        // MovementSystem's HardStuck escalation ran out (recalc → teleport → giving up) and
                        // reset path to Failed. The bot is jammed against geometry.
                        objective.Status = ObjectiveStatus.Failed;
                        Log.Debug($"{agent} movement Failed en-route to {objective.Location} — HardStuck giving up");
                        TrackArrivalFailure(agent, objective.Location);
                    }
                    // Stuck-at-destination guard: BSG's BotMover reports Reached but our euclidean radius
                    // check above failed (bot stopped just outside the arrival radius). If the bot stays in
                    // this in-between state too long it would never advance — fail the agent objective so the
                    // wait-timer / re-dispatch path kicks in immediately. Grace window: skip the failure
                    // trigger if the agent was dispatched less than DispatchGraceSeconds ago — Movement.Status
                    // == Stopped lingers from a prior Guard or other paused state on the first tick after
                    // dispatch, and we'd otherwise fail arrival before the bot has even started moving toward
                    // the new POI (HARDcore on Customs: 3 frames from assignment to "failing arrival",
                    // triggering 3 fail counts in 0.7s and blacklisting the POI before the bot got 1 m closer).
                    else if (agent.Movement.Status == MovementStatus.Stopped
                             && Time.time - objective.DispatchTime > DispatchGraceSeconds)
                    {
                        objective.Status = ObjectiveStatus.Failed;
                        Log.Debug($"{agent} stopped outside {objective.Location} arrival radius ({Mathf.Sqrt(distanceSqr):F1}m / {Mathf.Sqrt(objective.Location.RadiusSqr):F1}m) — failing objective to unblock re-dispatch");
                        TrackArrivalFailure(agent, objective.Location);
                    }

                    break;
                case ObjectiveStatus.Finished:
                case ObjectiveStatus.Failed:
                default:
                    break;
            }
        }
    }

    protected override void Deactivate(Agent entity)
    {
        if (entity.Objective.Status is ObjectiveStatus.Finished or ObjectiveStatus.Failed
                                    or ObjectiveStatus.Looting or ObjectiveStatus.Extracting)
            return;

        // Reset the status if the bot wasn't failed/finished — otherwise we won't resubmit the move order the
        // next time we're activated.
        entity.Objective.Status = ObjectiveStatus.None;
    }

    // Sprint-to-objective decision. Non-sprinting factions (scavs, Timmy) are filtered by SprintGate; PMCs
    // sprint when far and walk on the final approach, with the walk window shrinking as SprintPropensity
    // rises. Combat sprint is decided by SAIN.
    private static bool ShouldSprintToObjective(Agent agent, float startDistSqr)
    {
        if (!SprintGate.IsAllowedByFaction(agent)) return false;
        var propensity = agent.Squad?.Personality?.SprintPropensity ?? 0.5f;
        if (propensity >= 0.999f) return true;
        var walkApproachScale = 1.5f - propensity;
        var effectiveWalkSqr = WalkApproachDistanceSqr * walkApproachScale * walkApproachScale;
        return startDistSqr > effectiveWalkSqr;
    }

    // Maximum time an agent is allowed to stand within the arrival radius of a Lootable/Quest POI with the
    // Physics raycast continuously blocked before we give up on that POI and blacklist it for the squad. Quest
    // mains in particular have no other timeout — the trotil-drop point sitting inside a wall would otherwise
    // pin the bot for the whole raid.
    private const float LoSBlockedTimeoutSeconds = 10f;

    /// <summary>
    /// Track how long this agent has been "within radius but LoS blocked" on the current POI. When that window
    /// exceeds <see cref="LoSBlockedTimeoutSeconds"/>, blacklist the POI for the squad and clear the agent's
    /// pinned target so the next dispatch tick picks a new one. Returns true when the blacklist fired and the
    /// caller should skip the rest of the current arrival-resolution branch.
    /// </summary>
    private static bool TrackLoSBlocked(Agent agent, Waypoint location)
    {
        if (agent == null || location == null || agent.Squad == null) return false;
        var locId = location.Id;
        if (agent.LoSBlockedPoiId != locId)
        {
            agent.LoSBlockedPoiId = locId;
            agent.LoSBlockedSinceTime = Time.time;
            return false;
        }
        if (Time.time - agent.LoSBlockedSinceTime < LoSBlockedTimeoutSeconds) return false;
        agent.Squad.CompletedPoiIds.Add(locId);
        if (agent.Squad.Objective.Location != null
            && agent.Squad.Objective.Location.Id == locId)
        {
            agent.Squad.Objective.Location = null;
        }
        agent.Objective.Location = null;
        agent.Objective.SplinterParent = null;
        agent.Objective.Status = ObjectiveStatus.None;
        Log.Info($"{agent} blacklisting {location} for {agent.Squad} after {LoSBlockedTimeoutSeconds:F0}s of LoS-blocked arrival (target appears to be inside a wall) — cleared agent + squad target to force re-dispatch");
        ClearLoSBlockedTracking(agent);
        return true;
    }

    private static void ClearLoSBlockedTracking(Agent agent)
    {
        if (agent == null) return;
        agent.LoSBlockedPoiId = -1;
        agent.LoSBlockedSinceTime = -1f;
    }

    // Per-agent blacklist on repeated arrival failures for the same POI. The squad-level
    // ConsecutiveFailedDispatches only fires when ALL members fail at once; a single member stuck on an
    // unreachable splinter while squadmates loot fine never triggers it. Two failure modes feed in: "stopped
    // outside arrival radius" (navmesh sample lands 4m+ off) AND HardStuck "giving up" (bot jammed en-route).
    private void TrackArrivalFailure(Agent agent, Waypoint location)
    {
        if (location == null || agent?.Squad == null) return;
        var locId = location.Id;
        if (agent.LastFailedPoiId == locId)
        {
            agent.ConsecutiveSamePoiFailures++;
        }
        else
        {
            agent.LastFailedPoiId = locId;
            agent.ConsecutiveSamePoiFailures = 1;
        }
        if (agent.ConsecutiveSamePoiFailures >= 3)
        {
            // Exfil special case: the squad has already committed to extracting (ExtractRequested set,
            // bee-line in progress). If the bot can't physically enter the trigger volume after 3
            // attempts (locked door, blocked hatch, nav path can't descend), force the despawn from
            // wherever the bot is standing rather than blacklisting + re-dispatching to a different
            // exfil — at that point the bot is essentially camping the entry, sending them off to
            // wander to another exfil halfway across the map is worse UX than just letting them leave.
            if (location.Category == WaypointCategory.Exfil
                && location.Target is ExfiltrationPoint exfil
                && agent.Squad.ExtractRequested)
            {
                ActivateExfilForBot(exfil, agent);
                agent.Objective.Status = ObjectiveStatus.Extracting;
                Log.Info($"{agent} couldn't reach inside of {location} after 3 attempts — forcing extract from current position ({agent.Position}, exfil status={exfil.Status})");
                agent.ConsecutiveSamePoiFailures = 0;
                agent.LastFailedPoiId = -1;
                return;
            }

            agent.Squad.CompletedPoiIds.Add(locId);
            // Adding to CompletedPoiIds only filters FUTURE picks; the current dispatch still has
            // agent.Objective.Location pinned at the bad POI (set by AssignNewObjective, by a follower
            // splinter pick, or by the loot routine's scavenge sweep which pins squad.Objective.Location at
            // the chain target for 60s). If we don't break both pins explicitly the bot oscillates Goto →
            // Failed → reset to None → Goto re-submits move order toward the same POI → fails again, every
            // ~1s, even though the blacklist is already armed.
            if (agent.Squad.Objective.Location != null
                && agent.Squad.Objective.Location.Id == locId)
            {
                agent.Squad.Objective.Location = null;
            }
            agent.Objective.Location = null;
            agent.Objective.SplinterParent = null;
            agent.Objective.Status = ObjectiveStatus.None;
            // Record the blacklist firing so the rapid-POI-churn detector can fire the close-doors
            // remediation when this agent is bouncing across MULTIPLE POIs (the per-POI 3-fail counter
            // alone never catches that pattern — it resets when the agent switches POIs).
            movementSystem.RegisterPoiBlacklistAndMaybeCloseDoors(agent);
            Log.Info($"{agent} blacklisting {location} for {agent.Squad} after 3 consecutive arrival failures (cleared agent + squad target to force re-dispatch)");
            agent.ConsecutiveSamePoiFailures = 0;
            agent.LastFailedPoiId = -1;
        }
    }

    /// <summary>
    /// True when the agent is physically inside the exfil's trigger collider bounds (or no collider is
    /// available, in which case we fall back to the existing radius check upstream). Catches the case
    /// where the registered transform.position is at the surface but the actual trigger volume is below
    /// ground (or vice versa): the bot would otherwise extract from outside the real zone.
    /// </summary>
    private static bool IsAgentInsideExfilTrigger(Agent agent, ExfiltrationPoint exfil)
    {
        var collider = exfil?.GetComponent<Collider>();
        if (collider == null) return true; // can't verify → defer to the existing radius check
        var pos = agent.Position;
        // Bounds.Contains is AABB which is loose for rotated colliders, but exfil triggers are usually
        // axis-aligned BoxColliders so this is exact enough. Closer-point would be more precise but
        // costs more and isn't worth it for ~5-15 m volumes.
        return collider.bounds.Contains(pos);
    }

    // Mirrors BSG's ActivateExfil flow: forces a still-gated exfil into a usable state right when the bot
    // arrives. Without this, V-Ex / pay exfils stay at UncompleteRequirements and the BSG flow never lets the
    // bot leave. OnItemTransferred starts the V-Ex car countdown.
    private static void ActivateExfilForBot(ExfiltrationPoint exfil, Agent agent)
    {
        try
        {
            // ProfileId overload — the IPlayer overload pulls in IDissonancePlayer which Orbit's csproj
            // doesn't reference. BSG's string overload resolves the IPlayer for us.
            exfil.OnItemTransferred(agent.Bot.GetPlayer.ProfileId);

            if (exfil.Status == EExfiltrationStatus.UncompleteRequirements)
            {
                switch (exfil.Settings.ExfiltrationType)
                {
                    case EExfiltrationType.Individual:
                        exfil.SetStatusLogged(EExfiltrationStatus.RegularMode, "Orbit-Proceed-Ind");
                        break;
                    case EExfiltrationType.SharedTimer:
                        exfil.SetStatusLogged(EExfiltrationStatus.Countdown, "Orbit-Proceed-VEx");
                        break;
                    case EExfiltrationType.Manual:
                        // Manual exfils need a switch interaction (Lab keycard, Scav switch). Out of scope —
                        // set the bot to Failed so it picks another objective.
                        Log.Info($"{agent} reached Manual exfil {exfil.name}, no switch logic yet → failing objective");
                        agent.Objective.Status = ObjectiveStatus.Failed;
                        break;
                }
            }
        }
        catch (System.Exception e)
        {
            Log.Warning($"ActivateExfilForBot({exfil.name}) failed: {e.Message}");
        }
    }

    // Corpse excluded: bodies spawn behind cover the killer used; the selection-time gate already prevents
    // picking unseen bodies.
    private static bool RequiresArrivalLoSCheck(WaypointCategory category)
        => category == WaypointCategory.ContainerLoot
            || category == WaypointCategory.LooseLoot
            || category == WaypointCategory.Quest;

    // 3D world-space line-of-sight check between the bot's head and the POI, using the same
    // HighPolyWithTerrainMask BSG uses for cover and vision tests. Real Tarkov walls are HighPoly colliders
    // so any wall between head and POI registers as a Physics.Raycast hit.
    private static bool HasArrivalLineOfSight(Agent agent, Vector3 poiPosition)
    {
        Vector3 head;
        var lookSensor = agent.Bot?.LookSensor;
        head = lookSensor != null ? lookSensor.HeadPoint : agent.Position + new Vector3(0f, 1f, 0f);
        var direction = poiPosition - head;
        var dist = direction.magnitude;
        if (dist < 0.01f) return true; // basically on top of the POI
        var blocked = Physics.Raycast(head, direction / dist, dist, LayerMaskClass.HighPolyWithTerrainMask);
        return !blocked;
    }

    internal static bool IsLootableForAgent(Agent agent, Waypoint location)
    {
        if (location.Target == null) return false;
        var role = agent.Bot.Profile.Info.Settings.Role;
        return location.Category switch
        {
            WaypointCategory.ContainerLoot
            or WaypointCategory.LooseLoot
            or WaypointCategory.Corpse => (LootConfig.LootingEnabled?.Value ?? LootingFaction.None).IsBotEnabled(role),
            _ => false
        };
    }
}
