namespace Orbit.Systems;

internal enum SprintReason { Walking, Restricted, Approach, Turning, Settling, Sprinting, StutterRecovery }

// Native movement can reject a sprint or stop it during an animation transition. Do not
// reassert it every frame, or sprint into a short cover adjustment while still turning.
internal sealed class SprintControl
{
    private const float StartDistanceSqr = 8f * 8f;
    private const float StopDistanceSqr = 5f * 5f;
    private const float StartHeadingDot = .9063078f; // 25 degrees
    private const float StopHeadingDot = .6427876f; // 50 degrees
    private const float SettleSeconds = .35f;
    private float _eligibleSince = -1f;
    private bool _requested;
    private bool _observed, _lastPhysical;
    private float _windowUntil, _nextReport;
    private int _transitions, _reports;

    internal SprintReason Reason { get; private set; }
    internal bool ReportLoop { get; private set; }
    internal readonly SprintStutterGuard Stutter = new();

    internal void Suspend()
    {
        _eligibleSince = -1f;
        _requested = false;
        Stutter.Reset();
    }

    internal bool Evaluate(bool wantsSprint, bool allowed, bool physicalSprint,
        float destinationDistanceSqr, float headingDot, float now, bool detectStutter = true)
    {
        ReportLoop = false;
        if (!_observed || now > _windowUntil)
        {
            _windowUntil = now + 6f;
            _transitions = 0;
        }
        if (_observed && (physicalSprint != _lastPhysical || (_requested && !physicalSprint)) && ++_transitions >= 4
            && now >= _nextReport && _reports < 3)
        {
            ReportLoop = true;
            _nextReport = now + 15f;
            _reports++;
        }
        _observed = true;
        _lastPhysical = physicalSprint;

        if (!wantsSprint) return Stop(SprintReason.Walking);
        if (!allowed) return Stop(SprintReason.Restricted);
        if (destinationDistanceSqr < (physicalSprint ? StopDistanceSqr : StartDistanceSqr))
            return Stop(SprintReason.Approach);
        if (headingDot < (physicalSprint ? StopHeadingDot : StartHeadingDot))
            return Stop(SprintReason.Turning);
        if (Stutter.Update(detectStutter, physicalSprint, now))
            return Stop(SprintReason.StutterRecovery, resetStutter: false);
        if (physicalSprint)
        {
            _requested = true;
            Reason = SprintReason.Sprinting;
            return true;
        }

        // The engine dropped/refused our previous request. Require a fresh stable window.
        if (_requested || _eligibleSince < 0f) _eligibleSince = now;
        _requested = false;
        if (now - _eligibleSince < SettleSeconds)
        {
            Reason = SprintReason.Settling;
            return false;
        }
        _requested = true;
        Stutter.Requested();
        Reason = SprintReason.Sprinting;
        return true;
    }

    private bool Stop(SprintReason reason, bool resetStutter = true)
    {
        if (resetStutter) Stutter.Reset();
        _eligibleSince = -1f;
        _requested = false;
        Reason = reason;
        return false;
    }
}
