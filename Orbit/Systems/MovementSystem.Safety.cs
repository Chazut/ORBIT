using Orbit.Entities;
using UnityEngine;

namespace Orbit.Systems;

public partial class MovementSystem
{
    private static void StopAwakeInput(Agent agent)
    {
        // ResetPath is also called during handoff. Never overwrite native/SAIN input there.
        if (!agent.IsActive || agent.IsDormant || agent.Player == null) return;
        agent.Player.Move(Vector2.zero);
        agent.Player.CharacterController?.SetSteerDirection(Vector3.zero);
        if (agent.Player.Physical.Sprinting) agent.Player.EnableSprint(false);
    }
}
