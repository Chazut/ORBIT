using System.Runtime.CompilerServices;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

public partial class MovementSystem
{
    private void ObserveAwakeRecovery(Agent agent, bool allowReturn = true)
    {
        using var timing = MeasureMovement(TransitionPhase.MovementRecovery, "recovery anchor", agent);
        if (agent.Stuck.Recovery.Observe(agent.Position, agent.Bot?.Mover) && allowReturn)
            TryReturnToValidatedAnchor(agent);
    }

    private static bool CanRecoverAirborneFall(Player player)
        => !IsNativeTraversal(player) || (player.CurrentManagedState?.Name == EPlayerState.FallDown
            && player.MovementContext?.PlayerAnimatorGetIsVaulting() != true);

    private static bool IsNativeTraversal(Player player)
        => player.MovementContext?.PlayerAnimatorGetIsVaulting() == true
           || player.CurrentManagedState?.Name is EPlayerState.Jump or EPlayerState.FallDown
               or EPlayerState.JumpLanding or EPlayerState.ClimbOver or EPlayerState.ClimbUp
               or EPlayerState.VaultingFallDown or EPlayerState.VaultingLanding;

    private static bool HoldForAirborneMotion(Agent agent)
    {
        var motion = agent.Movement.AwakeMotion;
        var player = agent.Player;
        if (!agent.IsActive || agent.IsDormant || player == null)
        {
            motion.ClearInput();
            motion.Jump.End(agent, "handoff");
            motion.Finish(agent, "handoff", EPlayerState.None);
            return false;
        }

        var traversal = IsNativeTraversal(player);
        var state = player.CurrentManagedState?.Name ?? EPlayerState.None;
        var held = traversal || player.MovementContext?.IsGrounded != true;
        motion.Observe(agent, held, traversal, state);
        motion.Jump.Observe(agent, state);
        if (!held) return false;

        // Suspend new unstuck actions, pose changes and sprint requests during native motion.
        // This also covers grounded takeoff/landing frames and vaults initiated this tick.
        // Keep the validated anchor: the existing persistent off-mesh fall rescue still runs.
        agent.Stuck.Soft.Reset();
        agent.Stuck.IdleRescueSince = -1f;
        agent.Stuck.IdleRescueIntent = false;
        agent.Movement.SprintControl.Suspend();

        if (!traversal && (!agent.Movement.HasPath
            || agent.Movement.Status is MovementStatus.Failed or MovementStatus.Stopped))
        {
            StopAwakeInput(agent, "no-route");
            return true;
        }

        // Bridge brief contact flicker only in ordinary locomotion. Never feed a jump/vault,
        // start a sprint in mid-air, or keep steering after the grounded input expires.
        if (!traversal && state is EPlayerState.Run or EPlayerState.Sprint && motion.CanCoast(agent))
        {
            var input = CalcMoveDirection(motion.Steering, player.Rotation);
            if (input.normalized.y >= .6427876f)
            {
                player.CharacterController.SetSteerDirection(motion.Steering);
                player.Move(input);
                motion.Coast();
                return true;
            }
        }
        motion.ClearInput();
        return true;
    }

    private static void StopAwakeInput(Agent agent, [CallerMemberName] string reason = null)
    {
        agent.Movement.AwakeMotion.ClearInput();
        agent.Movement.AwakeMotion.Jump.End(agent, reason ?? "stop");
        agent.Movement.SprintControl.Suspend();
        // ResetPath is also called during handoff. Never overwrite native/SAIN input there.
        if (!agent.IsActive || agent.IsDormant || agent.Player == null)
        {
            agent.Movement.AwakeMotion.Finish(agent, "handoff", EPlayerState.None);
            return;
        }
        // Clearing a route must not cancel an already running native jump or vault.
        if (IsNativeTraversal(agent.Player)) return;
        agent.Player.Move(Vector2.zero);
        agent.Player.CharacterController?.SetSteerDirection(Vector3.zero);
        if (agent.Player.Physical.Sprinting)
        {
            agent.Movement.AwakeMotion.TraceStop(agent, reason);
            agent.Player.EnableSprint(false);
        }
    }
}
