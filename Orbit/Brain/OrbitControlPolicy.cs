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

// Native danger layers may briefly return false between two avoidance actions.
// A quiet window prevents ORBIT from restarting the interrupted loot path in
// every gap. The native layer remains free to act throughout this window.
internal sealed class NativeDangerGrace
{
    private float last = float.NegativeInfinity;
    internal void Observe(float now) { last = now; }
    internal bool Blocks(float now) => now >= last && now - last < 3f;
}
