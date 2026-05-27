using System.Runtime.CompilerServices;
using UnityEngine;

namespace Orbit.Helpers;

/// <summary>
/// Circular buffer recording recent world positions. <see cref="GetDistanceSqr"/>
/// returns the squared distance between the oldest and most-recent samples,
/// projected to the full window when the buffer is still warming up — used
/// by the stuck-detection heuristics to decide whether a bot has actually
/// moved over the recent past.
/// </summary>
public class PositionHistory
{
    private readonly int _bufferSize;
    private readonly Vector3[] _positions;
    private int _writeIndex;
    private int _validCount;

    public PositionHistory(int segments)
    {
        // +1 because covering N segments requires N+1 samples.
        _bufferSize = segments + 1;
        _positions = new Vector3[_bufferSize];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Vector3 currentPosition)
    {
        _positions[_writeIndex] = currentPosition;
        _writeIndex = (_writeIndex + 1) % _bufferSize;
        if (_validCount < _bufferSize)
            _validCount++;
    }

    public float GetDistanceSqr()
    {
        if (_validCount < 2) return 0f;

        var mostRecentIndex = (_writeIndex - 1 + _bufferSize) % _bufferSize;
        var mostRecentPosition = _positions[mostRecentIndex];

        var oldestIndex = _validCount < _bufferSize ? 0 : _writeIndex;
        var oldestPosition = _positions[oldestIndex];

        var observedDistSqr = (mostRecentPosition - oldestPosition).sqrMagnitude;

        if (_validCount >= _bufferSize) return observedDistSqr;

        // Warmup: project the partial window velocity to the full buffer length
        // so the value is comparable across all buffer fill states.
        var scaleFactor = (_bufferSize - 1f) / (_validCount - 1);
        return observedDistSqr * scaleFactor * scaleFactor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _writeIndex = 0;
        _validCount = 0;
    }
}
