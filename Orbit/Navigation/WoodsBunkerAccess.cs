using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Navigation;

internal static class WoodsBunkerAccess
{
    internal static bool Applies(Waypoint point) =>
        point?.Target is ExfiltrationPoint exit && ExfilArrival.RequiresOpenGate(exit);

    internal static bool Available(Waypoint point) =>
        point.ExfilGate != null && point.Target is ExfiltrationPoint { Status: EExfiltrationStatus.RegularMode };

    internal static bool Accepted(Squad squad, Agent solo) =>
        (solo == null ? squad.WoodsBunkerChoice : solo.SoloWoodsBunkerChoice) == true;

    internal static bool Choose(Squad squad, Agent solo, Waypoint point, bool? pmc)
    {
        if (pmc != true || !Available(point)) return false;
        ref var choice = ref (solo == null ? ref squad.WoodsBunkerChoice : ref solo.SoloWoodsBunkerChoice);
        if (choice.HasValue) return choice.Value;
        var chance = squad.Personality?.LockedDoorUnlockProba ?? Sain.PersonalityFallback.LockedDoorUnlockProba;
        choice = chance >= 1f || chance > 0f && UnityEngine.Random.value < chance;
        Log.Info($"WOODS BUNKER: {squad} solo={solo?.Id} choice={(choice.Value ? "accept" : "skip")} chance={chance:P0} gate={point.ExfilGate.DoorState}");
        return choice.Value;
    }

    // A closed gate must be reachable from the public side even while its NavMesh cut is active.
    // Once open, target the real volume even if its interior mesh could not be sampled at map load.
    internal static Vector3 Destination(Waypoint point)
    {
        if (Applies(point))
        {
            if (point.ExfilGate != null && point.ExfilGate.DoorState != EDoorState.Open)
            {
                if (point.ExfilGateApproach.HasValue) return point.ExfilGateApproach.Value;
                var hinge = point.ExfilGate.transform.position;
                if (point.ExfilGate.DoorState is not (EDoorState.Locked or EDoorState.Shut)) return hinge;
                var volume = ExfilArrival.BunkerVolume((ExfiltrationPoint)point.Target);
                var room = volume != null ? volume.bounds.center : point.ExfilInteriorPosition ?? point.Position;
                // EFT returns the caller's Y and chooses the handle side from the caller's XZ.
                // Use a probe outside the room at bunker height, even when the PMC is still on the surface.
                var outside = new Vector3(2f * hinge.x - room.x, point.ExfilInteriorPosition?.y ?? hinge.y,
                    2f * hinge.z - room.z);
                var standing = point.ExfilGate.GetInteractionPosition(outside);
                point.ExfilGateApproach = standing;
                return standing;
            }
            if (!point.ExfilInteriorPosition.HasValue && ExfilArrival.BunkerVolume((ExfiltrationPoint)point.Target) is { } roomVolume)
                return roomVolume.bounds.center;
        }
        return point.ExfilInteriorPosition ?? point.Position;
    }
}
