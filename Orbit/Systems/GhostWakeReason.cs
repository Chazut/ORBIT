namespace Orbit.Systems;

internal enum GhostWakeCause
{
    NativeFallback,
    HumanProximity,
    ScopedView,
    BotProximity,
    Extraction,
    GroupChanged,
    Damage,
    Targeted,
    RealFight,
}

internal readonly struct GhostWakeReason
{
    public readonly GhostWakeCause Cause;
    public readonly string Message;

    public GhostWakeReason(GhostWakeCause cause, string message)
    {
        Cause = cause;
        Message = message;
    }

    // Brief visibility/encounter wakes only need transition stability. Damage, targeting,
    // deliberate real fights and native fallbacks retain time for the physical AI to act.
    // Expiry never bypasses the independent combat, health, hands or player-distance gates.
    public float CooldownSeconds => Cause is GhostWakeCause.HumanProximity or GhostWakeCause.ScopedView
        or GhostWakeCause.BotProximity or GhostWakeCause.Extraction or GhostWakeCause.GroupChanged ? 5f : 30f;
}
