using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Inventory;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

namespace Orbit.Tasks.Actions;

/// <summary>
/// Drives a bot from wherever they are to their currently-assigned
/// <see cref="Waypoint"/>. Owns the arrival logic: strict-radius gate
/// with line-of-sight check (no looting through walls), nav-snap rescue
/// for the case where BSG's mover stopped 1.5-2.5m off the exact point,
/// status transition to Looting/Extracting/Finished, and per-agent
/// blacklist when the same POI keeps failing.
/// </summary>
public class GotoObjectiveAction(AgentData dataset, MovementSystem movementSystem, WaypointSystem waypointSystem, float hysteresis) : Task<Agent>(hysteresis)
{
    private const float UtilityBase = 0.5f;
    private const float UtilityBoost = 0.15f;
    private const float UtilityBoostMaxDistSqr = 50f * 50f;

    // Sprint only when far enough that running is worth the awareness
    // penalty. Within 50m of the objective we walk: the bot's vision
    // sensor updates more often when not sprinting, letting it spot
    // enemies it would otherwise blast past.
    private const float WalkApproachDistanceSqr = 50f * 50f;

    // BSG nav-snap arrival rescue. The bot's path consumes at the nearest
    // navmesh node, which can sit 1.5-2.5m off the exact Waypoint.Position.
    // With the strict 1m POI arrival radius we'd otherwise time out at
    // 1.0-2.5m without ever flipping to Looting. 4m is the upper bound
    // where the LoS check still makes sense.
    private const float NavSnapArrivalRadius = 4f;
    private const float NavSnapArrivalRadiusSqr = NavSnapArrivalRadius * NavSnapArrivalRadius;

    public override void UpdateScore(int ordinal)
    {
        var agents = dataset.Entities.Values;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            var location = agent.Objective.Location;

            // Release control when the objective has terminated OR moved
            // into a phase owned by another action (LootContainerAction
            // handles Looting, ExtractAction handles Extracting). Without
            // these two extra exclusions the nav-snap arrival rescue
            // accepts arrival 1-4m off the POI, flips status to Looting,
            // but our distSqr is still > RadiusSqr — so utilityDecay
            // clamps to 1.0 and our score (0.65) + Hysteresis (0.15) =
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

            // Baseline 0.5f, boosted to 0.65f as the bot gets nearer.
            // Once within the objective radius, utility falls off sharply.
            var distSqr = (location.Position - agent.Position).sqrMagnitude;

            var utilityBoostFactor = Mathf.InverseLerp(UtilityBoostMaxDistSqr, location.RadiusSqr, distSqr);
            var utilityDecay = Mathf.InverseLerp(0f, location.RadiusSqr, distSqr);

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

                    // Stop sprinting once inside the 50m "scan" radius so
                    // the bot has time to spot enemies on the final
                    // approach. Personality override: agents with sprint
                    // propensity ~1.0 (e.g. VeryAggressive GigaChad) keep
                    // sprinting all the way in.
                    var sprintPropensity = agent.Squad?.Personality?.SprintPropensity ?? 0.5f;
                    if (agent.Movement.Sprint
                        && distanceSqr < WalkApproachDistanceSqr
                        && sprintPropensity < 0.999f)
                    {
                        MovementSystem.ResetGait(agent);
                    }

                    // Arrival gate — two-stage check that both prevents
                    // looting through walls AND tolerates BSG's nav-snap
                    // drift:
                    //
                    //   1. Within the strict 1m radius → still require a
                    //      Physics.Raycast LoS clear between the bot's
                    //      head and the POI. A bot standing 0.9m from a
                    //      loot pile on the OTHER side of a wall would
                    //      otherwise pass the euclidean check and loot
                    //      through the wall. Uses the same
                    //      HighPolyWithTerrainMask BSG uses natively —
                    //      tests real 3D world geometry, not navmesh edges.
                    //
                    //   2. Between 1m and 4m, AND bot is Stopped (BSG
                    //      reports "destination reached" but stopped on
                    //      the nearest navmesh node, 1.5-2.5m off the
                    //      exact Waypoint.Position) → same Physics
                    //      raycast LoS check. BSG nav-snap rescue path.
                    //
                    // Lootables only — Synthetic / Exfil still use the
                    // wide radius without LoS (those don't suffer from
                    // through-wall validation).
                    var inRadius = false;
                    if (distanceSqr <= objective.Location.RadiusSqr)
                    {
                        if (RequiresArrivalLoSCheck(objective.Location.Category))
                        {
                            if (HasArrivalLineOfSight(agent, objective.Location.Position))
                            {
                                inRadius = true;
                            }
                            else
                            {
                                Log.Debug($"{agent} within {Mathf.Sqrt(distanceSqr):F1}m of {objective.Location} but Physics raycast BLOCKED — wall in between, holding off arrival");
                            }
                        }
                        else
                        {
                            inRadius = true;
                        }
                    }
                    else if (agent.Movement.Status == MovementStatus.Stopped
                             && distanceSqr <= NavSnapArrivalRadiusSqr
                             && RequiresArrivalLoSCheck(objective.Location.Category)
                             && HasArrivalLineOfSight(agent, objective.Location.Position))
                    {
                        Log.Debug($"{agent} BSG nav-snap arrival rescue: stopped {Mathf.Sqrt(distanceSqr):F1}m off {objective.Location} but Physics raycast clear → accepting arrival");
                        inRadius = true;
                    }
                    if (inRadius)
                    {
                        // If this is a lootable POI and we can grab the
                        // claim, chain straight into Looting state.
                        // Otherwise (claim held, or non-lootable category)
                        // fall through to Finished — the squad waits here
                        // and another task can run.
                        if (IsLootableForAgent(agent, objective.Location))
                        {
                            if (waypointSystem.TryClaim(objective.Location.Id, agent.Id))
                            {
                                objective.Status = ObjectiveStatus.Looting;
                                // Looting keeps the bot stationary for
                                // several seconds. If we leave the path
                                // pointing at the previous destination,
                                // BSG's BotMover eventually decides the bot
                                // is "stuck" relative to its last good
                                // cast point and teleports it away —
                                // sometimes 200m+. Re-aim at current pos
                                // so BSG re-links here and resets its
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
                            // Activate the exfil if still gated behind
                            // requirements (V-Ex / pay-to-leave), then
                            // hand off to ExtractAction which waits the
                            // countdown and despawns the bot.
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
                        // MovementSystem's HardStuck escalation ran out
                        // (recalc → teleport → giving up) and reset path
                        // to Failed. The bot is jammed against geometry.
                        objective.Status = ObjectiveStatus.Failed;
                        Log.Debug($"{agent} movement Failed en-route to {objective.Location} — HardStuck giving up");
                        TrackArrivalFailure(agent, objective.Location);
                    }
                    // Stuck-at-destination guard: BSG's BotMover reports
                    // Reached but our euclidean radius check above failed
                    // (bot stopped just outside the arrival radius). If
                    // the bot stays in this in-between state too long it
                    // would never advance — fail the agent objective so
                    // the wait-timer / re-dispatch path kicks in
                    // immediately.
                    else if (agent.Movement.Status == MovementStatus.Stopped)
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

        // Reset the status if the bot wasn't failed/finished — otherwise
        // we won't resubmit the move order the next time we're activated.
        entity.Objective.Status = ObjectiveStatus.None;
    }

    // Regular scavs (assault/assaultGroup) never sprint to an objective —
    // they wander, they don't hustle. Everyone else (PMCs, Goons, raiders,
    // bosses) sprints when far and walks on the final approach. Combat
    // sprint is decided by SAIN, not us.
    //
    // SAIN personality layer (PMC only): squad.Personality.SprintPropensity
    // is a 0..1 value rolled from the archetype table that further gates
    // when this agent sprints. 0 = never sprint (Timmy walks); 1 = always
    // sprint, even within the final 50m approach. Default 0.5 matches
    // the pre-personality behaviour.
    private static bool ShouldSprintToObjective(Agent agent, float startDistSqr)
    {
        var role = agent.Bot?.Profile?.Info?.Settings?.Role;
        if (role.HasValue && role.Value.IsScav()) return false;
        var propensity = agent.Squad?.Personality?.SprintPropensity ?? 0.5f;
        if (propensity <= 0.001f) return false; // never sprint
        if (propensity >= 0.999f) return true;  // always sprint, even close
        // Higher propensity shrinks the walk-only window so the bot
        // starts sprinting from closer distances.
        var walkApproachScale = 1.5f - propensity; // 0.2→1.3, 0.5→1.0, 0.8→0.7
        var effectiveWalkSqr = WalkApproachDistanceSqr * walkApproachScale * walkApproachScale;
        return startDistSqr > effectiveWalkSqr;
    }

    // Per-agent blacklist on repeated arrival failures for the same POI.
    // The squad-level ConsecutiveFailedDispatches only fires when ALL
    // members fail at once; a single member stuck on an unreachable
    // splinter while squadmates loot fine never triggers it. Two failure
    // modes feed in: "stopped outside arrival radius" (navmesh sample
    // lands 4m+ off) AND HardStuck "giving up" (bot jammed en-route).
    private static void TrackArrivalFailure(Agent agent, Waypoint location)
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
            agent.Squad.CompletedPoiIds.Add(locId);
            // Adding to CompletedPoiIds only filters FUTURE picks; the
            // current dispatch still has agent.Objective.Location pinned
            // at the bad POI (set by AssignNewObjective, by a follower
            // splinter pick, or by the loot routine's scavenge sweep which
            // pins squad.Objective.Location at the chain target for 60s).
            // If we don't break both pins explicitly the bot oscillates
            // Goto → Failed → reset to None → Goto re-submits move order
            // toward the same POI → fails again, every ~1s, even though
            // the blacklist is already armed.
            if (agent.Squad.Objective.Location != null
                && agent.Squad.Objective.Location.Id == locId)
            {
                agent.Squad.Objective.Location = null;
            }
            agent.Objective.Location = null;
            agent.Objective.SplinterParent = null;
            agent.Objective.Status = ObjectiveStatus.None;
            Log.Info($"{agent} blacklisting {location} for {agent.Squad} after 3 consecutive arrival failures (cleared agent + squad target to force re-dispatch)");
            agent.ConsecutiveSamePoiFailures = 0;
            agent.LastFailedPoiId = -1;
        }
    }

    // Mirrors BSG's ActivateExfil flow: forces a still-gated exfil into a
    // usable state right when the bot arrives. Without this, V-Ex / pay
    // exfils stay at UncompleteRequirements and the BSG flow never lets
    // the bot leave. OnItemTransferred starts the V-Ex car countdown.
    private static void ActivateExfilForBot(ExfiltrationPoint exfil, Agent agent)
    {
        try
        {
            // ProfileId overload — the IPlayer overload pulls in
            // IDissonancePlayer which Orbit's csproj doesn't reference.
            // BSG's string overload resolves the IPlayer for us.
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
                        // Manual exfils need a switch interaction (Lab
                        // keycard, Scav switch). Out of scope — set the
                        // bot to Failed so it picks another objective.
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

    // Categories whose arrival must be gated on a 3D LoS check to
    // prevent through-wall validation. Loot + Quest sit in confined
    // rooms where a thin wall between bot and POI is a real risk;
    // Synthetic / Exfil are wide-radius volumes where the LoS check
    // would mostly produce false negatives.
    private static bool RequiresArrivalLoSCheck(WaypointCategory category)
        => category == WaypointCategory.ContainerLoot
            || category == WaypointCategory.LooseLoot
            || category == WaypointCategory.Corpse
            || category == WaypointCategory.Quest;

    // 3D world-space line-of-sight check between the bot's head and the
    // POI, using the same HighPolyWithTerrainMask BSG uses for cover and
    // vision tests. Real Tarkov walls are HighPoly colliders so any wall
    // between head and POI registers as a Physics.Raycast hit.
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
            WaypointCategory.ContainerLoot => (LootConfig.ContainerLootingEnabled?.Value ?? LootingFaction.None).IsBotEnabled(role),
            WaypointCategory.LooseLoot => (LootConfig.LooseItemLootingEnabled?.Value ?? LootingFaction.None).IsBotEnabled(role),
            WaypointCategory.Corpse => (LootConfig.CorpseLootingEnabled?.Value ?? LootingFaction.None).IsBotEnabled(role),
            _ => false
        };
    }
}
