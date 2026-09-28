using System;
using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Fika;

internal static class DoorStateReceiver
{
    // A terminal snapshot must also stop an old swing coroutine. SyncInteractState alone returns
    // early when the logical state already matches, leaving that coroutine and a wrong angle alive.
    private static readonly FieldInfo Interaction = AccessTools.Field(typeof(WorldInteractiveObject), "_interaction");
    private static readonly PropertyInfo Break = Interaction?.FieldType.GetProperty("Break");
    private static readonly PropertyInfo Result = Interaction?.FieldType.GetProperty("ResultState");

    internal static bool Supported => Interaction != null && Break?.CanWrite == true && Result?.CanWrite == true;

    internal static bool Terminal(EDoorState state)
        => state is EDoorState.Open or EDoorState.Shut or EDoorState.Locked;

    internal static bool Valid(OrbitDoorPacket packet)
        => Terminal((EDoorState)packet.State) && !float.IsNaN(packet.Angle)
            && !float.IsInfinity(packet.Angle) && Mathf.Abs(packet.Angle) <= 3600f;

    internal static bool Apply(Door door, OrbitDoorPacket packet)
    {
        if (!Supported || !Valid(packet) || door == null || door.ForceLocalInteraction) return false;
        var state = (EDoorState)packet.State;
        var interaction = Interaction.GetValue(door);
        if (interaction == null) return false;
        // Door-owned unlock and handle coroutines can write Shut after a later Open snapshot.
        // Cancel only this interactive component's routines, not the bot or other scene components.
        door.StopAllCoroutines();
        Break.SetValue(interaction, true);
        Result.SetValue(interaction, state);
        if (door.DoorState == state && Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, packet.Angle)) < 0.01f
            && door.IsBroken == packet.Broken) return true;
        // Use the same public restoration entry point as Fika's reconnect snapshots.
        // It preserves real broken-door visuals, without generating a kick or a second open sound.
        door.SetInitialSyncState(new WorldInteractiveObject.InteractiveObjectStatusInfo
        {
            State = packet.State,
            IsBroken = packet.Broken,
        });
        door.CurrentAngle = packet.Angle;
        EFT.GlobalEvents.GlobalEventsController.CreateEvent<EFT.GlobalEvents.InteractiveObjectInteractionResultEvent>()
            .Invoke(door, state);
        return true;
    }
}
