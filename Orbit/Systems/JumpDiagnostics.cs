using Orbit.Entities;
using UnityEngine;

namespace Orbit.Systems;

// Observe requests and native states without changing movement, steering or sprint.
internal sealed class JumpDiagnostics
{
    private float _requested = -1f, _started = -1f, _nextReport;
    private int _revision, _suppressed;
    private bool _report, _orbit;
    private Vector3 _origin, _target;

    internal void Request(Agent agent)
    {
        End(agent, "new-request");
        Begin(agent, true);
        if (_report) Log.Always($"MOVEMENT JUMP: {agent} phase=request source=orbit-soft-stuck mode=observe "
            + $"state={agent.Player.CurrentManagedState?.Name} grounded={agent.Player.MovementContext?.IsGrounded == true} "
            + $"position={agent.Position} target={_target} revision={_revision} objective={agent.Objective.Location}");
    }

    private void Begin(Agent agent, bool orbit)
    {
        _requested = Time.time; _started = -1f; _orbit = orbit;
        _origin = agent.Position; _target = agent.Movement.Target;
        _revision = agent.Movement.PathRevision;
        _report = Time.time >= _nextReport;
        if (_report)
        {
            _nextReport = Time.time + 10f;
            if (_suppressed > 0) Log.Always($"MOVEMENT JUMP: {agent} phase=suppressed episodes={_suppressed}");
            _suppressed = 0;
        }
        else _suppressed++;
    }

    internal void Observe(Agent agent, EPlayerState state)
    {
        if (_requested < 0f)
        {
            if (state != EPlayerState.Jump) return;
            Begin(agent, false);
        }
        if (state == EPlayerState.Jump && _started < 0f)
        {
            // A late native jump cannot be attributed to an old rejected request.
            if (Time.time - _requested > .5f) _orbit = false;
            _started = Time.time;
            if (_report) Log.Always($"MOVEMENT JUMP: {agent} phase=begin source={(_orbit ? "orbit-soft-stuck" : "unattributed")} "
                + $"mode=observe state={state} position={agent.Position} revision={agent.Movement.PathRevision} objective={agent.Objective.Location}");
        }
        if (Time.time < _requested || Time.time - _requested > 2.5f)
            End(agent, "expired");
        else if (_started >= 0f && state is not (EPlayerState.Jump or EPlayerState.JumpLanding))
            End(agent, "state-" + state);
        else if (_started < 0f && Time.time - _requested > .5f)
            End(agent, "not-started");
    }

    internal void End(Agent agent, string reason)
    {
        if (_requested >= 0f && _report)
            Log.Always($"MOVEMENT JUMP: {agent} phase=end source={(_orbit ? "orbit-soft-stuck" : "unattributed")} reason={reason} "
                + $"observed={_started >= 0f} seconds={Time.time - _requested:F3} mode=observe "
                + $"distance={Vector3.Distance(_origin, agent.Position):F2} position={agent.Position} revision={agent.Movement.PathRevision}");
        _requested = _started = -1f; _report = false;
    }
}
