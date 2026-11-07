using UnityEngine;

namespace Orbit.Systems;

// Counts actual attempted travel, across path changes and short jump/door pauses.
// Vertical bouncing alone is not progress. Intentional waits never add time.
internal sealed class TravelProgressWatch
{
    private Vector3 _anchor;
    private float _last = -1f, _cooldown;
    private int _stage, _assists;
    internal float Seconds { get; private set; }
    internal int Assists => _assists;
    internal void Reset() { _last = -1f; Seconds = 0f; _stage = _assists = 0; _cooldown = 0f; }
    internal int Observe(Vector3 position, float now)
    {
        var offset = position - _anchor;
        if (_last < 0f || now < _last || now - _last > 3f
            || offset.x * offset.x + offset.z * offset.z > 4f || Mathf.Abs(offset.y) > 2f)
        { Reset(); _anchor = position; _last = now; return 0; }
        var dt = now - _last; _last = now;
        // Paused physics frames and gaps are not extra attempted movement.
        if (dt <= .5f) Seconds += dt;
        if (now < _cooldown) return 0;
        if (_stage == 0 && Seconds >= 6f) { _stage = 1; return 1; }
        if (_stage == 1 && Seconds >= 12f)
        { _stage = 0; Seconds = 0f; _cooldown = now + 10f; return 2; }
        return 0;
    }
    internal bool TryAssist() { if (_assists >= 4) return false; _assists++; return true; }
}
