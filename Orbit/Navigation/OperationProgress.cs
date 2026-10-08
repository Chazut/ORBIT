using UnityEngine;

namespace Orbit.Navigation;

// Keep the best progress across approach legs, including detours and failed searches.
internal sealed class OperationProgress
{
    private float _bestGap = float.MaxValue, _progressAt;
    private int _stalledLegs;
    internal void Reset() { _bestGap = float.MaxValue; _progressAt = 0; _stalledLegs = 0; }
    internal bool Observe(Vector3 position, Vector3 target, float activeElapsed)
    {
        var gap = Vector3.Distance(position, target);
        if (gap > _bestGap - 3f) return false;
        _bestGap = gap; _progressAt = activeElapsed; _stalledLegs = 0;
        return true;
    }
    internal void ReachedLeg() => _stalledLegs++;
    internal bool Stalled(float activeElapsed) => _stalledLegs >= 6
        || _stalledLegs >= 2 && activeElapsed - _progressAt >= 90f;
    internal string Diagnostics => $"bestGap={_bestGap:F2}m stalledLegs={_stalledLegs}";
}
