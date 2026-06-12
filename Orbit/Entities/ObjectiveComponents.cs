using System.Collections.Generic;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Entities;

// ── Per-agent objective ──────────────────────────────────────────────

public enum ObjectiveStatus
{
    None,
    Moving,
    Looting,
    Extracting,
    Finished,
    Failed
}

public class Objective
{
    public ObjectiveStatus Status;
    public Waypoint Location;
    public Vector3[] ArrivalPath;

    /// <summary>
    /// When this agent was dispatched as a follower to a loot splinter instead of the squad's main objective,
    /// this points to the squad's main objective the splinter was picked around. Lets UpdateAgents recognise
    /// that the follower is still "aligned" with the squad even though Location != squad.Objective.Location,
    /// and avoid re-dispatching them every tick.
    /// </summary>
    public Waypoint SplinterParent;

    /// <summary>
    /// Time.time when this objective was assigned. The arrival-failure check needs a grace window after
    /// dispatch — without it, an agent transitioning from Guard (Movement.Status == Stopped) directly into
    /// Goto fails arrival on the FIRST tick because BSG's BotMover hasn't begun the new move yet, the
    /// Stopped state lingers from Guard, and our check fires "stopped outside arrival radius" before the
    /// bot has moved a single metre. Field repro: HARDcore on Customs, F324942 → F324945 = 3 frames (0.05s)
    /// from assignment to "failing objective", reach failed without leaving Guard. Goto's stuck-at-destination
    /// branch reads this and skips the failure trigger until the bot has had time to actually start moving.
    /// </summary>
    public float DispatchTime;

    /// <summary>
    /// Time.time when the agent first entered the loose 15 m arrival radius of an Exfil objective AND was
    /// outside the actual trigger collider. Used by the exfil force-extract timer — if the agent stays
    /// in this "within radius, outside trigger" state for <c>ExfilOutsideTriggerForceExtractSeconds</c>,
    /// we despawn from current position anyway rather than make them wander forever. -1 = not in that
    /// state. Reset when the agent exits the radius or actually enters the trigger.
    /// </summary>
    public float ExfilOutsideTriggerSince = -1f;

    public override string ToString() => $"Objective({Location}, status: {Status})";
}

// ── Per-squad objective ──────────────────────────────────────────────

public enum SquadObjectiveState
{
    Active,
    Wait
}

public class SquadObjective
{
    public Waypoint Location;
    public Waypoint LocationPrevious;
    public readonly List<CoverPoint> CoverPoints = [];

    public SquadObjectiveState Status = SquadObjectiveState.Wait;

    public float StartTime;
    public float Duration;
    public bool DurationAdjusted;

    public override string ToString()
        => $"SquadObjective({Location}, {Status}, timeout: {Time.time - StartTime} / {Duration})";
}
