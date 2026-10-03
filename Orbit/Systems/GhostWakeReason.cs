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
    Spectator,
}

internal readonly struct GhostWakeReason
{
    public readonly GhostWakeCause Cause;
    public readonly string Message;
    public readonly string TriggerProfileId;
    public readonly string TriggerName;
    public readonly string MemberProfileId;
    public readonly float Distance;

    public GhostWakeReason(GhostWakeCause cause, string message, EFT.Player trigger = null,
        string memberProfileId = null, float distance = -1f)
    {
        Cause = cause;
        Message = message;
        TriggerProfileId = trigger?.ProfileId;
        TriggerName = trigger?.Profile?.Nickname;
        MemberProfileId = memberProfileId;
        Distance = distance;
    }

    // Brief visibility/encounter wakes only need transition stability. Damage, targeting,
    // deliberate real fights and native fallbacks retain time for the physical AI to act.
    // Expiry never bypasses the independent combat, health, hands or player-distance gates.
    public float CooldownSeconds => Cause is GhostWakeCause.HumanProximity or GhostWakeCause.ScopedView
        or GhostWakeCause.BotProximity or GhostWakeCause.Extraction or GhostWakeCause.GroupChanged ? 5f : 30f;
}
