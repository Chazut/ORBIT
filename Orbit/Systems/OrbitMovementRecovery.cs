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
    private float _supportedSince = -1f;
    private Vector3 _probeOrigin;
    private int _failedProbes;
    private float _nextProbe;
    private readonly Vector3[] _rescuePoints = new Vector3[16];
    private readonly float[] _rescueTimes = new float[16];
    private int _rescueCount, _rescueIndex;
    internal bool HandoffPending { get; private set; }
    private float _nextHandoffProbe, _nextHandoffReport;

    internal void CancelHandoff() => HandoffPending = false;

    private static void InvalidateNativePosition(BotMover mover, Vector3 position)
    {
        // These coordinates are a current reference, not a certified NavMesh anchor. Invalidate
        // both link flags and timestamps so the first native tick cannot reuse the old spawn.
        mover._linkedToNavmeshInitially = false;
        mover._lastGoodCastPointTime = float.NegativeInfinity;
        mover._prevPosLinkedTime = float.NegativeInfinity;
        mover._lastGoodCastPoint = mover._prevSuccessLinkedFrom = mover._prevLinkPos = position;
        mover.PositionOnWayInner = mover._positionOnWayCasted = position;
        mover._prevOffsetGoodCasted = Vector3.zero;
        mover._prevOffsetGoodCastedTime = float.NegativeInfinity;
    }

    internal void ConfirmNativeLink(BotMover mover, EBotLinkResult result)
    {
        if (!HandoffPending || result is not (EBotLinkResult.complete or EBotLinkResult.extraConnect)) return;
        var position = mover._owner.GetPlayer.Position;
        if (!TrySample(position, out _) || !BotGroundPlacement.HasSupport(mover._owner.GetPlayer)) return;
        HandoffPending = false;
    }

    internal Vector3 HandoffFallback(BotMover mover)
    {
        var position = mover._owner.GetPlayer.Position;
        // FindBetterPosition normally searches 100m around historical anchors. Until the native
        // mover confirms a fresh link, only a supported local landing may replace the body position.
        if (Time.time >= _nextHandoffProbe)
        {
            _nextHandoffProbe = Time.time + 2f;
            if (BotLandingGuard.TryHandoffLanding(mover._owner, out var landing))
            {
                InvalidateNativePosition(mover, landing);
                Log.Debug($"MOVEMENT HANDOFF: {mover._owner.Profile.Nickname} local recovery from={position} to={landing}");
                return landing;
            }
            if (Time.time >= _nextHandoffReport)
            {
                _nextHandoffReport = Time.time + 10f;
                Log.Debug($"MOVEMENT HANDOFF: {mover._owner.Profile.Nickname} awaiting local support at={position}; distant fallback suppressed");
            }
        }
        if (Finite(position)) InvalidateNativePosition(mover, position);
        return position;
    }

    internal bool RecentlyRescuedAt(Vector3 point)
    {
        for (var i = 0; i < _rescueCount; i++)
            if (Time.time - _rescueTimes[i] < 120f && (point - _rescuePoints[i]).sqrMagnitude < 6f * 6f)
                return true;
        return false;
    }

    internal void RecordLocalRescue(Vector3 from, Vector3 to)
    {
        RememberRescuePoint(from);
        RememberRescuePoint(to);
        Recovered(to);
    }

    private void RememberRescuePoint(Vector3 point)
    {
        _rescuePoints[_rescueIndex] = point;
        _rescueTimes[_rescueIndex] = Time.time;
        _rescueIndex = (_rescueIndex + 1) % _rescuePoints.Length;
        if (_rescueCount < _rescuePoints.Length) _rescueCount++;
    }

    internal bool Observe(Vector3 position, BotMover mover, bool force = false)
    {
        if (mover == null || !force && Time.time < _nextCheck) return false;
        _nextCheck = Time.time + 0.25f;
        OnMesh = TrySample(position, out var point);
        if (!OnMesh)
        {
            _supportedSince = -1f;
            if (OffMeshSince < 0f) OffMeshSince = Time.time;
            return true;
        }
        OffMeshSince = -1f;
        // Grounded state and a physical support check must agree before this can become a return point.
        // A supported interval prevents the teleport frame from certifying its own destination.
        if (!BotGroundPlacement.HasSupport(mover._owner?.GetPlayer)
            || BotLandingGuard.IsRejected(mover._owner, point))
        { _supportedSince = -1f; return true; }
        if (_supportedSince < 0f) _supportedSince = Time.time;
        if (Time.time - _supportedSince < 0.5f) return true;
        HasAnchor = true;
        Anchor = point;
        _anchorAt = Time.time;
        SeedNativePosition(mover, point);
        return true;
    }

    internal bool TryPrepareHandoff(Vector3 position, BotMover mover)
    {
        // A current navigation point and a stable rescue anchor have different lifetimes.
        // Invalidate the rescue history as native movement takes control. Never teleport here:
        // SetPlayerToNavMesh can fall back to old anchors and a 100m search even with a local input.
        Suspend();
        HandoffPending = mover != null;
        _nextHandoffProbe = Time.time + 0.5f;
        _nextHandoffReport = 0f;
        if (mover != null && Finite(position)) InvalidateNativePosition(mover, position);
        if (mover == null || !TrySample(position, out var point)
            || !BotGroundPlacement.HasSupport(mover._owner?.GetPlayer)
            || BotLandingGuard.IsRejected(mover._owner, point)) return false;
        SeedNativePosition(mover, point);
        return true;
    }

    private static void SeedNativePosition(BotMover mover, Vector3 point)
    {
        // Only a successful, local NavMesh sample may advance the native recovery anchors.
        mover._lastGoodCastPoint = point;
        mover._prevSuccessLinkedFrom = point;
        mover._prevLinkPos = point;
        mover.PositionOnWayInner = point;
        mover._lastGoodCastPointTime = Time.time;
        mover._prevPosLinkedTime = Time.time;
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
        _supportedSince = -1f;
        HasAnchor = false;
    }

    internal void Suspend()
    {
        // Native combat or Ghost movement can relocate the body. Do not reuse the old anchor.
        HasAnchor = false;
        _supportedSince = -1f;
        OnMesh = false;
        OffMeshSince = -1f;
        _nextCheck = 0f;
    }
}
