using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>
/// Drives the bot's head-look direction. By default it points the bot's
/// gaze at a look-ahead point along its current path, so the head turns
/// into corners ahead of the body. Tasks that need a specific look target
/// (loot inspection, watching a cover sector) set <c>agent.Look.Target</c>
/// directly and this system honours that override.
/// </summary>
public class LookSystem
{
    private const float MoveLookAheadDistSqr = 1.5f;
    private const float MoveTargetProxmityDistSqr = 1f;

    public static void Update(List<Agent> liveAgents)
    {
        for (var i = 0; i < liveAgents.Count; i++)
        {
            var agent = liveAgents[i];

            if (!agent.IsActive)
            {
                agent.Look.Target = null;
                continue;
            }

            var bot = agent.Bot;
            var movement = agent.Movement;

            if (agent.Look.Target != null)
                continue;

            if (!movement.HasPath || (movement.Path[^1] - agent.Position).sqrMagnitude <= MoveTargetProxmityDistSqr)
                continue;

            var fwdPoint = PathHelper.CalcForwardPoint(movement.Path, agent.Position, movement.CurrentCorner, MoveLookAheadDistSqr);
            var lookDirection = fwdPoint - agent.Position;
            lookDirection.Normalize();
            bot.Steering.LookToDirection(lookDirection, 540f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LookToPoint(Agent agent, Vector3 target, float rotateSpeed = 180f)
    {
        agent.Look.Target = target;
        agent.Look.Type = LookType.Position;
        agent.Bot.Steering.LookToPoint(agent.Look.Target.Value, rotateSpeed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LookToDirection(Agent agent, Vector3 target, float rotateSpeed = 180f)
    {
        agent.Look.Target = target;
        agent.Look.Type = LookType.Direction;
        agent.Bot.Steering.LookToDirection(agent.Look.Target.Value, rotateSpeed);
    }

    /// <summary>
    /// Sample a random direction inside an ellipse-shaped cone around
    /// <paramref name="centerDirection"/>. Used by Guard sweep to pick
    /// the next watch heading.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 RandomDirectionInEllipse(Vector3 centerDirection, float horizontalAngle, float verticalAngle)
    {
        var horizontalOffset = Random.Range(-horizontalAngle / 2f, horizontalAngle / 2f);
        var verticalOffset = Random.Range(-verticalAngle / 2f, verticalAngle / 2f);

        var centerRotation = Quaternion.LookRotation(centerDirection);
        var offsetRotation = Quaternion.Euler(verticalOffset, horizontalOffset, 0f);

        return centerRotation * offsetRotation * Vector3.forward;
    }
}
