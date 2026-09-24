using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Per Agent, so neither recycled bot IDs nor subsequent raids inherit a recovery anchor.
internal sealed class OrbitMovementRecovery
{
    internal Vector3 Anchor { get; private set; }
    internal bool HasAnchor { get; private set; }
    internal bool OnMesh { get; private set; }
    internal float OffMeshSince { get; private set; } = -1f;
    private float _anchorAt, _nextCheck;
    private Vector3 _probeOrigin;
    private int _failedProbes;
    private float _nextProbe;

    internal bool Observe(Vector3 position, BotMover mover, bool force = false)
    {
        if (mover == null || !force && Time.time < _nextCheck) return false;
        _nextCheck = Time.time + 0.25f;
        OnMesh = TrySample(position, out var point);
        if (!OnMesh)
        {
            if (OffMeshSince < 0f) OffMeshSince = Time.time;
            return true;
        }
        OffMeshSince = -1f;
        HasAnchor = true;
        Anchor = point;
        _anchorAt = Time.time;
        // Only a successful, local NavMesh sample may advance the native recovery anchors.
        mover._lastGoodCastPoint = point;
        mover._prevSuccessLinkedFrom = point;
        mover._prevLinkPos = point;
        mover.PositionOnWayInner = point;
        mover._lastGoodCastPointTime = Time.time;
        mover._prevPosLinkedTime = Time.time;
        return true;
    }

    internal bool TryReturnPoint(out Vector3 point)
    {
        point = default;
        // Never send a bot back to a distant historical spawn, or undo a brief jump/vault.
        return !OnMesh && OffMeshSince >= 0f && Time.time - OffMeshSince >= 2f
            && HasAnchor && Time.time - _anchorAt <= 30f && TrySample(Anchor, out point);
    }

    internal static bool TrySample(Vector3 position, out Vector3 point)
    {
        point = default;
        if (!Finite(position) || !NavMesh.SamplePosition(position, out var hit, 0.75f, NavMesh.AllAreas)
            || !Finite(hit.position) || (hit.position - position).sqrMagnitude > 0.75f * 0.75f
            || Mathf.Abs(hit.position.y - position.y) > 0.5f) return false;
        point = hit.position;
        return true;
    }

    private static bool Finite(Vector3 point)
        => !float.IsNaN(point.x) && !float.IsInfinity(point.x)
            && !float.IsNaN(point.y) && !float.IsInfinity(point.y)
            && !float.IsNaN(point.z) && !float.IsInfinity(point.z);

    internal bool ProbeDue(Vector3 position)
        => (position - _probeOrigin).sqrMagnitude > 3f * 3f || Time.time >= _nextProbe;

    internal bool BeginProbe(Vector3 position)
    {
        if ((position - _probeOrigin).sqrMagnitude > 3f * 3f)
        {
            _probeOrigin = position;
            _failedProbes = 0;
            _nextProbe = 0f;
        }
        if (Time.time < _nextProbe) return false;
        // Reserve the slot before navigation runs, including callers sharing this frame.
        _nextProbe = Time.time + 5f;
        return true;
    }

    internal void ProbeFailed()
    {
        _failedProbes++;
        _nextProbe = Time.time + (_failedProbes == 1 ? 5f : _failedProbes == 2 ? 15f : 60f);
    }

    internal void Recovered(Vector3 position)
    {
        _probeOrigin = position;
        _failedProbes = 0;
        _nextProbe = Time.time + 5f;
        _nextCheck = 0f;
    }

    internal void Suspend()
    {
        // Native combat or Ghost movement can relocate the body. Do not reuse the old anchor.
        HasAnchor = false;
        OnMesh = false;
        OffMeshSince = -1f;
        _nextCheck = 0f;
    }
}
