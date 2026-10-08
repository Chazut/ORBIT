using UnityEngine;

namespace Orbit.Systems;

// Observe real path failures across successive objectives. Never invalidate a route from a local probe.
internal sealed class MovementFailureHistory
{
    private Vector3 _origin;
    private float _lastFailure = -1f;
    private int _failures;

    internal void Reset() { _failures = 0; _lastFailure = -1f; }

    internal void FailedRoute(Vector3 position)
    {
        if (_lastFailure < 0f || Time.time - _lastFailure > 10f
            || (position - _origin).sqrMagnitude > 9f)
        { _failures = 0; _origin = position; }
        _lastFailure = Time.time;
        _failures++;
    }

    internal bool RepeatedFailures(Vector3 position)
        => _failures >= 3 && Time.time - _lastFailure <= 10f
           && (position - _origin).sqrMagnitude <= 9f;
}
