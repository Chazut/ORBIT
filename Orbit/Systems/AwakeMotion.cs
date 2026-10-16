using Orbit.Entities;
using UnityEngine;

namespace Orbit.Systems;

// Keep only a very recent, grounded travel input. Native sprint consumes its input every
// frame; returning without Move can stop it just as surely as EnableSprint(false).
internal sealed class AwakeMotion
{
    private const float CoastSeconds = .15f;
    private float _inputAt = -1f;
    private int _revision;
    private Vector3 _position, _target;
    private bool _intent, _lastSprint;
    internal Vector3 Steering { get; private set; }

    private float _since = -1f, _nextGroundReport, _nextStopReport;
    private int _frames, _coasted, _groundReports, _stopReports;
    private bool _report, _dropped, _traversal;
    internal int GroundEpisodes { get; private set; }
    internal int NativeDrops { get; private set; }

    internal void Remember(Agent agent, Vector3 steering)
    {
        _inputAt = Time.time;
        _position = agent.Position;
        _target = agent.Movement.Target;
        _revision = agent.Movement.PathRevision;
        _intent = agent.Movement.Sprint;
        _lastSprint = agent.Player.Physical.Sprinting;
        Steering = steering;
    }

    internal void ClearInput() => _inputAt = -1f;

    internal bool CanCoast(Agent agent)
        => _inputAt >= 0f && Time.time >= _inputAt && Time.time - _inputAt <= CoastSeconds
           && agent.Movement.HasPath && agent.Movement.Status == MovementStatus.Moving
           && agent.Movement.PathRevision == _revision && agent.Movement.Sprint == _intent
           && (agent.Movement.Target - _target).sqrMagnitude < .0001f
           && (agent.Position - _position).sqrMagnitude < 1f
           && Time.time >= agent.Movement.DoorInteractHoldUntil;

    internal void Coast() => _coasted++;

    internal void Observe(Agent agent, bool held, bool traversal, EPlayerState state)
    {
        var physical = agent.Player.Physical.Sprinting;
        if (held && _since < 0f)
        {
            _since = Time.time;
            _frames = _coasted = 0;
            _dropped = _traversal = false;
            GroundEpisodes++;
            _report = (physical || agent.Movement.Sprint) && _groundReports < 4 && Time.time >= _nextGroundReport;
            if (_report)
            {
                _groundReports++;
                _nextGroundReport = Time.time + 10f;
                Log.Always($"SPRINT GROUND: {agent} phase=begin state={state} "
                    + $"grounded={agent.Player.MovementContext?.IsGrounded == true} physical={physical} "
                    + $"requested={agent.Movement.Sprint} traversal={traversal} objective={agent.Objective.Location}");
            }
        }
        if (_since >= 0f)
        {
            if (_lastSprint && !physical)
            {
                NativeDrops++;
                _dropped = true;
            }
            _traversal |= traversal;
            if (held) _frames++;
            else Finish(agent, "grounded", state);
        }
        _lastSprint = physical;
    }

    internal void Finish(Agent agent, string reason, EPlayerState state)
    {
        if (_since >= 0f && _report)
            Log.Always($"SPRINT GROUND: {agent} phase=end reason={reason} state={state} "
                + $"seconds={Time.time - _since:F3} frames={_frames} continuedFrames={_coasted} "
                + $"nativeDrop={_dropped} traversal={_traversal} physical={agent.Player?.Physical.Sprinting == true} "
                + $"episodes={GroundEpisodes} nativeDrops={NativeDrops}");
        _since = -1f;
        _report = false;
    }

    internal void TraceStop(Agent agent, string reason)
    {
        // A stop we requested is not a native interruption on the next observation.
        _lastSprint = false;
        if (_stopReports >= 6 || Time.time < _nextStopReport) return;
        _stopReports++;
        _nextStopReport = Time.time + 2f;
        Log.Always($"SPRINT STOP: {agent} reason={reason} state={agent.Player.CurrentManagedState?.Name ?? EPlayerState.None} "
            + $"grounded={agent.Player.MovementContext?.IsGrounded == true} requested={agent.Movement.Sprint} "
            + $"episodes={GroundEpisodes} nativeDrops={NativeDrops} objective={agent.Objective.Location}");
    }
}
