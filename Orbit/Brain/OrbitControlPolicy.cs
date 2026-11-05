namespace Orbit.Brain;

internal static class OrbitControlPolicy
{
    // A wound / available medical item is a need, not an operation in progress.
    // Keep the post-combat grace period, but never time out a real treatment.
    internal static bool CanTakeControl(bool dormant, bool sainCombat, bool underFire,
        float secondsSinceCombat, bool medicineUsing, bool medicalHands)
        => dormant || !(sainCombat || underFire || secondsSinceCombat < 15f
            || medicineUsing || medicalHands);
}
