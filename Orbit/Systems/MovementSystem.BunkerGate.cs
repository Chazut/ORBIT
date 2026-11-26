using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

public partial class MovementSystem
{
    // Called from the selected bunker's access step, after the bot's actual feet pass the reach check.
    internal void UseBunkerGate(Agent agent, Door door)
        => UseObjectiveGate(agent, door, "WOODS BUNKER");

    internal void UseObjectiveGate(Agent agent, Door door, string source)
    {
        if (door == null || !door.Operatable || door.InteractingPlayer != null) return;
        if (door.DoorState == EDoorState.Shut) { OpenObjectiveDoor(agent, door); return; }
        if (door.DoorState != EDoorState.Locked || agent.Bot.Profile?.Info?.Settings?.Role.IsPMC() != true) return;
        var doorId = door.GetInstanceID();
        if (_doorInteractCooldown.TryGetValue(doorId, out var last) && Time.time - last < DoorInteractCooldownSeconds) return;
        _doorInteractCooldown[doorId] = Time.time;
        try
        {
            door.Unlock();
            Orbit.Api.OrbitDoorEvents.Raise(door, Orbit.Api.OrbitDoorEvents.Operation.Unlock);
            Log.Info($"{source}: {agent} unlocking door={door.Id} ghost={agent.IsDormant} feet={agent.Position}");
        }
        catch (Exception error) { Log.Warning($"{source}: {agent} unlock failed door={door.Id}: {error.Message}"); }
    }
}
