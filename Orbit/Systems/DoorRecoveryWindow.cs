namespace Orbit.Systems;

// A continuous interaction can delay recovery once, never renew its own lease.
internal sealed class DoorRecoveryWindow
{
    internal const float MaximumSeconds = 6f;
    private float _started = -1f, _clearSince = -1f;
    internal bool Held { get; private set; }
    internal bool Expired { get; private set; }

    internal bool Observe(float now, bool requested)
    {
        Held = false;
        if (!requested)
        {
            if (_clearSince < 0f) _clearSince = now;
            if (now - _clearSince >= .5f) Reset();
            return false;
        }
        _clearSince = -1f;
        if (_started < 0f) _started = now;
        Expired = now - _started >= MaximumSeconds;
        return Held = !Expired;
    }

    internal void Reset() { _started = _clearSince = -1f; Held = Expired = false; }
}
