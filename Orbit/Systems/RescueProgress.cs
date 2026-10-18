using UnityEngine;

namespace Orbit.Systems;

// A supported landing is provisional. Only continuous travel on a usable objective route
// clears this history; teleports, task changes and native handoffs cannot reset its backoff.
internal sealed class RescueProgress
{
    internal bool Pending { get; private set; }
    internal int Failures { get; private set; }
    internal float RetryAt { get; private set; }
    internal bool Tracking => Pending || Failures > 0;
    internal bool CanAttempt(float now) => !Pending && now >= RetryAt;
    internal float NextRouteCheck;
    private float _lastAt = -1f, _stalled, _moving;
    private Vector3 _last, _start, _target;
    private int _objective = -1;
    private bool _observed, _walking;
    internal float Distance => Vector3.Distance(_start, _last);
    internal Vector3 LastPosition => _last;
    internal bool Ready => _walking && _moving >= 1f && Distance >= 3f;

    internal void Begin(Vector3 landing, float now)
    {
        Pending = true;
        _stalled = 0f;
        _last = _start = landing;
        _lastAt = now;
        _observed = _walking = false;
        _moving = 0f;
        NextRouteCheck = now;
    }

    internal void Observe(Vector3 position, float now, bool observe, bool walking,
        int objective, Vector3 target)
    {
        if (!Tracking) return;
        var elapsed = now - _lastAt;
        var step = Vector3.Distance(position, _last);
        var continuous = observe && _observed && elapsed > 0f && elapsed <= 1f;
        if (continuous && Pending) _stalled += elapsed;
        // Reject discontinuities even when another system moved the body. Do not treat a
        // long inactive interval, a fall or an objective switch as walking off the landing.
        if (!continuous || !walking || !_walking || objective != _objective
            || (target - _target).sqrMagnitude > 2.25f || step > elapsed * 12f + .1f)
        {
            _start = position;
            _moving = 0f;
        }
        else if (step > .001f) _moving += elapsed;
        _last = position;
        _lastAt = now;
        _observed = observe;
        _walking = observe && walking;
        _objective = objective;
        _target = target;
    }

    internal bool Expired => Pending && _stalled >= 30f;

    internal float Fail(float now)
    {
        Pending = false;
        Failures++;
        var delay = Failures == 1 ? 30f : Failures == 2 ? 60f : 120f;
        RetryAt = now + delay;
        return delay;
    }

    internal void Confirm()
    {
        Pending = false;
        Failures = 0;
        RetryAt = 0f;
        _observed = _walking = false;
    }
}
