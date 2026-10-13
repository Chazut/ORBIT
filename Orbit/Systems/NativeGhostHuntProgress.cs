using UnityEngine;

namespace Orbit.Systems;

/// <summary>Bounds failed hunt travel across destination changes, without timing intentional waits.</summary>
internal sealed class NativeGhostHuntProgress(Vector3 origin)
{
    private const float TimeoutSeconds = 90f;
    private const float ProgressDistanceSqr = 9f;
    private Vector3 _origin = origin, _walked;
    private float _lastAt, _stalled;
    private bool _tracking, _blocked;

    internal float StalledSeconds => _stalled;

    internal static bool IsTravel(string decision)
        => decision is "MoreBotsAPI.Behavior.Actions.HuntTargetAction"
            or "MoreBotsAPI.Behavior.Actions.HuntRegroupAction";

    internal void Suspend() => _tracking = false;

    internal bool Update(bool travelling, bool routeFailed)
    {
        if (travelling)
        {
            if (_tracking) _stalled += Mathf.Clamp(Time.time - _lastAt, 0f, Time.deltaTime);
            _blocked |= routeFailed;
        }
        _lastAt = Time.time;
        _tracking = travelling;
        return travelling && _blocked && _stalled >= TimeoutSeconds;
    }

    internal void Walked(Vector3 from, Vector3 to)
    {
        if (!_tracking) return;
        _walked += to - from;
        // A local rescue offset is not walking. Require both a real walk and departure from
        // the obstruction, so repeated offsets followed by walking back cannot renew the budget.
        if (_walked.sqrMagnitude <= ProgressDistanceSqr || (to - _origin).sqrMagnitude <= ProgressDistanceSqr) return;
        _origin = to;
        _walked = default;
        _stalled = 0f;
        _blocked = false;
    }
}
