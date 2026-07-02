using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Systems;
using Unity.Collections;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Tasks.Actions;

/// <summary>
/// Steady-state "hold this objective" behaviour: walk to the assigned cover point, crouch, raycast-sweep
/// nearby directions for the longest sightlines, then cycle the bot's gaze across them while holding the
/// position. Activates once the bot has arrived at a POI and there's nothing to loot or path away to.
/// </summary>
public class GuardAction(AgentData dataset, MovementSystem movementSystem, float hysteresis) : Task<Agent>(hysteresis)
{
    private const float UtilityBoost = 0.45f;
    private const float UtilityBase = 0.2f;
    private const float InnerRadiusRatio = 0.95f * 0.95f;
    private const float SweepAngle = 45f;
    private const int MaxWatchCandidateCount = 25;

    private readonly List<Vector3> _candidateBuffer = [];
    private readonly List<ValueTuple<float, Vector3>> _sortBuffer = [];

    public override void UpdateScore(int ordinal)
    {
        var agents = dataset.Entities.Values;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            var location = agent.Objective.Location;

            // No objective or no cover-point → no Guard. Exfil objectives never Guard either: there
            // is no post-arrival idle at an exfil (inside trigger → ExtractAction owns it, outside
            // trigger → Goto must stay active to run the force-extract timer). With Guard allowed to
            // bid, its in-radius 0.65 + active-task hysteresis could lock a bot into guarding beside
            // its own extract until raid end.
            if (location == null || agent.Guard.CoverPoint == null
                || location.Category == WaypointCategory.Exfil)
            {
                agent.TaskScores[ordinal] = 0;
                continue;
            }

            // Utility ramps up to ~0.65 as the bot enters the objective radius.
            var distSqr = (location.Position - agent.Position).sqrMagnitude;
            var utilityScale = Mathf.InverseLerp(location.RadiusSqr, InnerRadiusRatio * location.RadiusSqr, distSqr);
            agent.TaskScores[ordinal] = UtilityBase + utilityScale * UtilityBoost;
        }
    }

    /// <summary>
    /// Seconds an agent can sit in GuardAction with a loot POI as their objective WITHOUT having entered
    /// Looting status before the watchdog assumes the arrival check failed (radius / LoS raycast) and
    /// blacklists the chain target. 5s is generous — the bot has already finished moving (otherwise it'd
    /// be in GotoObjectiveAction, not Guard), so the only thing left to wait for is the arrival ack +
    /// LootContainerAction takeover, both of which fire within ~1s when nothing is wrong.
    /// </summary>
    private const float GuardOnLootPoiTimeoutSeconds = 5f;

    public override void Update()
    {
        for (var i = 0; i < ActiveEntities.Count; i++)
        {
            var agent = ActiveEntities[i];
            var guard = agent.Guard;

            if (agent.Objective.Location == null || guard.CoverPoint == null)
            {
                // Don't leave a scheduled sweep parked while the objective is gone — a pending job's TempJob
                // arrays trip Unity's 4-frame JobTempAlloc watchdog, and leak outright if we deactivate later.
                if (guard.AreaSweepJob != null)
                {
                    DrainAreaSweepJob(guard);
                    // Re-enter through Moving so a restored objective resubmits the sweep — the Sweep case
                    // spins forever on a null job.
                    guard.Status = GuardStatus.Moving;
                }
                agent.GuardOnLootPoiSinceTime = -1f;
                continue;
            }

            // Stuck-on-loot-POI watchdog. Scavenge sweeps chain to a nearby POI, the bot walks the 0-2m,
            // movement-destination-reached fires, BUT the arrival check (1m radius + Physics raycast LoS)
            // sometimes returns false (slight overshoot, vertical mismatch, wall corner blocking LoS) — the
            // bot doesn't flip Status=Looting and falls into GuardAction by default. The squad keeps its
            // anchor on this POI, alignment check sees agent.Objective matches squad anchor, no re-dispatch
            // fires, agent stays guarding indefinitely. Detect by tracking how long the agent has been in
            // Guard while the objective is a loot category AND status hasn't transitioned through Looting.
            // Bot in GotoObjectiveAction (still walking) won't tick this branch — Guard isn't active there.
            var loc = agent.Objective.Location;
            var stuckCandidate = (loc.Category == WaypointCategory.ContainerLoot
                                  || loc.Category == WaypointCategory.LooseLoot
                                  || loc.Category == WaypointCategory.Corpse)
                                 && agent.Objective.Status != ObjectiveStatus.Looting
                                 && agent.Objective.Status != ObjectiveStatus.Finished;
            if (stuckCandidate)
            {
                if (agent.GuardOnLootPoiSinceTime < 0f) agent.GuardOnLootPoiSinceTime = Time.time;
                if (Time.time - agent.GuardOnLootPoiSinceTime >= GuardOnLootPoiTimeoutSeconds)
                {
                    agent.Squad?.CompletedPoiIds.Add(loc.Id);
                    if (agent.Squad?.Objective.Location != null && agent.Squad.Objective.Location.Id == loc.Id)
                        agent.Squad.Objective.Location = null;
                    var locForLog = loc;
                    agent.Objective.Location = null;
                    agent.Objective.SplinterParent = null;
                    agent.Objective.Status = ObjectiveStatus.None;
                    agent.GuardOnLootPoiSinceTime = -1f;
                    Log.Info($"{agent} guard-on-loot-POI watchdog: blacklisting {locForLog} for {agent.Squad} after {GuardOnLootPoiTimeoutSeconds:F0}s in Guard without Looting (arrival check probably failed — radius / LoS raycast) — cleared agent + squad target to force re-dispatch");
                    // Same churn-detection feed as the 3-fail arrival blacklist: a flurry of these
                    // across DIFFERENT POIs means the bot can't path anywhere and might be wedged on a
                    // door they previously opened.
                    movementSystem.RegisterPoiBlacklistAndMaybeCloseDoors(agent);
                    continue;
                }
            }
            else
            {
                agent.GuardOnLootPoiSinceTime = -1f;
            }

            var coverPoint = guard.CoverPoint.Value;

            switch (agent.Guard.Status)
            {
                case GuardStatus.None:
                    // Sprint to cover honours the same faction / personality gate as objective dispatch
                    // (scavs and Timmy never sprint).
                    var sprintAllowed = SprintGate.IsAllowedByFaction(agent);
                    movementSystem.MoveToByPath(agent, coverPoint.Position, sprint: sprintAllowed, urgency: MovementUrgency.Low);
                    guard.Status = GuardStatus.Moving;
                    Log.Debug($"{agent} guarding: moving to cover point (sprint={sprintAllowed})");
                    break;
                case GuardStatus.Moving:
                    if (agent.Movement.Status == MovementStatus.Moving) continue;

                    // Arrived: crouch and submit the area sweep job.
                    if (agent.Movement.Pose > 0.3f && (coverPoint.Level != CoverLevel.Stay || Random.value > 0.5f))
                        MovementSystem.ResetGait(agent, pose: 0.25f);

                    SubmitAreaSweepJob(agent, coverPoint);
                    guard.Status = GuardStatus.Sweep;
                    Log.Debug($"{agent} guarding: submitted area sweep job");
                    break;
                case GuardStatus.Sweep:
                    if (guard.AreaSweepJob == null) continue;

                    CompleteAreaSweepJob(guard, guard.AreaSweepJob.Value);
                    guard.Status = GuardStatus.Watch;
                    Log.Debug($"{agent} guarding: completed area sweep job");
                    break;
                case GuardStatus.Watch:
                    if (guard.WatchDirections.Count == 0 || guard.WatchTimeout > Time.time)
                        continue;

                    var direction = guard.WatchDirections[Random.Range(0, guard.WatchDirections.Count)];
                    var randomDirection = LookSystem.RandomDirectionInEllipse(direction, SweepAngle, 15f);
                    LookSystem.LookToDirection(agent, randomDirection, 120f);
                    guard.WatchTimeout = Time.time + Random.Range(2.5f, 10f);
                    Log.Debug($"{agent} guarding: set new watch direction");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    protected override void Deactivate(Agent entity)
    {
        var guard = entity.Guard;

        guard.Status = GuardStatus.None;
        // Complete+Dispose, never just drop: nulling a pending job leaked its two TempJob NativeArrays for
        // good (Unity's "JobTempAlloc > 4 frames" warning) every time combat preempted an agent mid-sweep.
        DrainAreaSweepJob(guard);
        guard.WatchDirections.Clear();
        guard.WatchTimeout = 0f;

        entity.Look.Target = null;
    }

    private void SubmitAreaSweepJob(Agent agent, CoverPoint coverPoint)
    {
        var origin = agent.Player.PlayerBones.Head.position;

        _candidateBuffer.Clear();

        if (coverPoint.Category == CoverCategory.Hard)
        {
            _candidateBuffer.Add(-1 * coverPoint.Direction);

            // Non-standing cover: also try looking toward the wall (peek).
            if (coverPoint.Level != CoverLevel.Stay)
                _candidateBuffer.Add(coverPoint.Direction);
        }

        // The objective's Y can produce odd directions relative to the bot's head. Force it into the same
        // elevation for the direction maths.
        var coplanarObjective = new Vector3(agent.Objective.Location.Position.x, origin.y, agent.Objective.Location.Position.z);
        var objectiveVector = coplanarObjective - origin;
        objectiveVector.Normalize();

        _candidateBuffer.Add(objectiveVector);
        _candidateBuffer.Add(-objectiveVector);

        // Radial sweep — 6 evenly-spaced directions around the bot.
        const float angleStep = 360f / 10f;

        var forward = agent.Player.Transform.forward;

        for (var i = 0; i < 6; i++)
        {
            var angle = i * angleStep;
            var rotation = Quaternion.AngleAxis(angle, Vector3.up);
            var direction = rotation * forward;
            _candidateBuffer.Add(direction);
        }

        // Arrival-path corners (looking back the way we came).
        if (agent.Objective.ArrivalPath != null)
        {
            var cornerSweepCount = Math.Min(agent.Objective.ArrivalPath.Length, 10) + 1;

            for (var i = 1; i < cornerSweepCount; i++)
            {
                if (_candidateBuffer.Count >= MaxWatchCandidateCount) break;

                var target = agent.Objective.ArrivalPath[^i] + 1.5f * Vector3.up;
                var direction = target - origin;
                direction.Normalize();
                _candidateBuffer.Add(direction);
            }
        }

        // Nearby doors.
        for (var i = 0; i < agent.Objective.Location.Doors.Count; i++)
        {
            if (_candidateBuffer.Count >= MaxWatchCandidateCount) break;

            var target = agent.Objective.Location.Doors[i].transform.position;
            var direction = target - origin;
            direction.Normalize();
            _candidateBuffer.Add(direction);
        }

        // Pad with nearby cover points.
        for (var i = 0; i < agent.Objective.Location.CoverPoints.Count; i++)
        {
            if (_candidateBuffer.Count >= MaxWatchCandidateCount) break;

            var target = agent.Objective.Location.CoverPoints[i].Position;
            var direction = target - origin;
            direction.Normalize();
            _candidateBuffer.Add(direction);
        }

        var commands = new NativeArray<RaycastCommand>(_candidateBuffer.Count, Allocator.TempJob);
        var results = new NativeArray<RaycastHit>(_candidateBuffer.Count, Allocator.TempJob);

        for (var i = 0; i < _candidateBuffer.Count; i++)
        {
            var direction = _candidateBuffer[i];
            var parameters = new QueryParameters { layerMask = LayerMasksDataAbstractClass.HitMask };
            commands[i] = new RaycastCommand(origin, direction, parameters, 100);
        }

        Log.Debug($"{agent} found {_candidateBuffer.Count} watch candidates");

        agent.Guard.AreaSweepJob = new AreaSweepJob
        {
            Handle = RaycastCommand.ScheduleBatch(commands, results, 1),
            Commands = commands,
            Hits = results,
        };
        Orbit.Helpers.PerfMonitor.SweepJobsSubmitted++;
    }

    // Completes and disposes a scheduled-but-unconsumed sweep, discarding its results.
    private static void DrainAreaSweepJob(Guard guard)
    {
        if (guard.AreaSweepJob == null) return;
        var job = guard.AreaSweepJob.Value;
        job.Handle.Complete();
        job.Commands.Dispose();
        job.Hits.Dispose();
        guard.AreaSweepJob = null;
        Orbit.Helpers.PerfMonitor.SweepJobsDrained++;
    }

    private void CompleteAreaSweepJob(Guard guard, AreaSweepJob job)
    {
        job.Handle.Complete();

        _sortBuffer.Clear();

        for (var i = 0; i < job.Hits.Length; i++)
        {
            var cmd = job.Commands[i];
            var hit = job.Hits[i];

            var distance = hit.collider == null ? cmd.distance : hit.distance;
            _sortBuffer.Add(new(distance, cmd.direction));
        }

        job.Commands.Dispose();
        job.Hits.Dispose();
        // Clear the slot so a later Deactivate drain can't double-Dispose the arrays just released.
        guard.AreaSweepJob = null;
        Orbit.Helpers.PerfMonitor.SweepJobsCompleted++;

        _sortBuffer.Sort(Comparer.Instance);

        // Keep the 5 longest-distance directions — those are the most worthwhile to watch (open sightlines).
        var limit = Math.Min(_sortBuffer.Count, 5) + 1;

        for (var i = 1; i < limit; i++)
            guard.WatchDirections.Add(_sortBuffer[^i].Item2);
    }

    public sealed class Comparer : Comparer<ValueTuple<float, Vector3>>
    {
        public static readonly Comparer Instance = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int Compare(ValueTuple<float, Vector3> x, ValueTuple<float, Vector3> y)
            => x.Item1.CompareTo(y.Item1);
    }
}
