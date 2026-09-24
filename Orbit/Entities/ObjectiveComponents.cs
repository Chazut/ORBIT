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
    /// bot has moved a single metre — a Guard→Goto transition can otherwise register a failure within a
    /// few frames of dispatch, before the bot has left its Guard spot. Goto's stuck-at-destination branch
    /// reads this and skips the failure trigger until the bot has had time to actually start moving.
    /// </summary>
    public float DispatchTime;

    /// <summary>
    /// Time.time when the agent last made meaningful progress inside the loose exfil radius
    /// but outside its trigger. The local fallback can despawn a stuck bot from its current
    /// position. Reset on movement, leaving the radius, entering the trigger or a new dispatch.
    /// -1 means the outside-trigger timer is not armed.
    /// </summary>
    public float ExfilOutsideTriggerSince = -1f;
    public Vector3 ExfilOutsideTriggerLastPosition;

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

    // Bound full waypoint re-selection when every member has exhausted nearby loot.
    // Kept across anchor changes so repeated empty picks cannot spin each strategy tick.
    public float NextLootExhaustionRecheckAt;

    public override string ToString()
        => $"SquadObjective({Location}, {Status}, timeout: {Time.time - StartTime} / {Duration})";
}
