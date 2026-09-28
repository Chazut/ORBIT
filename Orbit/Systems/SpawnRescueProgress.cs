using UnityEngine;

namespace Orbit.Systems;

// Track newly reached ground, rather than distance from spawn or distance walked in circles.
internal sealed class SpawnRescueProgress
{
    private readonly Vector3[] _visited = new Vector3[64];
    private int _count;
    private float _nextSample;
    internal float LastProgressAt { get; private set; }

    internal bool Observe(Vector3 position, float now)
    {
        if (_count > 0 && now < _nextSample) return false;
        _nextSample = now + 0.25f;
        for (var i = 0; i < _count; i++)
        {
            var delta = position - _visited[i];
            if (delta.x * delta.x + delta.z * delta.z < 4f && Mathf.Abs(delta.y) < 0.75f) return false;
        }
        if (_count == _visited.Length) return false;
        _visited[_count++] = position;
        LastProgressAt = now;
        return true;
    }

    internal bool Stalled(float now) => _count > 0 && now - LastProgressAt >= 10f;
}
