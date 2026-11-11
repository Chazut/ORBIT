using System;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using UnityEngine;

namespace Orbit.Systems;

public partial class MovementSystem
{
    private bool HoldForDoorRecovery(Agent agent)
    {
        var movement = agent.Movement;
        var window = movement.DoorRecovery;
        if (!agent.IsActive || agent.IsDormant || agent.Player == null || agent.Bot == null || agent.Bot.IsDead)
        {
            window.Reset();
            return false;
        }
        var state = agent.Player.CurrentManagedState?.Name ?? EPlayerState.None;
        var reason = Time.time < movement.DoorInteractHoldUntil ? "opening-window"
            : state is EPlayerState.DoorInteraction or EPlayerState.BreachDoor ? "body-animation" : null;
        if (reason == null)
        {
            // Cached references, fresh door states. No per-frame scene scan.
            foreach (var door in movement.GhostDoors.Get(_doorSystem, agent.Position))
                if (door != null && door.DoorState == EDoorState.Interacting && OccupiesDoor(door, agent.Position))
                { reason = "nearby-animation"; break; }
        }
        var heldBefore = window.Held;
        var expiredBefore = window.Expired;
        var held = window.Observe(Time.time, reason != null);
        if (held && !heldBefore)
            Log.Debug($"DOOR RECOVERY: {agent} action=hold reason={reason} state={state} limit={DoorRecoveryWindow.MaximumSeconds:F1}s");
        else if (!held && !expiredBefore && window.Expired)
            Log.Info($"DOOR RECOVERY: {agent} action=timeout reason={reason} state={state} limit={DoorRecoveryWindow.MaximumSeconds:F1}s recovery=resumed");
        else if (!held && heldBefore)
            Log.Debug($"DOOR RECOVERY: {agent} action=resume reason=interaction-ended state={state}");
        if (!held) return false;

        // Every recovery family pauses, including the no-path/island watchdogs.
        // Seed fresh samples so the animation is not charged as lost travel afterwards.
        agent.Stuck.Soft.Reset();
        agent.Stuck.Soft.LastUpdate = Time.time;
        agent.Stuck.Soft.LastPosition = agent.Position;
        agent.Stuck.Soft.LastSpeed = 0f;
        agent.Stuck.Hard.Status = HardStuckStatus.None;
        agent.Stuck.Hard.Timer = 0f;
        agent.Stuck.Hard.LastUpdate = Time.time;
        agent.Stuck.Hard.PositionHistory.Reset();
        agent.Stuck.Hard.AverageSpeed.Reset();
        agent.Stuck.TravelProgress.Reset();
        agent.Stuck.IdleRescueSince = -1f;
        agent.Stuck.IdleRescueIntent = false;
        agent.Stuck.SpawnIslandDisconnectedSince = -1f;
        ObserveRescueProgress(agent, paused: true);
        HoldForDoorInteraction(agent);
        return true;
    }

    private static bool OccupiesDoor(Door door, Vector3 position)
    {
        if (Math.Abs(position.y - door.transform.position.y) > 1.75f) return false;
        var closest = door.Collider != null ? door.Collider.bounds.ClosestPoint(position + Vector3.up * .9f)
            : door.transform.position;
        return XzDistance(position, closest) < 2f;
    }

    private bool IsDoorOccupied(Agent requester, Door door, out string occupant)
    {
        if (OccupiesDoor(door, requester.Position)) { occupant = "self"; return true; }
        if (_recoveryAgents != null)
            foreach (var other in _recoveryAgents)
                if (other != requester && other.Player != null && other.Bot != null && !other.Bot.IsDead
                    && !other.IsDormant && OccupiesDoor(door, other.Position))
                { occupant = "other-bot"; return true; }
        foreach (var human in _humanPlayers)
            if (human?.HealthController is { IsAlive: true } && OccupiesDoor(door, human.Position))
            { occupant = "player"; return true; }
        occupant = null;
        return false;
    }
}
