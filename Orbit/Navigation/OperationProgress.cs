using UnityEngine;

namespace Orbit.Navigation;

// Keep the best progress across approach legs, including detours and failed searches.
internal sealed class OperationProgress
{
    private float _bestGap = float.MaxValue, _progressAt;
    private int _stalledLegs;
    private float _bestHeight = float.MaxValue, _legRemaining, _legProgressAt;
    private Vector3[] _leg;
    internal void Reset()
    { _bestGap = _bestHeight = float.MaxValue; _progressAt = 0; _stalledLegs = 0; _leg = null; }
    internal void BeginLeg(Vector3[] corners, float activeElapsed)
    {
        _leg = corners;
        _legRemaining = float.MaxValue;
        _legProgressAt = activeElapsed - 7f;
    }
    internal void ObserveLeg(Vector3 position, float activeElapsed)
    {
        if (_leg == null || _leg.Length < 2) return;
        var remaining = 0f;
        var nearest = float.MaxValue;
        var bestRemaining = float.MaxValue;
        for (var i = _leg.Length - 1; i > 0; i--)
        {
            var segment = _leg[i] - _leg[i - 1];
            var length = segment.magnitude;
            var offset = position - _leg[i - 1];
            var t = length < .001f ? 0 : Mathf.Clamp((offset.x * segment.x + offset.y * segment.y + offset.z * segment.z)
                / (length * length), 0f, 1f);
            var gap = (position - (_leg[i - 1] + segment * t)).sqrMagnitude;
            if (gap < nearest) { nearest = gap; bestRemaining = remaining + (1f - t) * length; }
            remaining += length;
        }
        if (nearest > 64f) return;
        if (_legRemaining == float.MaxValue) { _legRemaining = bestRemaining; return; }
        if (bestRemaining <= _legRemaining - 2f)
        { _legRemaining = bestRemaining; _legProgressAt = activeElapsed; }
    }
    internal bool AdvancingAlongLeg(float activeElapsed) => _leg != null && activeElapsed - _legProgressAt < 10f;
    internal bool Observe(Vector3 position, Vector3 target, float activeElapsed)
    {
        var gap = Vector3.Distance(position, target);
        var height = Mathf.Abs(position.y - target.y);
        var climbed = height < _bestHeight - 1f;
        if (gap > _bestGap - 3f && !climbed) return false;
        _bestGap = Mathf.Min(_bestGap, gap); _bestHeight = Mathf.Min(_bestHeight, height);
        _progressAt = activeElapsed; _stalledLegs = 0;
        return true;
    }
    internal void ReachedLeg() { _stalledLegs++; _leg = null; }
    internal bool Stalled(float activeElapsed) => _stalledLegs >= 6
        || _stalledLegs >= 2 && activeElapsed - _progressAt >= 90f;
    internal string Diagnostics => $"bestGap={_bestGap:F2}m stalledLegs={_stalledLegs}";
}
