using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

/// <summary>
/// Per-frame movement engine. Owns the queue of pending navmesh path
/// jobs, the corner-following + path-deviation steering, door handling,
/// sprint gating, and the two-stage stuck-detection / remediation
/// pipeline (soft = vault/jump, hard = re-path then teleport).
/// </summary>
public class MovementSystem
{
    private const float TargetEps = 1.5f;
    private const float TargetEpsSqr = TargetEps * TargetEps;
    private const float CornerWalkEpsSqr = 0.35f * 0.35f;
    private const float CornerSprintEpsSqr = 0.6f * 0.6f;
    private const int RetryLimit = 10;

    private readonly NavJobExecutor _navJobExecutor;
    private readonly Queue<ValueTuple<Agent, NavJob>> _moveJobs;
    private readonly StuckRemediation _stuckRemediation;

    public MovementSystem(NavJobExecutor navJobExecutor, List<Player> humanPlayers)
    {
        _navJobExecutor = navJobExecutor;
        _moveJobs = new Queue<(Agent, NavJob)>(20);
        _stuckRemediation = new StuckRemediation(this, humanPlayers);
    }

    public void Update(List<Agent> liveAgents)
    {
        if (_moveJobs.Count > 0)
        {
            for (var i = 0; i < _moveJobs.Count; i++)
            {
                var (agent, job) = _moveJobs.Dequeue();

                if (!job.IsReady)
                {
                    _moveJobs.Enqueue((agent, job));
                    continue;
                }

                // Discard the move job if the agent is inactive (mod
                // deactivated, bot died, etc).
                if (!agent.IsActive)
                    continue;

                StartMovement(agent, job);
            }
        }

        for (var i = 0; i < liveAgents.Count; i++)
        {
            var agent = liveAgents[i];

            if (!agent.IsActive)
            {
                if (agent.Movement.HasPath)
                    ResetPath(agent);
                continue;
            }

            // Keep BSG's BotMover anchored to where the bot ACTUALLY is.
            // BotMover.method_10 (the hard rescue teleport) snaps the bot to
            // LastGoodCastPoint when it decides the bot is stuck. The brain
            // layer sets LastGoodCastPoint to agent.Position only at the
            // layer *transition* — so while we're in control, it stays
            // frozen at wherever the bot was when handed off. A rescue then
            // yeets the bot back to that stale anchor (sometimes their
            // spawn). Refreshing every frame makes any rescue land as a
            // teleport-to-self no-op.
            var mover = agent.Bot?.Mover;
            if (mover != null)
            {
                var pos = agent.Position;
                mover.LastGoodCastPoint = pos;
                mover.PrevSuccessLinkedFrom_1 = pos;
                mover.PrevLinkPos = pos;
                mover.PositionOnWayInner = pos;
            }

            UpdateMovement(agent);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMovementTargetCurrent(Agent agent, Vector3 destination)
        => (agent.Movement.Target - destination).sqrMagnitude <= TargetEpsSqr;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ResetGait(
        Agent agent, float pose = 1f, float speed = 1f, bool prone = false, bool sprint = false, MovementUrgency urgency = MovementUrgency.Medium)
    {
        agent.Movement.Pose = pose;
        agent.Movement.Speed = speed;
        agent.Movement.Prone = prone;
        agent.Movement.Sprint = sprint;
        agent.Movement.Urgency = urgency;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MoveToByPath(
        Agent agent, Vector3 destination, float pose = 1f, float speed = 1f, bool prone = false, bool sprint = false,
        MovementUrgency urgency = MovementUrgency.Medium)
    {
        if (NavMesh.SamplePosition(destination, out var hit, TargetEps, NavMesh.AllAreas))
            destination = hit.position;

        // Set the target up-front so callers' "is the target current?"
        // checks see the new value immediately.
        agent.Movement.Target = destination;
        ScheduleMoveJob(agent, destination);
        ResetGait(agent, pose, speed, prone, sprint, urgency);
        ResetPath(agent, MovementStatus.Moving);
        agent.Movement.Retry = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MoveToDirect(Agent agent, Vector3 destination)
        => throw new NotImplementedException();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveRetry(Agent agent, Vector3 destination)
    {
        ResetPath(agent);

        if (agent.Movement.Retry >= RetryLimit)
        {
            Log.Debug($"{agent} movement failed due to exhausting the retry limits");
            agent.Movement.Status = MovementStatus.Failed;
            return;
        }

        ScheduleMoveJob(agent, destination);
        agent.Movement.Retry++;
    }

    private void ScheduleMoveJob(Agent agent, Vector3 destination)
    {
        var origin = agent.Position;

        if (NavMesh.SamplePosition(origin, out var hit, TargetEps, NavMesh.AllAreas))
            origin = hit.position;

        var job = _navJobExecutor.Submit(origin, destination);
        _moveJobs.Enqueue((agent, job));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StartMovement(Agent agent, NavJob job)
    {
        if (job.Status == NavMeshPathStatus.PathInvalid)
        {
            Log.Debug($"{agent} movement failed due to an invalid path");
            agent.Movement.Target = job.Target;
            ResetPath(agent, MovementStatus.Failed);
            return;
        }

        AssignPath(agent.Movement, job);

        agent.Bot.Mover.Stop();
        agent.Bot.Mover.Pause = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateMovement(Agent agent)
    {
        var bot = agent.Bot;
        var player = agent.Player;
        var movement = agent.Movement;

        // Pose must be updated even if we aren't moving.
        var poseDelta = movement.Pose - player.PoseLevel;
        if (Math.Abs(poseDelta) > 1e-2)
            bot.SetPose(movement.Pose);

        if (bot.BotLay.IsLay != movement.Prone)
        {
            if (movement.Prone) bot.BotLay.TryLay();
            else bot.BotLay.GetUp(true);
        }

        if (!movement.HasPath || movement.Status == MovementStatus.Failed || movement.Status == MovementStatus.Stopped)
            return;

        if (movement.VoxelUpdatePacing.Allowed())
            bot.AIData.SetPosToVoxel(agent.Position);

        var moveSpeedMult = 1f;

        // Door handling
        var doorsNearby = HandleDoors(agent);
        if (doorsNearby)
            moveSpeedMult = 0.25f;

        // Speed
        var movementSpeed = movement.Speed * moveSpeedMult;
        var speedDelta = movementSpeed - player.Speed;
        if (Math.Abs(speedDelta) > 1e-8)
            bot.Mover.SetTargetMoveSpeed(movementSpeed);

        // Sprint
        var shouldSprint = movement.Sprint && CanSprint(agent) && !doorsNearby;
        if (player.Physical.Sprinting != shouldSprint)
            player.EnableSprint(shouldSprint);

        // Run stuck remediation before movement logic
        _stuckRemediation.Update(agent);

        // The stuck remediation might've nulled out the path
        if (movement.Path == null)
            return;

        // Path handling
        var moveVector = movement.Path[movement.CurrentCorner] - agent.Position;
        var nextCornerIndex = movement.CurrentCorner + 1;
        var hasNextCorner = nextCornerIndex < movement.Path.Length;

        if (hasNextCorner)
        {
            var cornerReached = false;
            var cornerReachedEps = bot.Mover.Sprinting ? CornerSprintEpsSqr : CornerWalkEpsSqr;
            var moveVectorSqrMag = moveVector.sqrMagnitude;

            if (moveVectorSqrMag <= cornerReachedEps)
            {
                cornerReached = true;
            }
            else if (moveVectorSqrMag < 1f)
            {
                var nextCorner = movement.Path[nextCornerIndex];
                if (!NavMesh.Raycast(agent.Position, nextCorner, out _, NavMesh.AllAreas))
                    cornerReached = true;
            }

            if (cornerReached)
            {
                movement.CurrentCorner = nextCornerIndex;
                moveVector = movement.Path[movement.CurrentCorner] - agent.Position;
            }
        }
        else
        {
            // Last corner reached: maybe the path doesn't go all the way to
            // the target (navmesh truncation, dynamic geometry). Retry if
            // we're still too far from the actual destination.
            if ((movement.Path[movement.CurrentCorner] - agent.Player.Position).sqrMagnitude <= TargetEpsSqr)
            {
                if ((movement.Target - movement.Path[movement.CurrentCorner]).sqrMagnitude > TargetEpsSqr)
                {
                    MoveRetry(agent, movement.Target);
                    return;
                }

                Log.Debug($"{agent} movement destination reached");
                // Don't reset the target — it hasn't changed, we just reached it.
                ResetPath(agent);
                return;
            }
        }

        // Calculate a 2D path deviation so the spring pull-back doesn't
        // drag the bot backwards on uneven terrain.
        var agentPos2d = new Vector2(agent.Position.x, agent.Position.z);
        var closestPointOnPath = PathHelper.ClosestPointOnLine(
            movement.Path[Math.Max(0, movement.CurrentCorner - 1)].ToVector2(),
            movement.Path[movement.CurrentCorner].ToVector2(),
            agentPos2d
        );

        // Spring force pulling the bot back to the path if they've veered off.
        var pathDeviationSpring = (closestPointOnPath - agentPos2d).ToVector3();

        // Steering
        moveVector.Normalize();
        moveVector += pathDeviationSpring;
        moveVector.Normalize();

        var moveDir = CalcMoveDirection(moveVector, player.Rotation);
        player.CharacterController.SetSteerDirection(moveVector);
        player.Move(moveDir);
        bot.AimingManager.CurrentAiming.Move(player.Speed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 CalcMoveDirection(Vector3 direction, Vector2 rotation)
    {
        var vector = Quaternion.Euler(0f, 0f, rotation.x) * new Vector2(direction.x, direction.z);
        return new Vector2(vector.x, vector.y);
    }

    private static bool HandleDoors(Agent agent)
    {
        var currentVoxel = agent.Bot.VoxelesPersonalData.CurVoxel;

        if (currentVoxel == null) return false;

        if (currentVoxel.DoorLinks.Count == 0)
            return false;

        var foundDoors = false;

        for (var i = 0; i < currentVoxel.DoorLinks.Count; i++)
        {
            var doorLink = currentVoxel.DoorLinks[i];
            var door = doorLink.Door;

            if ((door.transform.position - agent.Position).sqrMagnitude > 9f)
                continue;

            foundDoors = true;

            if (!(door.InteractingPlayer == null && door.enabled && door.Operatable && door.DoorState != EDoorState.Open))
                continue;

            // Only open doors that the bot's current path actually crosses.
            // Without this gate, every bot walking past a hallway flings
            // open every door it brushes — a scav strolling through Dorms
            // ends up popping every room. Path-crossing test: project the
            // doorway frame segment (Close1 ↔ Close2_Normal, stable
            // regardless of swing state) onto the XZ plane and check if
            // any of the next few path segments intersect it.
            if (!PathCrossesDoorway(agent, doorLink)) continue;

            // Locked doors: only PMCs may attempt to unlock. Scavs/bosses/
            // raiders don't carry door keys in their loadouts, and even if
            // vmethod_1 silently fails without a key, letting every bot
            // poll the interaction wastes ticks and produces unrealistic
            // behaviour. Real unlock still gated by key inventory inside
            // BSG's vmethod_1.
            if (door.DoorState == EDoorState.Locked)
            {
                var role = agent.Bot?.Profile?.Info?.Settings?.Role;
                if (!role.HasValue || !role.Value.IsPMC()) continue;

                // The squad rolled (or was granted 100% as a Main anchor)
                // for this door at dispatch time — call Door.Unlock() to
                // bypass the BSG key check. Unlock() flips the state to
                // Shut on the next coroutine yield; the next HandleDoors
                // tick will then take the normal OpenDoor branch since
                // DoorState != Open & != Locked. Without the ForceUnlock
                // tag, fall through cleanly — vmethod_1 would silently
                // fail without a key anyway.
                if (agent.Squad != null && agent.Squad.ForceUnlockDoorIds.Contains(door.GetInstanceID()))
                {
                    door.Unlock();
                    Log.Debug($"{agent} force-unlocked {door.Id} (was Locked, squad had ForceUnlock tag)");
                    continue; // next tick: door is Shut → normal Open path runs
                }
                continue;
            }

            OpenDoor(agent, door);
        }

        return foundDoors;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OpenDoor(Agent agent, Door door)
        => agent.Player.vmethod_1(door, new InteractionResult(EInteractionType.Open));

    // Cap how far ahead we look on the path when deciding whether the bot
    // is actually walking through this door. 5 segments comfortably covers
    // the next ~10-15m which is well past the 3m proximity gate above,
    // while keeping the per-tick cost bounded.
    private const int PathCrossLookaheadSegments = 5;

    /// <summary>
    /// True if the bot's current path crosses the doorway frame (segment
    /// from Close1 to Close2_Normal projected onto XZ). Without this
    /// gate, HandleDoors would open every door the bot brushes past in a
    /// hallway just because the door is within 3m and in the same voxel.
    /// </summary>
    private static bool PathCrossesDoorway(Agent agent, NavMeshDoorLink doorLink)
    {
        var movement = agent.Movement;
        if (!movement.HasPath || movement.Path == null) return false;

        var b1 = doorLink.Close1;
        var b2 = doorLink.Close2_Normal;

        // First segment: bot position → current corner.
        var current = movement.CurrentCorner;
        if (current >= movement.Path.Length) return false;
        if (PathHelper.Segments2dIntersectXZ(agent.Position, movement.Path[current], b1, b2)) return true;

        // Following segments up to the lookahead cap.
        var end = Math.Min(movement.Path.Length - 1, current + PathCrossLookaheadSegments);
        for (var i = current; i < end; i++)
        {
            if (PathHelper.Segments2dIntersectXZ(movement.Path[i], movement.Path[i + 1], b1, b2)) return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ResetPath(Agent agent, MovementStatus status = MovementStatus.Stopped)
    {
        // Explicitly DON'T reset the target — it hasn't changed. Only the
        // path is supposed to be deleted.
        agent.Movement.Path = null;
        agent.Movement.Status = status;
        agent.Movement.CurrentCorner = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AssignPath(Movement movement, NavJob job)
    {
        movement.Target = job.Target;
        movement.Path = job.Path;
        movement.Status = MovementStatus.Moving;
        movement.CurrentCorner = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanSprint(Agent agent)
    {
        var angleJitterLimit = agent.Movement.Urgency switch
        {
            MovementUrgency.High => 45f,
            MovementUrgency.Medium => 30f,
            MovementUrgency.Low => 20f,
            _ => 30f
        };

        var bot = agent.Bot;
        // Don't run indoors (prevalence of complex geometry).
        var isOutside = bot.AIData.EnvironmentId == 0;
        var isAbleToSprint = bot.GetPlayer.MovementContext.CanSprint;
        // Only sprint when there's no explicit look target.
        var isFreeLook = agent.Look.Target == null;
        // Skip sprinting through twisty paths.
        var isPathSmooth = PathHelper.CalculatePathAngleJitter(agent.Movement.Path, agent.Movement.CurrentCorner, 10f) < angleJitterLimit;

        return isOutside && isAbleToSprint && isFreeLook && isPathSmooth;
    }

    private class StuckRemediation(MovementSystem movementSystem, List<Player> humanPlayers)
    {
        private readonly SoftStuckRemediation _softRemediation = new(0.2f);
        private readonly HardStuckRemediation _hardRemediation = new(movementSystem, humanPlayers, 0.2f);

        public void Update(Agent agent)
        {
            var stuck = agent.Stuck;

            if (stuck.Pacing.Blocked())
                return;

            _softRemediation.Update(agent);
            _hardRemediation.Update(agent);
        }
    }

    private class SoftStuckRemediation(float staleThreshold)
    {
        private const float SpeedThreshold = 3.5f / 2f; // half the moveSpeed-adjusted expected distance
        private const float VaultAttemptDelay = 1.5f;
        private const float JumpAttemptDelay = 1.5f + VaultAttemptDelay;
        private const float FailedDelay = 3f + JumpAttemptDelay;

        public void Update(Agent agent)
        {
            var stuck = agent.Stuck.Soft;

            var deltaTime = Time.time - stuck.LastUpdate;
            stuck.LastUpdate = Time.time;

            var currentPos = agent.Position;
            var lastPos = stuck.LastPosition;
            stuck.LastPosition = currentPos;

            // Asymmetric speed buffering:
            //   - currentSpeed ≤ lastSpeed: use currentSpeed (don't
            //     over-estimate expected distance during a slowdown).
            //   - currentSpeed > lastSpeed: EWMA with alpha=0.9 (gives
            //     the agent a frame or two to actually build distance).
            var currentSpeed = agent.Player.MovementContext.CharacterMovementSpeed;
            var moveSpeed = currentSpeed <= stuck.LastSpeed ? currentSpeed : 0.9f * stuck.LastSpeed + 0.1f * currentSpeed;
            stuck.LastSpeed = moveSpeed;

            if (moveSpeed <= 0.01)
            {
                stuck.Reset();
                return;
            }

            if (deltaTime > staleThreshold)
            {
                stuck.Reset();
                return;
            }

            var expectedSpeed = SpeedThreshold * moveSpeed;
            var stuckThreshold = expectedSpeed * deltaTime;

            var moveVector = currentPos - lastPos;
            // Ignore vertical axis (filter out jumps).
            moveVector.y = 0f;

            var distanceMoved = moveVector.magnitude;
            if (distanceMoved > stuckThreshold)
            {
                stuck.Reset();
                return;
            }

            stuck.Timer += deltaTime;

            switch (stuck.Status)
            {
                case SoftStuckStatus.None when stuck.Timer >= VaultAttemptDelay:
                    Log.Debug($"{agent} is stuck, attempting to vault.");
                    stuck.Status = SoftStuckStatus.Vaulting;
                    agent.Player.MovementContext?.TryVaulting();
                    break;
                case SoftStuckStatus.Vaulting when stuck.Timer >= JumpAttemptDelay:
                    Log.Debug($"{agent} is stuck, attempting to jump.");
                    stuck.Status = SoftStuckStatus.Jumping;
                    agent.Player.MovementContext?.TryJump();
                    break;
                case SoftStuckStatus.Jumping when stuck.Timer >= FailedDelay:
                    stuck.Status = SoftStuckStatus.Failed;
                    break;
                case SoftStuckStatus.Failed:
                default:
                    break;
            }
        }
    }

    private class HardStuckRemediation(MovementSystem movementSystem, List<Player> humanPlayers, float staleThreshold)
    {
        private const float StuckRadiusSqr = 3f * 3f;

        private const float PathRetryDelay = 5f;
        private const float TeleportDelay = 5f + PathRetryDelay;
        private const float FailedDelay = 5f + TeleportDelay;

        private static readonly LayerMask LayerMaskVisCheck = 0b0000_00000_0000_0001_1000_0000_0000;

        private static readonly EBodyPartColliderType[] VisCheckBodyParts =
        {
            EBodyPartColliderType.HeadCommon,
            EBodyPartColliderType.Pelvis,
            EBodyPartColliderType.LeftForearm,
            EBodyPartColliderType.RightForearm,
            EBodyPartColliderType.LeftCalf,
            EBodyPartColliderType.RightCalf
        };

        public void Update(Agent agent)
        {
            // If the bot stays within a radius of its position 5 s ago for
            // extended periods of time, treat as stuck. Radius is
            // modulated by the bot's target velocity (deliberate slow
            // movement shouldn't false-positive).
            var stuck = agent.Stuck.Hard;

            stuck.PositionHistory.Update(agent.Position);
            stuck.AverageSpeed.Update(agent.Player.MovementContext.CharacterMovementSpeed);

            var deltaTime = Time.time - stuck.LastUpdate;
            stuck.LastUpdate = Time.time;

            if (deltaTime > staleThreshold)
            {
                Reset(stuck);
                return;
            }

            var averageSpeed = stuck.AverageSpeed.Value;
            var currentSpeed = agent.Player.MovementContext.CharacterMovementSpeed;
            // Movespeed is 0-1.
            var moveSpeed = currentSpeed <= averageSpeed ? currentSpeed : averageSpeed;

            if (moveSpeed <= 0.01 && stuck.Status != HardStuckStatus.None)
            {
                Reset(stuck);
                return;
            }

            // If the bot moved more than the radius × moveSpeed from its
            // oldest position, treat as not-stuck and reset.
            var moveDistanceSqr = stuck.PositionHistory.GetDistanceSqr();
            var stuckThresholdSqr = StuckRadiusSqr * moveSpeed;

            if (moveDistanceSqr > stuckThresholdSqr)
            {
                Reset(stuck);
                return;
            }

            stuck.Timer += deltaTime;

            switch (stuck.Status)
            {
                case HardStuckStatus.None when stuck.Timer >= PathRetryDelay:
                    Log.Debug($"{agent} is hard stuck, attempting to recalculate path.");
                    stuck.Status = HardStuckStatus.Retrying;
                    movementSystem.MoveRetry(agent, agent.Movement.Target);
                    break;
                case HardStuckStatus.Retrying when stuck.Timer >= TeleportDelay:
                    Log.Debug($"{agent} is hard stuck, attempting to teleport.");
                    stuck.Status = HardStuckStatus.Teleport;
                    AttemptTeleport(agent);
                    break;
                case HardStuckStatus.Teleport when stuck.Timer >= FailedDelay:
                    Log.Debug($"{agent} is hard stuck, giving up.");
                    stuck.Status = HardStuckStatus.Failed;
                    ResetPath(agent, MovementStatus.Failed);
                    break;
                case HardStuckStatus.Failed:
                default:
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Reset(HardStuck stuck)
        {
            if (stuck.Status == HardStuckStatus.None)
            {
                stuck.Timer = 0f;
                return;
            }

            stuck.AverageSpeed.Reset();
            stuck.PositionHistory.Reset();
            stuck.Status = HardStuckStatus.None;
            stuck.Timer = 0f;
        }

        private void AttemptTeleport(Agent agent)
        {
            for (var i = 0; i < humanPlayers.Count; i++)
            {
                var player = humanPlayers[i];

                if (player?.HealthController is not { IsAlive: true })
                    continue;

                // Don't teleport when a human player is closer than 10m.
                if ((player.Position - agent.Position).sqrMagnitude <= 100f)
                {
                    Log.Debug($"{agent} teleport proximity check failed: {player.Profile.Nickname} too close");
                    return;
                }

                var humanHeadPos = player.PlayerBones.Head.Original.position;
                var agentBodyParts = agent.Player.PlayerBones.BodyPartCollidersDictionary;

                for (var j = 0; j < VisCheckBodyParts.Length; j++)
                {
                    var bodyPartType = VisCheckBodyParts[j];
                    var bodyPart = agentBodyParts[bodyPartType];

                    // Anything we don't hit on the way → considered visible.
                    if (Physics.Linecast(humanHeadPos, bodyPart.transform.position, out _, LayerMaskVisCheck.value)) continue;

                    Log.Debug(
                        $"{agent} teleport vis check failed: player {player.Profile.Nickname} can see body part {bodyPart.BodyPartColliderType}"
                    );

                    return;
                }
            }

            var teleportPos = agent.Movement.Path[agent.Movement.CurrentCorner];
            teleportPos.y += 0.25f;
            agent.Player.Teleport(teleportPos);
            Log.Debug($"{agent} teleporting to {teleportPos}");
        }
    }
}
