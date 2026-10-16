namespace Orbit.Systems;

// Count confirmed, short native sprints after our own requests. Ordinary stops,
// traversal and ownership changes reset this history before it can affect movement.
internal sealed class SprintStutterGuard
{
    internal const int BurstLimit = 3;
    internal const float WindowSeconds = 6f;
    internal const float ShortSprintSeconds = 1f;
    internal const float WalkSeconds = 2f;
    private float _lastSample = -1f, _runSince = -1f, _windowSince = -1f, _walkUntil = -1f;
    private int _bursts;
    private bool _pendingStart, _canTrack;

    internal bool Triggered { get; private set; }
    internal int Activations { get; private set; }
    internal float LastSprintSeconds { get; private set; }

    internal void Reset()
    {
        _lastSample = _runSince = _windowSince = _walkUntil = -1f;
        _bursts = 0;
        _pendingStart = _canTrack = Triggered = false;
    }

    internal void Requested()
    {
        if (_canTrack) _pendingStart = true;
    }

    internal bool Update(bool eligible, bool physicalSprint, float now)
    {
        Triggered = false;
        if (!eligible)
        {
            Reset();
            return false;
        }
        // Missing observations cannot establish the duration or cause of a short sprint.
        if (_lastSample >= 0f && (now < _lastSample || now - _lastSample > .5f)) Reset();
        _lastSample = now;
        _canTrack = true;
        if (now < _walkUntil) return true;

        if (_windowSince >= 0f && now - _windowSince > WindowSeconds)
        {
            _windowSince = -1f;
            _bursts = 0;
        }
        if (physicalSprint)
        {
            if (_pendingStart)
            {
                _runSince = now;
                _pendingStart = false;
            }
            if (_runSince >= 0f && now - _runSince > ShortSprintSeconds)
            {
                // A sustained sprint breaks the sequence of interrupted bursts.
                _runSince = _windowSince = -1f;
                _bursts = 0;
            }
        }
        else if (_runSince >= 0f)
        {
            LastSprintSeconds = now - _runSince;
            _runSince = -1f;
            if (LastSprintSeconds > ShortSprintSeconds)
            {
                _bursts = 0;
                _windowSince = -1f;
                return false;
            }
            if (_bursts == 0) _windowSince = now;
            if (++_bursts >= BurstLimit)
            {
                _walkUntil = now + WalkSeconds;
                _bursts = 0;
                _windowSince = -1f;
                _pendingStart = _canTrack = false;
                Triggered = true;
                Activations++;
                return true;
            }
        }
        return false;
    }
}
