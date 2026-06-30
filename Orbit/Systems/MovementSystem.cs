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
/// Per-frame movement engine. Owns the queue of pending navmesh path jobs, the corner-following +
/// path-deviation steering, door handling, sprint gating, and the two-stage stuck-detection / remediation
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
    private readonly List<Player> _humanPlayers;
    private readonly WaypointSystem _waypointSystem;
    private readonly NavMeshPath _rescuePath = new();
    private readonly List<Waypoint> _wpScratch = new();

    public MovementSystem(NavJobExecutor navJobExecutor, List<Player> humanPlayers, WaypointSystem waypointSystem)
    {
        _navJobExecutor = navJobExecutor;
        _moveJobs = new Queue<(Agent, NavJob)>(20);
        _stuckRemediation = new StuckRemediation(this, humanPlayers);
        _humanPlayers = humanPlayers;
        _waypointSystem = waypointSystem;
    }

    public void Update(List<Agent> liveAgents)
    {
        TickDoorOpenWatches();

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

                // Discard the move job if the agent is inactive (mod deactivated, bot died, etc).
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

            // Keep BSG's BotMover anchored to where the bot ACTUALLY is. BotMover.method_10 (the hard rescue
            // teleport) snaps the bot to LastGoodCastPoint when it decides the bot is stuck. The brain layer
            // sets LastGoodCastPoint to agent.Position only at the layer *transition* — so while we're in
            // control, it stays frozen at wherever the bot was when handed off. A rescue then yeets the bot
            // back to that stale anchor (sometimes their spawn). Refreshing every frame makes any rescue land
            // as a teleport-to-self no-op.
            var mover = agent.Bot?.Mover;
            if (mover != null)
            {
                var pos = agent.Position;
                mover.LastGoodCastPoint = pos;
                mover.PrevSuccessLinkedFrom_1 = pos;
                mover.PrevLinkPos = pos;
                mover.PositionOnWayInner = pos;
            }

            // Runs before UpdateMovement: an islanded bot has no path, so UpdateMovement early-returns and the
            // stuck remediation never sees it.
            TryIdleIslandRescue(agent);
            TrySpawnIslandRescue(agent, liveAgents);

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

        // Set the target up-front so callers' "is the target current?" checks see the new value immediately.
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

        // While a door interaction is in flight, the open animation only plays if the bot stops pushing
        // forward through the doorway — the interact state is silently cancelled by movement input, the
        // door stays stuck in Interacting and the bot phantom-walks. Back off toward the previous path
        // corner for the animation window instead of advancing. Observed in testing: at full walking
        // speed the door never finishes opening and the bot phases through; the only passes that opened
        // it were the ones where the bot happened to slow down.
        if (Time.time < movement.DoorInteractHoldUntil)
        {
            HoldForDoorInteraction(agent);
            return;
        }

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
            // Last corner reached: maybe the path doesn't go all the way to the target (navmesh truncation,
            // dynamic geometry). Retry if we're still too far from the actual destination.
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

        // Calculate a 2D path deviation so the spring pull-back doesn't drag the bot backwards on uneven
        // terrain.
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

    /// <summary>
    /// Step back toward the previous path corner at low speed while a door interaction plays out.
    /// Backing off (rather than just standing) also clears the bot out of the swing arc for doors
    /// that open toward them. When already at/near the hold point, no movement input is issued this
    /// tick — the bot simply stands and lets the animation finish.
    /// </summary>
    private static void HoldForDoorInteraction(Agent agent)
    {
        var movement = agent.Movement;
        var player = agent.Player;
        if (player == null) return;

        var holdTarget = movement.HasPath
            ? movement.Path[Math.Max(0, movement.CurrentCorner - 1)]
            : agent.Position;
        var backVector = holdTarget - agent.Position;
        backVector.y = 0f;
        if (backVector.sqrMagnitude < 0.09f)
            return;

        backVector.Normalize();
        agent.Bot.Mover.SetTargetMoveSpeed(DoorHoldMoveSpeed);
        player.CharacterController.SetSteerDirection(backVector);
        player.Move(CalcMoveDirection(backVector, player.Rotation));
    }

    private const float DoorHoldMoveSpeed = 0.33f;

    /// <summary>
    /// How long path-following backs off after firing a door interaction. Covers the push-open
    /// animation (~1 s) plus margin for pull-open doors whose swing takes slightly longer.
    /// </summary>
    private const float DoorInteractHoldSeconds = 1.25f;

    // Tighter than the generic 3 m door gate so the key animation plays at the door leaf, not metres back.
    private const float DoorKeyUnlockMaxDistanceSqr = 1.6f * 1.6f;

    /// <summary>
    /// Tracks every OpenDoor / force-unlock interaction we've initiated. After
    /// <see cref="DoorWatchTimeoutSeconds"/> we poll the door's actual state — if it never reached Open AND
    /// the bot has walked past the door anyway, we log a `PHANTOM-WALKED` warning. Pure diagnostic, no
    /// behaviour change; lets us audit the door-phasing class of bug from a single raid log instead of
    /// freecaming bots in real time.
    /// </summary>
    private readonly Dictionary<long, DoorOpenWatch> _pendingDoorOpens = new();

    private struct DoorOpenWatch
    {
        public Agent Agent;
        public Door Door;
        public float RequestedAtTime;
        public Vector3 DoorPos;
        public float InitDistance;
        public string Kind;
    }

    private const float DoorWatchTimeoutSeconds = 3f;
    private const float DoorWatchMinPhaseDistance = 2f;

    private static long DoorWatchKey(int agentId, int doorInstanceId)
        => ((long)agentId << 32) | (uint)doorInstanceId;

    private void StartDoorWatch(Agent agent, Door door, string kind)
    {
        var key = DoorWatchKey(agent.Id, door.GetInstanceID());
        var doorPos = door.transform.position;
        var initDist = Vector3.Distance(agent.Position, doorPos);
        _pendingDoorOpens[key] = new DoorOpenWatch
        {
            Agent = agent,
            Door = door,
            RequestedAtTime = Time.time,
            DoorPos = doorPos,
            InitDistance = initDist,
            Kind = kind,
        };
        Log.Info($"DoorWatch: {agent} initiated {kind} on door Id={door.Id} (state={door.DoorState}, dist={initDist:F1}m)");
    }

    private readonly List<long> _doorWatchRemoveBuffer = new();

    private void TickDoorOpenWatches()
    {
        if (_pendingDoorOpens.Count == 0) return;
        var now = Time.time;
        _doorWatchRemoveBuffer.Clear();
        foreach (var kv in _pendingDoorOpens)
        {
            var watch = kv.Value;
            if (watch.Door == null || watch.Agent == null)
            {
                _doorWatchRemoveBuffer.Add(kv.Key);
                continue;
            }

            var state = watch.Door.DoorState;
            var elapsed = now - watch.RequestedAtTime;
            if (elapsed < DoorWatchTimeoutSeconds) continue;

            var currentDist = Vector3.Distance(watch.Agent.Position, watch.DoorPos);
            if (currentDist > watch.InitDistance && currentDist >= DoorWatchMinPhaseDistance)
            {
                Log.Warning($"DoorWatch: {watch.Agent} PHANTOM-WALKED through door Id={watch.Door.Id} — requested {watch.Kind} {elapsed:F1}s ago, state still={state}, agent moved from {watch.InitDistance:F1}m → {currentDist:F1}m (past the door)");
            }
            else
            {
                Log.Debug($"DoorWatch: {watch.Agent} watch timeout on door Id={watch.Door.Id} after {elapsed:F1}s, state={state}, dist {watch.InitDistance:F1}m → {currentDist:F1}m — interaction may have failed but bot didn't pass through");
            }

            // Bot-driven interactions never finalize the BSG door state: the animation plays and the
            // door is visually open, but DoorState stays Interacting forever (the completion callback
            // is tied to player-side animation events bots don't emit). Accepted desync — the visual is
            // what matters. Settling the state back to Shut keeps the door alive: a door left on
            // Interacting is skipped by HandleDoors (later bots would phase through silently) and shows
            // no interaction prompt to the player.
            if (state == EDoorState.Interacting)
            {
                try
                {
                    watch.Door.DoorState = EDoorState.Shut;
                    Log.Debug($"DoorWatch: reset door Id={watch.Door.Id} Interacting → Shut after {watch.Kind} window (bot interactions never finalize door state)");
                }
                catch (System.Exception e)
                {
                    Log.Debug($"DoorWatch: failed to reset door Id={watch.Door.Id}: {e.Message}");
                }
            }
            _doorWatchRemoveBuffer.Add(kv.Key);
        }
        for (var i = 0; i < _doorWatchRemoveBuffer.Count; i++) _pendingDoorOpens.Remove(_doorWatchRemoveBuffer[i]);
    }

    private bool HandleDoors(Agent agent)
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

            // Also reject doors mid-animation (state=Interacting) — the door is already opening / closing
            // and BSG silently no-ops a second vmethod_1 call against an in-flight transition. The
            // DoorWatch diagnostic on Woods saw 3/3 interactions hit doors already in the Interacting
            // state, all 3 watch-timeouts at 3s with state still=Interacting. The original guard checked
            // InteractingPlayer != null which handles a player actively pressing F on the door, but the
            // state can remain Interacting for ~1s AFTER the player releases / the AI lets go, with
            // InteractingPlayer back to null — that's the window we were hitting.
            if (!(door.InteractingPlayer == null && door.enabled && door.Operatable
                  && door.DoorState != EDoorState.Open && door.DoorState != EDoorState.Interacting))
                continue;

            // Only open doors the bot is actively heading toward — distance + forward-cone check.
            // Without this gate, every bot in a hallway would pop every door they brush past just because
            // the door is within voxel range. See IsBotApproachingDoor for the rationale (the old
            // segment-intersection test missed ~80 % of doors in practice).
            if (!IsBotApproachingDoor(agent, doorLink)) continue;

            // Locked doors: only PMCs may attempt to unlock. Scavs/bosses/ raiders don't carry door keys in
            // their loadouts, and even if vmethod_1 silently fails without a key, letting every bot poll the
            // interaction wastes ticks and produces unrealistic behaviour. Real unlock still gated by key
            // inventory inside BSG's vmethod_1.
            if (door.DoorState == EDoorState.Locked)
            {
                var role = agent.Bot?.Profile?.Info?.Settings?.Role;
                if (!role.HasValue || !role.Value.IsPMC()) continue;

                if ((door.transform.position - agent.Position).sqrMagnitude > DoorKeyUnlockMaxDistanceSqr)
                    continue;

                // Unlock for ANY PMC at a carver-opened door, not only the granting squad: the carver is shared
                // per door, so another bot would otherwise phase through the still-locked leaf (bots ignore door
                // colliders). Door.Unlock() flips it to Shut on the next coroutine yield so the following tick
                // takes the normal OpenDoor branch.
                var doorIdForUnlock = door.GetInstanceID();
                if ((agent.Squad != null && agent.Squad.ForceUnlockDoorIds.Contains(doorIdForUnlock))
                    || Orbit.Helpers.DoorNavMesh.IsCarverOpened(doorIdForUnlock))
                {
                    if (_doorInteractCooldown.TryGetValue(doorIdForUnlock, out var lastUnlockTime)
                        && Time.time - lastUnlockTime < DoorInteractCooldownSeconds)
                    {
                        continue; // already unlocking this door, wait for animation
                    }
                    // BSG silently drops a sprinting / proned / ADS'd bot's interaction; prep like OpenDoor.
                    try
                    {
                        var unlockBot = agent.Bot;
                        unlockBot.Sprint(false);
                        unlockBot.SetPose(1f);
                        unlockBot.Mover?.SetTargetMoveSpeed(1f);
                    }
                    catch (System.Exception prepEx)
                    {
                        Log.Debug($"{agent} unlock prep on {door.Id} threw (non-fatal): {prepEx.Message}");
                    }

                    // Don't route through Door.Interact(Unlock): it casts to KeyInteractionResultClass, which
                    // dereferences a real KeyComponent, so a keyless bot NREs inside vmethod_1. Replicate
                    // ExecuteDoorInteraction by hand: read interaction params WHILE still Locked (resolves
                    // AnimationId to the DoorKeyOpen gesture from the locked state), unlock the latch directly,
                    // then drive the hands animation via SetInteractInHands.
                    try
                    {
                        var keyAnim = door.GetInteractionParameters(agent.Position);
                        door.LockForInteraction();
                        if (door.DoorState == EDoorState.Locked)
                            door.Unlock();
                        if (!door.interactWithoutAnimation)
                            agent.Player.MovementContext.SetInteractInHands((EInteraction)keyAnim.AnimationId);
                    }
                    catch (System.Exception unlockEx)
                    {
                        Log.Debug($"{agent} key-unlock on {door.Id} threw (non-fatal), flooring to direct Unlock: {unlockEx.Message}");
                        if (door.DoorState == EDoorState.Locked) door.Unlock();
                    }
                    _doorInteractCooldown[doorIdForUnlock] = Time.time;
                    // Hold through the whole unlock + open. Without it the bot phantom-walks the still-shut leaf:
                    // the carver is open (navmesh passes) and ORBIT ignores door<->bot collision. Bridge the
                    // interact cooldown so OpenDoor finishes the open before we release path-following.
                    agent.Movement.DoorInteractHoldUntil = Time.time + DoorInteractCooldownSeconds + DoorInteractHoldSeconds;
                    Log.Debug($"{agent} unlocked {door.Id} on arrival (was Locked, ORBIT carver-opened) — holding {DoorInteractCooldownSeconds + DoorInteractHoldSeconds:F1}s for unlock+open, no phase-through");
                    StartDoorWatch(agent, door, "Unlock");
                    continue; // next tick: door is Shut → normal Open path runs
                }
                continue;
            }

            if (OpenDoor(agent, door))
                StartDoorWatch(agent, door, "Open");
        }

        return foundDoors;
    }

    /// <summary>
    /// Reset the player's "can use prop" state machine, ask BSG to construct a validated InteractionResult
    /// via <see cref="Door.Interact"/> (this is the call that checks key inventory, ownership, lock state,
    /// and produces the proper internal transition struct), then fire vmethod_1 with THAT result. The
    /// previous implementation built an InteractionResult by hand — BSG silently no-op'd when the missing
    /// internal fields were stale, which manifested as our PHANTOM-WALK signature on Customs dorm doors
    /// (148 interactions initiated, 0 logged successfully opened, 7 phantom-walks). Also pre-enables
    /// IgnoreInteractionCollision so the bot doesn't bounce off the door's collider during the animation
    /// window.
    /// </summary>
    private bool OpenDoor(Agent agent, Door door)
    {
        var player = agent.Player;
        if (player == null) return false;
        // Per-door cooldown: HandleDoors runs every frame, but BSG's vmethod_1 takes a few frames to
        // transition the door from Shut → Interacting → Open. If we re-fire vmethod_1 each frame in
        // that window, BSG silently no-ops the duplicate calls and the door may never finish opening
        // (observed: re-firing vmethod_1 every frame produced well over a hundred calls in a row with
        // zero confirmed transitions to Open). 1.5 s is enough to cover the open / unlock animation window
        // and matches SAIN's _doorInteractionEndTime.
        var doorId = door.GetInstanceID();
        if (_doorInteractCooldown.TryGetValue(doorId, out var lastTime)
            && Time.time - lastTime < DoorInteractCooldownSeconds)
        {
            return false;
        }
        try
        {
            // BSG won't play the door open animation unless the bot is in the right movement state at
            // the moment vmethod_1 fires — a sprinting / proning / ADS'd bot's interaction is silently
            // dropped, vmethod_1 returns successfully but visually nothing happens. The bot then walks
            // through the collider (which we ignore for the duration) and phantom-walks. Prepare the
            // bot exactly like the vanilla door interact flow: stand, no prone, no sprint, no ADS,
            // target pose 1 (standing), normal walking speed.
            var botOwner = agent.Bot;
            try
            {
                botOwner.Sprint(false);
                botOwner.SetPose(1f);
                botOwner.Mover?.SetTargetMoveSpeed(1f);
            }
            catch (System.Exception prepEx)
            {
                Log.Debug($"{agent} OpenDoor prep on {door.Id} threw (non-fatal): {prepEx.Message}");
            }

            player.MovementContext.ResetCanUsePropState();
            var gstruct = Door.Interact(player, EInteractionType.Open);
            if (!gstruct.Succeeded)
            {
                Log.Debug($"{agent} OpenDoor on {door.Id}: Door.Interact returned non-success — interaction rejected by BSG (likely lock / key / state)");
                return false;
            }
            player.vmethod_1(door, gstruct.Value);
            // Set collision-pass AFTER vmethod_1 so the door's animation can drive the bot's traversal
            // through the swing arc. Order matters: setting it before vmethod_1 lets the bot rush the
            // collider before the animation has actually started.
            if (door.Collider != null)
                player.MovementContext.IgnoreInteractionCollision(door.Collider, true);
            // Freeze forward path-following for the animation window — movement input cancels the
            // interact state and the door never completes Interacting → Open (see UpdateMovement).
            agent.Movement.DoorInteractHoldUntil = Time.time + DoorInteractHoldSeconds;
            _doorInteractCooldown[doorId] = Time.time;
            // Remember the door so the stuck remediation can close it later if the bot's nav gets
            // wedged on a swing-arc-into-corridor pattern. Cap the list at MaxRecentOpenedDoors so it
            // doesn't grow unbounded over a long raid.
            var list = agent.RecentOpenedDoors;
            list.Remove(door); // dedup if reopened
            list.Add(door);
            if (list.Count > MaxRecentOpenedDoors) list.RemoveAt(0);
            return true;
        }
        catch (System.Exception e)
        {
            Log.Debug($"{agent} OpenDoor on {door.Id} threw: {e.Message}");
            return false;
        }
    }

    private readonly Dictionary<int, float> _doorInteractCooldown = new();
    private const float DoorInteractCooldownSeconds = 1.5f;

    private const int MaxRecentOpenedDoors = 10;

    /// <summary>
    /// Min XZ distance between the agent and a door before we consider the door "behind" the agent and
    /// safe to close without slamming into them. 4 m is enough that an agent looting in a small room
    /// doesn't auto-close the door they just walked through.
    /// </summary>
    private const float CloseDoorBehindMinDistance = 4f;

    /// <summary>
    /// Max XZ distance between the agent and a door before we stop considering it relevant to the
    /// current stuck. A door 100 m behind cannot possibly be the cause of the bot's nav-wedge right now,
    /// closing it is pure noise. 30 m is a comfortable upper bound for "still nearby on the same
    /// section of the map".
    /// </summary>
    private const float CloseDoorBehindMaxDistance = 30f;

    /// <summary>
    /// Window (in seconds) over which we count distinct per-agent blacklist firings to detect the
    /// "rapid POI churn" pattern. A bot can blacklist one POI, switch to another, then blacklist that
    /// one too a few seconds later — different POIs, so the per-POI 3-fail counter never catches the
    /// across-POI switching. Tracking distinct fires in a sliding window does.
    /// </summary>
    private const float RapidChurnWindowSeconds = 10f;

    /// <summary>
    /// Number of distinct blacklist firings in <see cref="RapidChurnWindowSeconds"/> that triggers the
    /// door-close remediation. 3 fires in 10 s ≈ 1 every 3 s, well above the baseline rate of healthy
    /// play (~1 blacklist per 30-60 s). 2 was too sensitive — a single unlucky POI placement followed
    /// by a sweep-chain miss would tip it. 3 confirms the bot is genuinely churning, not just having
    /// one bad streak.
    /// </summary>
    private const int RapidChurnThreshold = 3;

    /// <summary>
    /// Record a per-agent POI blacklist firing and, if the bot has just crossed the rapid-churn
    /// threshold, fire the close-doors-behind remediation. Called from both the 3-fail arrival
    /// blacklist (GotoObjectiveAction.TrackArrivalFailure) and the Guard-on-loot-POI watchdog
    /// (GuardAction.Update) — either signal counts toward the same window.
    /// </summary>
    public void RegisterPoiBlacklistAndMaybeCloseDoors(Agent agent)
    {
        var times = agent?.RecentPoiBlacklistTimes;
        if (times == null) return;
        var now = Time.time;
        var cutoff = now - RapidChurnWindowSeconds;
        // Drop expired entries
        for (var i = times.Count - 1; i >= 0; i--)
            if (times[i] < cutoff) times.RemoveAt(i);
        times.Add(now);
        if (times.Count >= RapidChurnThreshold)
        {
            Log.Debug($"{agent} hit rapid POI churn threshold ({times.Count} blacklists in {RapidChurnWindowSeconds:F0}s) — closing doors behind to free paths");
            CloseRecentDoorsBehindAgent(agent);
            times.Clear(); // don't immediately re-trigger; the next fire restarts the window
        }
    }

    /// <summary>
    /// Fired by the stuck remediation paths (hard-stuck recalculate, per-agent 3-fail blacklist) when
    /// the bot has trouble pathing. Closes any still-Open doors the agent personally opened and that
    /// they've now walked away from — swing-arc geometry from one of those doors is the prime suspect
    /// for wedging the bot's nav, and a recalculated path through a clean cross-section often succeeds.
    /// Cheap: only walks the agent's own opened-doors list, only fires under explicit stuck signals.
    /// </summary>
    /// <param name="includeNear">When true (hard-stuck path), also close doors inside the normal 4 m floor: a
    /// pinned bot is usually wedged on the swing arc of a door right next to it, which the floor would skip.</param>
    public void CloseRecentDoorsBehindAgent(Agent agent, bool includeNear = false)
    {
        var list = agent?.RecentOpenedDoors;
        if (list == null || list.Count == 0) return;
        var player = agent.Player;
        if (player == null) return;
        var agentPos = agent.Position;
        var closed = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var door = list[i];
            if (door == null || !door.enabled || !door.Operatable)
            {
                list.RemoveAt(i);
                continue;
            }
            // Mid-animation: let the DoorWatch settle the state first, retry on the next fire.
            if (door.DoorState == EDoorState.Interacting) continue;
            var dist = XzDistance(agentPos, door.transform.position);
            if (!includeNear && dist < CloseDoorBehindMinDistance) continue;
            if (dist > CloseDoorBehindMaxDistance)
            {
                // Door is far away — can't possibly be causing the current stuck. Drop it from the
                // tracking list so the next fire doesn't waste cycles on it.
                list.RemoveAt(i);
                continue;
            }
            try
            {
                // Every door in this list was opened by us and sits visually open, but its state reads
                // Shut (bot interactions never finalize state; the DoorWatch settled it). BSG validates
                // Close against Open state only — re-align the state with the visual truth first, then
                // settle it to Shut right after firing the close, matching the swing we just played.
                if (door.DoorState != EDoorState.Open)
                    door.DoorState = EDoorState.Open;
                player.MovementContext.ResetCanUsePropState();
                var gstruct = Door.Interact(player, EInteractionType.Close);
                if (gstruct.Succeeded)
                {
                    player.vmethod_1(door, gstruct.Value);
                    door.DoorState = EDoorState.Shut;
                    _doorInteractCooldown[door.GetInstanceID()] = Time.time;
                    closed++;
                    var wedge = dist < CloseDoorBehindMinDistance ? " — wedged in doorway" : "";
                    Log.Debug($"{agent} closed previously-opened door {door.Id} (stuck remediation, dist {dist:F1}m{wedge})");
                }
                else
                {
                    door.DoorState = EDoorState.Shut;
                }
            }
            catch (System.Exception e)
            {
                Log.Debug($"{agent} CloseRecentDoorsBehindAgent on {door.Id} threw: {e.Message}");
            }
            list.RemoveAt(i);
        }
        if (closed > 0)
            Log.Info($"{agent} closed {closed} previously-opened door(s) on stuck signal to free the path");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float XzDistance(Vector3 a, Vector3 b)
    {
        var dx = a.x - b.x;
        var dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Cap how far ahead we look on the path when deciding whether the bot is actually walking through this
    // door. 5 segments comfortably covers the next ~10-15m which is well past the 3m proximity gate above,
    // while keeping the per-tick cost bounded.
    private const int PathCrossLookaheadSegments = 5;

    /// <summary>
    /// True if the bot's forward ray crosses the doorway threshold — distance ≤ 3 m to door center AND
    /// the bot's forward direction (path next-corner or velocity) sweeps through the doorway frame
    /// segment (Close1 ↔ Close2_Normal projected onto XZ). Replaces the previous segment-intersection
    /// test against pre-computed path segments: that test failed when the path was stale or short, and
    /// missed bots driven by BSG nav directly (Movement.Path null). In practice the old test let bots
    /// walk through most doors while only registering a couple of DoorWatch entries.
    ///
    /// Why forward-ray instead of cone-around-door-position: a cone catches doors that are slightly to
    /// the side (bot walking down a narrow corridor with doors on the wall would pop them all). The
    /// threshold-segment intersection only fires when the bot's IMMEDIATE forward path crosses the
    /// doorway line — bot walking parallel to a wall doesn't trigger, bot turning to enter a doorway
    /// does.
    /// </summary>
    private static bool IsBotApproachingDoor(Agent agent, NavMeshDoorLink doorLink)
    {
        var door = doorLink.Door;
        if (door == null) return false;

        var agentPos = agent.Position;
        var doorPos = door.transform.position;
        var dx = doorPos.x - agentPos.x;
        var dz = doorPos.z - agentPos.z;
        var distSqr = dx * dx + dz * dz;
        if (distSqr > 9f) return false; // > 3 m XZ — out of reach for this tick

        // Every test here is projected onto XZ, so a door on the floor above/below reads as adjacent. A
        // same-floor approach stays within ~1 m of the door pivot in Y; reject anything past 1.5 m.
        const float VerticalReachMeters = 1.5f;
        if (Mathf.Abs(doorPos.y - agentPos.y) > VerticalReachMeters) return false;

        // Bot's forward direction — prefer the path's next-corner direction, fall back to live
        // velocity for bots whose nav is driven by BSG directly.
        var movement = agent.Movement;
        Vector3 forward;
        if (movement.HasPath && movement.Path != null
            && movement.CurrentCorner < movement.Path.Length)
        {
            forward = movement.Path[movement.CurrentCorner] - agentPos;
        }
        else
        {
            forward = agent.Player?.Velocity ?? Vector3.zero;
        }
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.04f) return false; // not moving meaningfully

        forward.Normalize();

        // Forward ray endpoint — far enough ahead to span any door at < 3 m distance even when the
        // bot is approaching diagonally.
        const float ForwardRayLength = 3.5f;
        var rayEnd = new Vector3(
            agentPos.x + forward.x * ForwardRayLength,
            agentPos.y,
            agentPos.z + forward.z * ForwardRayLength);

        // Crosses the doorway threshold segment (Close1 ↔ Close2_Normal) projected onto XZ.
        if (PathHelper.Segments2dIntersectXZ(agentPos, rayEnd, doorLink.Close1, doorLink.Close2_Normal))
            return true;

        // Fallback: ray the door's own collider. The threshold segment is a thin line at the frame's
        // base — an off-axis approach can have the forward ray pass over the panel without crossing
        // that segment in XZ (observed: bots phased through doors reached diagonally, with zero
        // DoorWatch entries). "About to physically touch the door panel" is approach evidence
        // regardless of angle, and Collider.Raycast tests just this one collider, not the scene.
        var doorCollider = door.Collider;
        if (doorCollider != null)
        {
            var ray = new Ray(agentPos + Vector3.up, forward);
            if (doorCollider.Raycast(ray, out _, DoorColliderRayLength))
                return true;
        }
        return false;
    }

    private const float DoorColliderRayLength = 1.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ResetPath(Agent agent, MovementStatus status = MovementStatus.Stopped)
    {
        // Explicitly DON'T reset the target — it hasn't changed. Only the path is supposed to be deleted.
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

    // ── Teleport safety (shared by hard-stuck teleport + idle-island rescue) ──────────────────────────

    private static readonly LayerMask TeleportVisLayerMask = 0b0000_00000_0000_0001_1000_0000_0000;

    private static readonly EBodyPartColliderType[] TeleportVisBodyParts =
    {
        EBodyPartColliderType.HeadCommon,
        EBodyPartColliderType.Pelvis,
        EBodyPartColliderType.LeftForearm,
        EBodyPartColliderType.RightForearm,
        EBodyPartColliderType.LeftCalf,
        EBodyPartColliderType.RightCalf
    };

    // Safe to teleport only when no live human is within 10 m and none has line of sight to any tracked body
    // part — a player watching a bot blink across the map is the worst-case visual regression.
    private static bool TeleportSafe(Agent agent, List<Player> humanPlayers)
    {
        var agentPos = agent.Position;
        for (var i = 0; i < humanPlayers.Count; i++)
        {
            var player = humanPlayers[i];
            if (player?.HealthController is not { IsAlive: true }) continue;

            if ((player.Position - agentPos).sqrMagnitude <= 100f)
            {
                Log.Debug($"{agent} teleport proximity check failed: {player.Profile.Nickname} too close");
                return false;
            }

            var humanHeadPos = player.PlayerBones.Head.Original.position;
            var agentBodyParts = agent.Player.PlayerBones.BodyPartCollidersDictionary;
            for (var j = 0; j < TeleportVisBodyParts.Length; j++)
            {
                var bodyPart = agentBodyParts[TeleportVisBodyParts[j]];
                // Linecast true == view blocked == body part not visible.
                if (Physics.Linecast(humanHeadPos, bodyPart.transform.position, out _, TeleportVisLayerMask.value)) continue;
                Log.Debug($"{agent} teleport vis check failed: {player.Profile.Nickname} can see {bodyPart.BodyPartColliderType}");
                return false;
            }
        }
        return true;
    }

    // ── Idle-island rescue ────────────────────────────────────────────────────────────────────────────
    // A bot on a navmesh chunk disconnected from the map can never path to its objective and the stuck
    // remediation never sees it (UpdateMovement early-returns with no path). This watchdog runs regardless of
    // path state: a bot far from its objective that hasn't moved for a window gets one teleport to the nearest
    // navmesh point connected to that objective. One-shot per bot so it can't loop.

    private const float IdleRescueNoMoveRadiusSqr = 3f * 3f;
    private const float IdleRescueThresholdSeconds = 25f;
    private const float IdleRescueMinObjectiveDistSqr = 12f * 12f;
    private static readonly float[] IdleRescueRingRadii = { 4f, 8f, 16f, 28f, 45f };

    // ── Spawn-island rescue thresholds ──
    private const float SpawnIslandGraceSeconds = 30f;             // parked near spawn this long before probing
    private const float SpawnIslandMoveRadiusSqr = 20f * 20f;      // got >this from spawn => not islanded, stop
    private const float SpawnIslandMinReferenceDistSqr = 15f * 15f; // ignore reference agents basically on top of us
    private const float SpawnIslandBotSafetyRadiusSqr = 5f * 5f;   // keep the TP destination clear of players/bots
    private const float SpawnIslandWaypointSearchRadius = 80f;     // look this far from the bot for a player-reachable waypoint

    private void TryIdleIslandRescue(Agent agent)
    {
        var stuck = agent.Stuck;
        if (stuck.IdleRescued) return;

        // Only rescue a bot actively trying to REACH its objective. A guarding bot (Status == Finished) sits
        // deliberately still, often far from a wide anchor's centre; teleporting it would yank it off its post.
        if (agent.Objective?.Status != ObjectiveStatus.Moving)
        {
            stuck.IdleRescueSince = -1f;
            return;
        }

        // Use the agent's own objective, not the squad anchor: a follower holding a splinter position can be
        // far from the anchor, and gating on the anchor would teleport it mid-guard.
        var objLoc = agent.Objective?.Location;
        if (objLoc == null)
        {
            stuck.IdleRescueSince = -1f;
            return;
        }

        var pos = agent.Position;

        // Already near the objective: it arrived, not islanded.
        if ((objLoc.Position - pos).sqrMagnitude < IdleRescueMinObjectiveDistSqr)
        {
            stuck.IdleRescueSince = -1f;
            return;
        }

        if (stuck.IdleRescueSince < 0f || (pos - stuck.IdleRescueAnchor).sqrMagnitude > IdleRescueNoMoveRadiusSqr)
        {
            stuck.IdleRescueAnchor = pos;
            stuck.IdleRescueSince = Time.time;
            return;
        }

        if (Time.time - stuck.IdleRescueSince < IdleRescueThresholdSeconds) return;

        if (RescueTeleportToConnectedPoint(agent, objLoc.Position) || RescueTeleportNearSquadmate(agent))
            stuck.IdleRescued = true;

        // Re-arm either way: on failure, retry after another full window rather than hammering CalculatePath.
        stuck.IdleRescueSince = Time.time;
    }

    private bool RescueTeleportToConnectedPoint(Agent agent, Vector3 objectivePos, int startRing = 0)
    {
        if (!TeleportSafe(agent, _humanPlayers)) return false;

        var pos = agent.Position;
        for (var ri = startRing; ri < IdleRescueRingRadii.Length; ri++)
        {
            var r = IdleRescueRingRadii[ri];
            for (var a = 0; a < 8; a++)
            {
                var ang = a * (Mathf.PI * 2f / 8f);
                var candidate = pos + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                if (!NavMesh.SamplePosition(candidate, out var hit, 2f, NavMesh.AllAreas)) continue;
                // Require a point CONNECTED to the objective; SamplePosition alone could snap back onto the island.
                if (!NavMesh.CalculatePath(hit.position, objectivePos, NavMesh.AllAreas, _rescuePath)) continue;
                if (_rescuePath.status != NavMeshPathStatus.PathComplete) continue;

                var dest = hit.position;
                dest.y += 0.25f;
                agent.Player.Teleport(dest);
                ResetPath(agent);
                Log.Info($"{agent} idle-island rescue: teleported {Vector3.Distance(pos, dest):F0}m to a navmesh point connected to its objective (stranded {IdleRescueThresholdSeconds:F0}s)");
                return true;
            }
        }
        Log.Debug($"{agent} idle-island rescue: no connected navmesh point within {IdleRescueRingRadii[^1]:F0}m — trying squadmate fallback");
        return false;
    }

    // Fallback when no objective-connected point exists: a live squadmate reached its own objective, so its
    // position is usable navmesh — enough to get the bot off a dead chunk.
    private bool RescueTeleportNearSquadmate(Agent agent)
    {
        var squad = agent.Squad;
        if (squad?.Members == null) return false;
        if (!TeleportSafe(agent, _humanPlayers)) return false;

        var pos = agent.Position;
        const float minDistSqr = 3f * 3f; // skip a mate on top of us
        Agent best = null;
        var bestDistSqr = float.MaxValue;
        var bestDest = Vector3.zero;
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var m = squad.Members[i];
            if (m == null || m == agent) continue;
            if (m.Player?.HealthController is not { IsAlive: true }) continue;
            var d = (m.Position - pos).sqrMagnitude;
            if (d < minDistSqr || d >= bestDistSqr) continue;
            if (!NavMesh.SamplePosition(m.Position, out var hit, 3f, NavMesh.AllAreas)) continue;
            best = m;
            bestDistSqr = d;
            bestDest = hit.position;
        }
        if (best == null) return false;

        bestDest.y += 0.25f;
        agent.Player.Teleport(bestDest);
        ResetPath(agent);
        Log.Info($"{agent} idle-island rescue: no objective-connected point, teleported {Vector3.Distance(pos, bestDest):F0}m next to squadmate {best} instead (off the stuck chunk)");
        return true;
    }

    // ── Spawn-island rescue ────────────────────────────────────────────────────────────────────────────
    // One-shot teleport for a bot that SPAWNED on a disconnected navmesh chunk. The idle-island rescue can't
    // catch this: the bot still "arrives" at its few on-island waypoints, so it never reads as stranded-far.
    // Detect directly (parked near spawn past a grace window AND can't path to any other live agent, since the
    // rest sit on the main mesh), then teleport once onto the main mesh, clear of humans and bots.
    private void TrySpawnIslandRescue(Agent agent, List<Agent> liveAgents)
    {
        var stuck = agent.Stuck;
        if (stuck.SpawnIslandRescued) return; // one-shot; also set once the bot proves it can reach the map

        var pos = agent.Position;
        if (stuck.SpawnIslandSeenAt <= 0f)
        {
            stuck.SpawnIslandPos = pos;
            stuck.SpawnIslandSeenAt = Time.time;
            return;
        }

        // Moved away from spawn => not islanded.
        if ((pos - stuck.SpawnIslandPos).sqrMagnitude > SpawnIslandMoveRadiusSqr)
        {
            stuck.SpawnIslandRescued = true;
            return;
        }

        if (Time.time - stuck.SpawnIslandSeenAt < SpawnIslandGraceSeconds) return;

        // Find the nearest live agent we CANNOT reach; reaching ANY of them means we're on the main mesh.
        Agent reference = null;
        var bestDistSqr = float.MaxValue;
        for (var i = 0; i < liveAgents.Count; i++)
        {
            var other = liveAgents[i];
            if (other == null || other == agent) continue;
            // Skip same-squad agents: squadmates spawn on the same island, so one would falsely read as a
            // reachable reference or anchor the TP back onto the disconnected chunk.
            if (agent.Squad != null && other.Squad != null && other.Squad.Id == agent.Squad.Id) continue;
            if (other.Player?.HealthController is not { IsAlive: true }) continue;
            var d = (other.Position - pos).sqrMagnitude;
            if (d < SpawnIslandMinReferenceDistSqr) continue; // too close — might be on the same chunk
            if (IsPathComplete(pos, other.Position))
            {
                stuck.SpawnIslandRescued = true; // reachable => not islanded
                return;
            }
            if (d < bestDistSqr) { bestDistSqr = d; reference = other; }
        }

        if (reference == null) return;

        if (!TeleportSafe(agent, _humanPlayers)) return;
        // Anchor reachability on the alive human player (main mesh) if there is one, else the nearest agent.
        var anchorPos = reference.Position;
        for (var i = 0; i < _humanPlayers.Count; i++)
        {
            var p = _humanPlayers[i];
            if (p?.HealthController is { IsAlive: true }) { anchorPos = p.Position; break; }
        }
        if (TryFindReachableWaypoint(agent, liveAgents, agent.Position, anchorPos, SpawnIslandWaypointSearchRadius, out var wpDest))
        {
            var fromPos = agent.Position;
            agent.Player.Teleport(wpDest);
            ResetPath(agent);
            agent.Stuck.SpawnIslandRescued = true;
            Log.Info($"{agent} spawn-island rescue: teleported {Vector3.Distance(fromPos, wpDest):F0}m to the nearest reachable waypoint (off the disconnected spawn chunk)");
            return;
        }
    }

    // Nearest waypoint to fromPos that is reachable from anchorPos and clear of players/bots, so the bot lands
    // on a real POI rather than next to another bot.
    private bool TryFindReachableWaypoint(Agent agent, List<Agent> liveAgents, Vector3 fromPos, Vector3 anchorPos, float maxRadius, out Vector3 dest)
    {
        dest = Vector3.zero;
        if (_waypointSystem == null) return false;

        var cells = _waypointSystem.Cells;
        var w = cells.GetLength(0);
        var h = cells.GetLength(1);
        var center = _waypointSystem.WorldToCell(fromPos);
        var range = Mathf.CeilToInt(maxRadius / _waypointSystem.CellSize);
        var maxRadSqr = maxRadius * maxRadius;
        _wpScratch.Clear();
        for (var dx = -range; dx <= range; dx++)
        for (var dy = -range; dy <= range; dy++)
        {
            var cx = center.x + dx;
            var cy = center.y + dy;
            if (cx < 0 || cy < 0 || cx >= w || cy >= h) continue;
            var wps = cells[cx, cy].Waypoints;
            for (var k = 0; k < wps.Count; k++)
            {
                var wp = wps[k];
                if ((wp.Position - fromPos).sqrMagnitude <= maxRadSqr) _wpScratch.Add(wp);
            }
        }
        _wpScratch.Sort((x, y) => (x.Position - fromPos).sqrMagnitude.CompareTo((y.Position - fromPos).sqrMagnitude));

        var cap = Mathf.Min(_wpScratch.Count, 40); // bound the CalculatePath calls
        for (var i = 0; i < cap; i++)
        {
            var wp = _wpScratch[i];
            if (!_waypointSystem.IsReachableFromPosition(anchorPos, wp.Position)) continue;
            if (!NavMesh.SamplePosition(wp.Position, out var hit, 2f, NavMesh.AllAreas)) continue;
            if (!IsClearOfPlayersAndBots(hit.position, agent, liveAgents)) continue;
            dest = hit.position;
            dest.y += 0.25f;
            return true;
        }
        return false;
    }

    private bool IsPathComplete(Vector3 from, Vector3 to)
        => NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _rescuePath)
           && _rescuePath.status == NavMeshPathStatus.PathComplete;

    private bool IsClearOfPlayersAndBots(Vector3 dest, Agent self, List<Agent> liveAgents)
    {
        for (var i = 0; i < _humanPlayers.Count; i++)
        {
            var p = _humanPlayers[i];
            if (p?.HealthController is { IsAlive: true } && (p.Position - dest).sqrMagnitude < SpawnIslandBotSafetyRadiusSqr)
                return false;
        }
        for (var i = 0; i < liveAgents.Count; i++)
        {
            var other = liveAgents[i];
            if (other == null || other == self) continue;
            if (other.Player?.HealthController is { IsAlive: true } && (other.Position - dest).sqrMagnitude < SpawnIslandBotSafetyRadiusSqr)
                return false;
        }
        return true;
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
            // over-estimate expected distance during a slowdown).
            //   - currentSpeed > lastSpeed: EWMA with alpha=0.9 (gives
            // the agent a frame or two to actually build distance).
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

        public void Update(Agent agent)
        {
            // If the bot stays within a radius of its position 5 s ago for extended periods of time, treat as
            // stuck. Radius is modulated by the bot's target velocity (deliberate slow movement shouldn't
            // false-positive).
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

            // If the bot moved more than the radius × moveSpeed from its oldest position, treat as not-stuck
            // and reset.
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
                    // Swing-arc geometry is a common cause of HardStuck indoors; close any doors this bot opened
                    // before re-pathing. includeNear because a pinned bot is usually wedged on the door next to it.
                    movementSystem.CloseRecentDoorsBehindAgent(agent, includeNear: true);
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
            if (!TeleportSafe(agent, humanPlayers)) return;

            // Escalate on re-stick: teleporting again from ~the same spot means the bot re-wedged, so jump to a
            // farther rescue ring instead of dropping it a few metres away to re-stick.
            var stuck = agent.Stuck.Hard;
            var pos = agent.Position;
            const float reStuckRadiusSqr = 10f * 10f;
            stuck.TeleportCount = stuck.TeleportCount > 0 && (pos - stuck.LastTeleportPos).sqrMagnitude < reStuckRadiusSqr
                ? stuck.TeleportCount + 1
                : 1;
            stuck.LastTeleportPos = pos;
            var startRing = stuck.TeleportCount <= 1 ? 0 : stuck.TeleportCount == 2 ? 2 : 3; // rings {4,8,16,28,45}

            // Prefer an unsticking teleport (objective-connected point, else a squadmate); path-corner is last resort.
            var objLoc = agent.Objective?.Location;
            if (objLoc != null && movementSystem.RescueTeleportToConnectedPoint(agent, objLoc.Position, startRing)) return;
            if (movementSystem.RescueTeleportNearSquadmate(agent)) return;

            var teleportPos = agent.Movement.Path[agent.Movement.CurrentCorner];
            teleportPos.y += 0.25f;
            agent.Player.Teleport(teleportPos);
            Log.Debug($"{agent} teleporting to {teleportPos} (path-corner fallback)");
        }
    }
}
