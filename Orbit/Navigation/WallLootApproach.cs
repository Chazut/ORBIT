using EFT.Interactive;
using Orbit.Entities;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// Created only after a physical wall blocks an actual interaction. Sample the object side,
// not the waypoint: the nearest waypoint can be in a different room.
internal static class WallLootApproach
{
    private const int Directions = 8;
    private const int Samples = 1 + 3 * Directions;
    internal enum Result { Moving, Arrived, Exhausted }

    internal static void ResumeAfterBlockedSession(Agent agent)
    {
        var objective = agent.Objective;
        if (objective.WallApproach?.Location != objective.Location)
            objective.WallApproach = new WallLootApproachState(objective.Location);
        var state = objective.WallApproach;
        state.RejectedStand = state.Stand ?? agent.Position;
        state.Stand = null;
        state.RequireNewStand = true;
        objective.Status = ObjectiveStatus.Moving;
    }

    internal static Result Tick(this WallLootApproachState state, Agent agent, MovementSystem movement, WaypointSystem waypoints)
    {
        if ((!agent.IsActive && !agent.IsDormant) || MovementSystem.IsTravelPaused(agent)) return Result.Moving;
        var target = state.Location.Target as InteractableObject;
        if (target == null) return Result.Exhausted;
        var centre = target is Corpse ? LootVisibility.BodyPosition(target) : LootVisibility.Target(target);
        bool stopped = agent.Movement.Status == MovementStatus.Stopped && Time.time - agent.Objective.DispatchTime > 2f;
        bool near = state.Stand.HasValue ? (agent.Position - state.Stand.Value).sqrMagnitude <= .25f
            : !state.RequireNewStand && (agent.Position - state.Location.Position).sqrMagnitude <= state.Location.RadiusSqr;
        if (near || stopped && (state.Stand.HasValue || !state.RequireNewStand))
        {
            var head = !agent.IsDormant && agent.Bot?.LookSensor != null
                ? agent.Bot.LookSensor.HeadPoint : agent.Position + Vector3.up;
            if (InReach(target, agent.Position, centre, state.NextSample)
                && waypoints.HasReachedObjectiveFloor(agent, state.Location, agent.Position)
                && LootVisibility.Check(head, target, out _)) return Result.Arrived;
        }

        if (state.Stand.HasValue)
        {
            if (!stopped && agent.Movement.Status != MovementStatus.Failed) return Result.Moving;
            state.RejectedStand = state.Stand;
            state.Stand = null;
        }
        if (state.NextSample >= Samples) return Result.Exhausted;
        int index = state.NextSample++;
        int ring = index == 0 ? 0 : (index - 1) / Directions;
        float radius = index == 0 ? 0f : ring == 0 ? .65f : ring == 1 ? 1.45f : 2.5f;
        double angle = (index - 1) * (System.Math.PI / 4);
        var seed = new Vector3(centre.x + (float)System.Math.Cos(angle) * radius,
            index == 0 || target is Corpse ? centre.y : state.Location.Position.y,
            centre.z + (float)System.Math.Sin(angle) * radius);
        if (NavMesh.SamplePosition(seed, out var hit, 1.5f, NavMesh.AllAreas)
            && (hit.position - agent.Position).sqrMagnitude >= .04f
            && (!state.RejectedStand.HasValue || (hit.position - state.RejectedStand.Value).sqrMagnitude >= .04f)
            && InReach(target, hit.position, centre, state.NextSample)
            && waypoints.HasReachedObjectiveFloor(agent, state.Location, hit.position)
            && LootVisibility.Check(hit.position + Vector3.up, target, out _)
            && CorpseApproach.HasCompletePath(agent.Position, hit.position))
        {
            state.Stand = hit.position;
            movement.MoveToByPath(agent, hit.position, sprint: false);
            agent.Objective.DispatchTime = Time.time;
            Log.Debug($"LOOT WALL DETOUR: {agent} point={state.Location.Id} stand={hit.position}");
        }
        return Result.Moving;
    }

    private static bool InReach(InteractableObject target, Vector3 feet, Vector3 centre, int checkedSamples)
    {
        float limit = target is Corpse
            ? (checkedSamples <= 1 + Directions ? CorpseApproach.PreferredSqr : CorpseApproach.LimitSqr)
            : 4f * 4f;
        return (feet - centre).sqrMagnitude <= limit;
    }
}
