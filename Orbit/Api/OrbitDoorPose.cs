using System.Reflection;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Api;

public static class OrbitDoorPose
{
    private static readonly FieldInfo InteractionField = typeof(WorldInteractiveObject).GetField("_interaction",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    internal static WorldInteractiveObject.InteractionState Interaction(Door door)
        => InteractionField?.GetValue(door) as WorldInteractiveObject.InteractionState;

    internal static float TargetAngle(Door door)
    {
        var swing = Interaction(door);
        // EFT also sets ResultState for forced transitions that never initialized a swing.
        if (swing != null && !swing.Break && swing.Duration > 0f
            && (swing.ResultState == EDoorState.Open || door.IsBroken && swing.ResultState == EDoorState.Breaching)
            && Mathf.Abs(Mathf.DeltaAngle(door.GetAngle(EDoorState.Shut), swing.ResultAngle)) > 0.01f)
            return swing.ResultAngle;

        return door.GetAngle(EDoorState.Open);
    }

    /// <summary>Angular distance to a valid open pose. A logically closed or moving door is never ready.</summary>
    public static float OpenPoseError(Door door)
    {
        if (door == null || door.DoorState != EDoorState.Open) return float.PositiveInfinity;
        float error = Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, TargetAngle(door)));
        if (!door.IsBroken) return error;
        float breachError = Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, door.GetAngle(EDoorState.Breaching)));
        return Mathf.Min(error, breachError);
    }
}
