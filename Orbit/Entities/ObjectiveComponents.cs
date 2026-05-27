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
    /// When this agent was dispatched as a follower to a loot splinter
    /// instead of the squad's main objective, this points to the squad's
    /// main objective the splinter was picked around. Lets UpdateAgents
    /// recognise that the follower is still "aligned" with the squad even
    /// though Location != squad.Objective.Location, and avoid re-dispatching
    /// them every tick.
    /// </summary>
    public Waypoint SplinterParent;

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
