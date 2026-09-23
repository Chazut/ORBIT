using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

/// <summary>Repairs execution of a native order without choosing a new destination or behaviour.</summary>
internal sealed class NativeGhostNavigation(BotOwner bot, DoorSystem doors)
{
    private const float RetryInterval = 2f;
    private const float StuckSeconds = 12f;
    private const float MaxRelocation = 8f;
    private const float ProgressDistance = 3f;
    private const int EdgeProbeCount = 15;
    private const int MaxLandings = 32 + EdgeProbeCount;
    private static readonly float[] RescueRings = { 1f, 2f, 4f, MaxRelocation };
    private static readonly float[] EdgeForward = { 0.2f, 0.5f, 1f };
    private static readonly float[] EdgeSideways = { 0f, -0.15f, 0.15f, -0.35f, 0.35f };
    private static int _budgetFrame = -1, _queries;
    private readonly NavMeshPath _path = new();
    private Vector3? _target;
    private float _reach = 0.5f, _retryAt, _progressAt, _rescueAt, _reportAt, _repathReportAt, _blockedReportAt;
    private Vector3 _progressPosition, _walkedDelta;
    private Vector3? _recoveryOrigin;
    private readonly List<Vector3> _triedLandings = new();
    private readonly List<Vector3> _reachedFrontiers = new();
    private readonly Dictionary<string, int> _rejections = new();
    private Vector3? _partialEnd;
    private object _adjustedPath;
    private float _originalPathReach;
    private int _failures, _probe, _invalidPaths;
    private float _invalidSince;
    private NavMeshPathStatus _status = NavMeshPathStatus.PathInvalid;
    private string _source;
    private float _retainedReportAt;
    private Vector3? _blockedFrom, _blockedCorner;
    private int _repeatedSegment;
    private int _edgeProbe = EdgeProbeCount;
    private Vector3 _edgeOrigin, _edgeDirection;
    private int RetryCount => Math.Max(_repeatedSegment, _invalidPaths);
    private float RetryDelay => RetryCount >= 10 ? 30f : RetryCount >= 6 ? 10f
        : RetryCount >= 3 ? 5f : RetryInterval;
    internal bool HasOrder => _target.HasValue;
    internal Vector3? Target => _target;
    internal float StalledFor => HasOrder ? Time.time - _progressAt : 0f;
    internal bool RecoveringLocally => _recoveryOrigin.HasValue;
    internal bool PersistentlyInvalid => HasOrder && _status == NavMeshPathStatus.PathInvalid
        && _invalidPaths >= 3 && StalledFor >= 45f && Time.time - _invalidSince >= 45f;

    internal bool ConfirmInvalidPath()
    {
        if (!PersistentlyInvalid || !TakeQuery()) return false;
        if (!NavMesh.CalculatePath(bot.Position, _target.Value, NavMesh.AllAreas, _path)
            || _path.status == NavMeshPathStatus.PathInvalid) return true;
        // Geometry changed during the backoff. Let the next movement update resume this
        // original route, instead of rechecking it on every scheduled brain tick.
        _invalidPaths = _failures = 0;
        _status = _path.status;
        _retryAt = 0f;
        return false;
    }

    internal void SetReachDistance(float reach)
    {
        if (!float.IsNaN(reach) && !float.IsInfinity(reach) && reach >= 0f)
        {
            _reach = Mathf.Max(0.1f, reach);
            _originalPathReach = reach;
        }
    }

    internal string Summary => _target.HasValue
        ? $"order={_target.Value} remaining={Vector3.Distance(bot.Position, _target.Value):F1}m nav={_status} retries={_failures} source={_source} reach={_reach:F2}m stalled={StalledFor:F1}s rescues={_triedLandings.Count}"
        : "order=none";

    internal static void ResetBudget() { _budgetFrame = -1; _queries = 0; }

    internal static bool TakeQuery()
    {
        if (_budgetFrame != Time.frameCount) { _budgetFrame = Time.frameCount; _queries = 0; }
        if (_queries >= 4) return false;
        _queries++;
        return true;
    }

    internal void Cancel()
    {
        RestorePathReach();
        _target = null;
        _source = null;
        _failures = _probe = _invalidPaths = 0;
        _walkedDelta = default;
        _retryAt = _rescueAt = 0f;
        _recoveryOrigin = null;
        _triedLandings.Clear();
        _reachedFrontiers.Clear();
        _rejections.Clear();
        _partialEnd = null;
        _blockedFrom = _blockedCorner = null;
        _repeatedSegment = 0;
        _edgeProbe = EdgeProbeCount;
        _edgeDirection = default;
    }

    internal void Suspend()
    {
        _progressAt = Time.time;
        _progressPosition = bot.Position;
        _walkedDelta = default;
    }

    // Count only validated walking. Rescue offsets never contribute, and walking back to the
    // original obstruction cannot reset the search. This also permits successive nearby seams.
    internal void Walked(Vector3 from, Vector3 to)
    {
        if (!_target.HasValue || !_recoveryOrigin.HasValue) return;
        _walkedDelta += to - from;
        if (_walkedDelta.sqrMagnitude <= ProgressDistance * ProgressDistance
            || Vector3.Distance(to, _target.Value) > Vector3.Distance(_recoveryOrigin.Value, _target.Value) - ProgressDistance)
            return;
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} local recovery completed by walking: from={_recoveryOrigin.Value} to={to} nativeTarget={_target.Value}");
        ResetProgress();
    }

    private void ResetProgress()
    {
        Suspend();
        _failures = _probe = _invalidPaths = 0;
        _retryAt = _rescueAt = 0f;
        _recoveryOrigin = null;
        _triedLandings.Clear();
        _rejections.Clear();
        _edgeProbe = EdgeProbeCount;
        _edgeDirection = default;
        _repeatedSegment = 0;
        _blockedFrom = _blockedCorner = null;
    }

    internal bool Blocked(string reason = null, Vector3? attempted = null, Vector3? corner = null,
        Vector3? projected = null, Vector3? edge = null)
    {
        var from = bot.Position;
        if (corner.HasValue)
        {
            _repeatedSegment = _blockedFrom.HasValue && _blockedCorner.HasValue
                && Vector3.Distance(from, _blockedFrom.Value) < 0.25f
                && Vector3.Distance(corner.Value, _blockedCorner.Value) < 0.25f ? _repeatedSegment + 1 : 1;
            _blockedFrom = from;
            _blockedCorner = corner;
            if (_repeatedSegment == 1)
            {
                _edgeProbe = EdgeProbeCount;
                _edgeDirection = default;
                var direction = corner.Value - from;
                direction.y = 0f;
                if (reason == "navmesh-edge" && Finite(direction) && direction.sqrMagnitude > 0.0001f)
                {
                    _edgeOrigin = from;
                    _edgeDirection = direction.normalized;
                    _edgeProbe = 0;
                }
            }
        }
        if (reason != null && Time.time >= _blockedReportAt)
        {
            _blockedReportAt = Time.time + 30f;
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} route blocked: reason={reason} from={bot.Position} attempted={attempted} {Summary}");
            if (corner.HasValue)
                Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} blocked segment: index={bot.Mover.ActualPathController.CurPath?.CurIndex} from={Precise(from)} corner={Precise(corner)} desired={Precise(attempted)} projected={Precise(projected)} edge={Precise(edge)} repeat={_repeatedSegment} retryIn={RetryDelay:F1}s");
        }
        if (!_target.HasValue) return false;
        bot.Mover.ActualPathController.Stop();
        bot.Mover.IsMoving = false;
        _failures++;
        _retryAt = Time.time + RetryDelay;
        return true;
    }

    internal NavMeshPathStatus Request(Vector3 target, float reach)
    {
        if (!Finite(target)) { Cancel(); bot.Mover.ActualPathController.Stop(); return NavMeshPathStatus.PathInvalid; }
        if (reach < 0f) reach = bot.Settings.FileSettings.Move.REACH_DIST;
        if (float.IsNaN(reach) || float.IsInfinity(reach)) reach = 0.5f;
        reach = Mathf.Max(0.1f, reach);
        if (!_target.HasValue || (_target.Value - target).sqrMagnitude > 0.0025f || Mathf.Abs(_reach - reach) > 0.01f)
        {
            Cancel();
            _target = target;
            _source = "go-to-point";
            _reach = reach;
            _status = NavMeshPathStatus.PathInvalid;
            Suspend();
            bot.Mover.ActualPathController.Stop();
        }
        Update();
        return _status;
    }

    // Adopting an existing native route must not stop it, consume corners, or recalculate it.
    internal void Retain(Vector3 target, float reach, NavMeshPathStatus status, string source)
    {
        if (!Finite(target)) { Cancel(); return; }
        if (reach < 0f) reach = bot.Settings.FileSettings.Move.REACH_DIST;
        if (float.IsNaN(reach) || float.IsInfinity(reach)) reach = 0.5f;
        reach = Mathf.Max(0.1f, reach);
        if (!_target.HasValue || (_target.Value - target).sqrMagnitude > 0.0025f)
        {
            Cancel();
            Suspend();
        }
        _target = target;
        _reach = reach;
        _status = status;
        _source = source;
        AdjustPathReach();
        if (Time.time < _retainedReportAt) return;
        _retainedReportAt = Time.time + 30f;
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} retained native order: {Summary}");
    }

    internal void Update()
    {
        if (!_target.HasValue) return;
        if (NativeGhostSystem.MovementPinned(bot) || bot.Mover.Pause && bot.Mover.RemainPause > 0f)
        { Suspend(); return; }
        AdjustPathReach();
        if (Vector3.Distance(bot.Position, _target.Value) < _reach)
        {
            bot.Mover.ActualPathController.Stop();
            bot.Mover.IsMoving = false;
            _status = NavMeshPathStatus.PathComplete;
            Cancel();
            return;
        }
        // Preserve the fallback for large departures and ordinary routes. Within the rescue
        // area, only validated walking with net progress can release the original anchor.
        if ((bot.Position - _progressPosition).sqrMagnitude > ProgressDistance * ProgressDistance
            && (!_recoveryOrigin.HasValue
                || Vector3.Distance(bot.Position, _recoveryOrigin.Value) > MaxRelocation + ProgressDistance))
        {
            ResetProgress();
        }
        if (bot.Mover.ActualPathController.HavePath || Time.time < _retryAt) return;
        if (_partialEnd.HasValue && Vector3.Distance(bot.Position, _partialEnd.Value) <= _reach + 0.2f)
        {
            if (!_reachedFrontiers.Any(p => Vector3.Distance(p, _partialEnd.Value) < 1f))
            {
                if (_reachedFrontiers.Count == 16) _reachedFrontiers.RemoveAt(0);
                _reachedFrontiers.Add(_partialEnd.Value);
            }
            _partialEnd = null;
        }
        if (_failures >= 3 && Time.time - _progressAt >= StuckSeconds && Time.time >= _rescueAt)
        {
            TryRelocate(_target.Value);
            if (bot.Mover.ActualPathController.HavePath) return;
        }
        if (!TakeQuery()) return;
        _retryAt = Time.time + RetryDelay;
        var target = _target.Value;
        var calculated = NavMesh.CalculatePath(bot.Position, target, NavMesh.AllAreas, _path);
        _status = calculated ? _path.status : NavMeshPathStatus.PathInvalid;
        if (_status == NavMeshPathStatus.PathInvalid)
        {
            if (_invalidPaths++ == 0) _invalidSince = Time.time;
            _retryAt = Time.time + RetryDelay;
        }
        else _invalidPaths = 0;
        var corners = _path.corners;
        if (calculated && _status != NavMeshPathStatus.PathInvalid && corners.Length > 1
            && Vector3.Distance(bot.Position, corners[corners.Length - 1]) > PathReach(corners[corners.Length - 1]))
        {
            SetPath(corners, _status);
            if (Time.time >= _repathReportAt)
            {
                _repathReportAt = Time.time + 30f;
                Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} repath in Ghost: {_status} target={target} end={corners[corners.Length - 1]}");
            }
            return;
        }
        _failures++;
        Report($"route pending in Ghost: {_status} target={target} remaining={Vector3.Distance(bot.Position, target):F1}m retryIn={RetryDelay:F1}s");
    }

    private void TryRelocate(Vector3 target)
    {
        // A small, bounded correction across a broken navmesh seam. Never jump to the leader/goal.
        var origin = bot.Position;
        _recoveryOrigin ??= origin;
        for (var attempt = 0; attempt < 4 && (_edgeProbe < EdgeProbeCount || _probe < RescueRings.Length * 8); attempt++)
        {
            if (!TakeQuery()) return;
            // Probe the failed segment closely before the coarse radial search. A narrow opening
            // can fit a small correction even when every metre-spaced landing crosses a wall.
            var edgeProbe = _edgeProbe < EdgeProbeCount;
            Vector3 candidate;
            if (edgeProbe)
            {
                var index = _edgeProbe++;
                var lateral = new Vector3(-_edgeDirection.z, 0f, _edgeDirection.x);
                candidate = _edgeOrigin + _edgeDirection * EdgeForward[index / EdgeSideways.Length]
                    + lateral * EdgeSideways[index % EdgeSideways.Length];
            }
            else
            {
                var index = _probe++;
                var angle = (index % 8) * Mathf.PI / 4f;
                candidate = _recoveryOrigin.Value + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * RescueRings[index / 8];
            }
            if (!NavMesh.SamplePosition(candidate, out var hit, edgeProbe ? 0.1f : 0.4f, NavMesh.AllAreas)) { Reject("no-sample"); continue; }
            candidate = hit.position;
            if (!Finite(candidate)) { Reject("nonfinite-sample"); continue; }
            if (Vector3.Distance(origin, candidate) > MaxRelocation
                || Vector3.Distance(_recoveryOrigin.Value, candidate) > MaxRelocation
                || (candidate - origin).sqrMagnitude < (edgeProbe ? 0.01f : 0.25f)
                || TriedLanding(candidate, edgeProbe ? 0.01f : 0.5625f)) { Reject("bounds-or-repeat"); continue; }
            if (Mathf.Abs(candidate.y - origin.y) > 0.75f || DangerZones.IsInside(candidate)) { Reject("height-or-danger"); continue; }
            if (!NativeGhostRelocation.IsSafe(bot, origin, candidate, doors, out var unsafeReason))
            { Reject("unsafe-" + unsafeReason); continue; }
            if (!NavMesh.CalculatePath(candidate, target, NavMesh.AllAreas, _path)
                || _path.status == NavMeshPathStatus.PathInvalid) { Reject("invalid-path"); continue; }
            var corners = _path.corners;
            if (corners.Length < 2 || corners.Any(p => !Finite(p))) { Reject("invalid-corners"); continue; }
            var end = corners[corners.Length - 1];
            if (_path.status == NavMeshPathStatus.PathComplete)
            {
                if (Vector3.Distance(end, target) > _reach) { Reject("end-outside-goal"); continue; }
            }
            else
            {
                // The walk must leave the rescue area, not just relocate toward another nearby edge.
                if (Vector3.Distance(end, candidate) < ProgressDistance
                    || Vector3.Distance(end, _recoveryOrigin.Value) <= MaxRelocation + ProgressDistance
                    || Vector3.Distance(end, target) > Vector3.Distance(_recoveryOrigin.Value, target) - ProgressDistance)
                { Reject("partial-no-progress"); continue; }
                if (_reachedFrontiers.Any(p => Vector3.Distance(p, end) < 1f))
                { Reject("partial-repeated-frontier"); continue; }
            }
            if (!CanStartRoute(candidate, corners)) { Reject("blocked-start"); continue; }
            _triedLandings.Add(candidate);
            bot.GetPlayer.Transform.position = candidate;
            NativeGhostSystem.SyncMover(bot);
            SetPath(corners, _path.status);
            _rescueAt = Time.time + 30f;
            // Preserve the failure history until walking makes real progress. The relocation
            // itself is excluded from that measurement, including any previous walking delta.
            _progressPosition = candidate;
            _walkedDelta = default;
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} relocated in Ghost: {Vector3.Distance(origin, candidate):F1}m from={origin} to={candidate} nativeTarget={target} nav={_status} end={end} probe={(edgeProbe ? "edge" : "radial")}");
            _rejections.Clear();
            return;
        }
        if (_edgeProbe >= EdgeProbeCount && _probe >= RescueRings.Length * 8)
        {
            _probe = 0;
            // Geometry and door state can change. Retry rejected edge candidates with the next
            // bounded scan, while keeping successful landings in the no-repeat history.
            _edgeProbe = _edgeDirection.sqrMagnitude > 0.0001f ? 0 : EdgeProbeCount;
            _rescueAt = Time.time + 30f;
            // Completing a scan is already limited by _rescueAt. Do not lose its rejection
            // summary because a routine pending-route report happened during the same scan.
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} route unresolved in Ghost: no safe connected point within {MaxRelocation:F0}m, target={target} rejected=[{string.Join(",", _rejections.Select(p => $"{p.Key}:{p.Value}"))}]");
            _rejections.Clear();
        }
    }

    private void Reject(string reason)
        => _rejections[reason] = _rejections.TryGetValue(reason, out var count) ? count + 1 : 1;

    private static string Precise(Vector3? p)
        => p.HasValue ? FormattableString.Invariant($"({p.Value.x:F3},{p.Value.y:F3},{p.Value.z:F3})") : "none";

    // Arrival belongs to the requested goal. A projected last corner must not consume the route
    // while the original action is still outside its radius. A small margin also respects '<'.
    private float PathReach(Vector3 end)
    {
        if (!_target.HasValue) return _reach;
        var offset = Vector3.Distance(end, _target.Value);
        if (offset >= _reach && _status != NavMeshPathStatus.PathComplete) return _reach;
        return Mathf.Max(0f, _reach - offset - 0.01f);
    }

    private void SetPath(Vector3[] corners, NavMeshPathStatus status)
    {
        RestorePathReach();
        _status = status;
        if (status != NavMeshPathStatus.PathInvalid) _invalidPaths = 0;
        _partialEnd = status == NavMeshPathStatus.PathPartial ? corners[corners.Length - 1] : null;
        bot.Mover.ActualPathController.GoToByWay(corners, _reach);
        AdjustPathReach();
    }

    private void AdjustPathReach()
    {
        var path = bot.Mover.ActualPathController.CurPath;
        if (path?.TargetPoint == null || !_target.HasValue) return;
        if (!ReferenceEquals(path, _adjustedPath))
        {
            _adjustedPath = path;
            _originalPathReach = path.ReachDist;
        }
        path.SetReachDist(PathReach(path.TargetPoint.Position));
    }

    internal void RestorePathReach()
    {
        try
        {
            var path = bot?.Mover?.ActualPathController?.CurPath;
            if (path != null && ReferenceEquals(path, _adjustedPath)) path.SetReachDist(_originalPathReach);
        }
        finally { _adjustedPath = null; }
    }

    private bool TriedLanding(Vector3 candidate, float radiusSqr)
    {
        if (_triedLandings.Count >= MaxLandings) return true;
        foreach (var previous in _triedLandings)
            if ((candidate - previous).sqrMagnitude < radiusSqr) return true;
        return false;
    }

    // PathComplete alone can still lead straight back into a seam. Check the first two metres
    // with the same sampling and edge checks as the Ghost mover, including short corner segments.
    internal static bool CanStartRoute(Vector3 from, Vector3[] corners)
        => CanStartRoute(from, corners, out _);

    internal static bool CanStartRoute(Vector3 from, Vector3[] corners, out string reason)
    {
        reason = null;
        var remaining = 2f;
        var moved = false;
        for (var i = 0; i < corners.Length && i < 16 && remaining > 0.001f; i++)
        {
            var corner = corners[i];
            if (!Finite(corner)) { reason = "nonfinite-corner"; return false; }
            for (var step = 0; step < 9 && remaining > 0.001f; step++)
            {
                var distance = Vector3.Distance(from, corner);
                if (distance < 0.01f) break;
                var amount = Mathf.Min(0.25f, Mathf.Min(remaining, distance));
                var next = Vector3.MoveTowards(from, corner, amount);
                if (DangerZones.IsInside(next)) { reason = "start-danger"; return false; }
                if (!NavMesh.SamplePosition(next, out var hit, 0.75f, NavMesh.AllAreas)) { reason = "start-off-navmesh"; return false; }
                if (!Finite(hit.position)) { reason = "start-nonfinite"; return false; }
                if (DangerZones.IsInside(hit.position)) { reason = "start-danger"; return false; }
                if (NavMesh.Raycast(from, hit.position, out _, NavMesh.AllAreas)) { reason = "start-navmesh-edge"; return false; }
                if ((hit.position - from).sqrMagnitude < 0.000001f) { reason = "start-no-progress"; return false; }
                from = hit.position;
                remaining -= amount;
                moved = true;
                if (distance <= amount) break;
            }
        }
        if (!moved) reason = "start-no-progress";
        return moved;
    }

    private void Report(string message)
    {
        if (Time.time < _reportAt) return;
        _reportAt = Time.time + 30f;
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} {message}");
    }

    private static bool Finite(Vector3 p)
        => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)
            && !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);
}
