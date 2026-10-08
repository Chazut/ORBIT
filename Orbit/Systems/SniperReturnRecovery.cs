using Orbit.Entities;
using UnityEngine;

namespace Orbit.Systems;

// Retain the connected approach point across main completion. Observe subsequent movement without
// owning its objective; only a stalled departure can use this one-shot return.
internal sealed class SniperReturnRecovery(Agent agent, MainObjective main, Vector3 anchor, Vector3 post) : IObjectiveWork
{
    private MovementSystem _movement;
    private WaypointSystem _waypoints;
    private Vector3 _observed;
    private float _lastObservation = -1, _idleSince = -1, _nextAttempt;
    internal const float Range = 25f, Height = 1f, StallSeconds = 12f;

    internal static bool Near(Vector3 from, Vector3 to)
        => Mathf.Abs(from.y - to.y) <= Height && (from - to).sqrMagnitude <= Range * Range;

    void IObjectiveWork.ResumeObjectiveWork() => Tick(_movement, _waypoints);

    internal bool Tick(MovementSystem movement, WaypointSystem waypoints)
    {
        _movement = movement; _waypoints = waypoints;
        if (agent.SniperReturn != this || agent.Bot == null || agent.Bot.IsDead)
        { Clear(); return false; }
        if (!main.Completed) { Suspend(); return false; }
        if (!Near(agent.Position, post) || !Near(agent.Position, anchor)
            || (agent.Position - anchor).sqrMagnitude <= 2.25f)
        { Clear(); return false; }
        var objective = agent.Objective;
        var leaving = objective.Location != null
            && objective.Status is ObjectiveStatus.None or ObjectiveStatus.Moving or ObjectiveStatus.Failed
            && (objective.Location.Position - agent.Position).sqrMagnitude > 9f;
        leaving |= agent.Squad?.ConsecutiveDispatchFailures > 0;
        if (!leaving || !MovementSystem.SniperRelocationAllowed(agent))
        { Suspend(); return false; }
        var now = Time.time;
        if (_lastObservation < 0 || now - _lastObservation > 2f || (agent.Position - _observed).sqrMagnitude > 1f)
        { _observed = agent.Position; _idleSince = now; }
        _lastObservation = now;
        if (now - _idleSince < StallSeconds || now < _nextAttempt || !waypoints.TryOperationWork(this)) return false;
        _nextAttempt = now + 2f;
        if (!movement.TryReturnFromSniperPost(agent, anchor)) return false;
        Clear(); return true;
    }

    private void Suspend()
    { _lastObservation = _idleSince = -1; _waypoints?.CancelOperationWork(this); }

    private void Clear()
    {
        if (agent.SniperReturn == this) agent.SniperReturn = null;
        Suspend();
    }
}
