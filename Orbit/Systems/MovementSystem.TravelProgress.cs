using Orbit.Entities;
using UnityEngine;

namespace Orbit.Systems;

public partial class MovementSystem
{
    // Called only while ORBIT is trying to follow a live awake path. No extra
    // physics or path queries here; replanning uses the ordinary bounded queue.
    private bool CheckTravelProgress(Agent agent)
    {
        var watch = agent.Stuck.TravelProgress;
        if (agent.Bot.Memory.GoalEnemy != null || agent.Bot.Memory.IsUnderFire
            || agent.Movement.Speed <= .01f
            || agent.Objective?.Status is ObjectiveStatus.Looting or ObjectiveStatus.Extracting or ObjectiveStatus.Finished)
        { watch.Reset(); return false; }
        var action = watch.Observe(agent.Position, Time.time);
        if (action == 0) return false;
        Log.Always($"MOVEMENT PROGRESS: {agent} action={(action == 1 ? "repath" : "failed-route")} "
            + $"assists={watch.Assists} position={agent.Position} target={agent.Movement.Target} "
            + $"revision={agent.Movement.PathRevision} objective={agent.Objective?.Location}");
        if (action == 1) MoveRetry(agent, agent.Movement.Target);
        else
        {
            agent.Movement.Failures.FailedRoute(agent.Position);
            ResetPath(agent, MovementStatus.Failed);
            // Feed the existing paced, supported-landing search. It rechecks combat,
            // visibility, physics and connectivity before allowing any local rescue.
            agent.Stuck.IdleRescueAnchor = agent.Position;
            agent.Stuck.IdleRescueSince = Time.time - IdleRescueThresholdSeconds;
            agent.Stuck.IdleRescueLastObservedAt = Time.time;
            agent.Stuck.IdleRescueIntent = true;
        }
        return true;
    }
}
