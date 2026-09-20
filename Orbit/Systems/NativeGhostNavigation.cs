using System.Collections.Generic;
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
    private static readonly float[] RescueRings = { 1f, 2f, 4f, MaxRelocation };
    private static int _budgetFrame = -1, _queries;
    private readonly NavMeshPath _path = new();
    private Vector3? _target;
    private float _reach = 0.5f, _retryAt, _progressAt, _rescueAt, _reportAt, _repathReportAt, _blockedReportAt;
    private Vector3 _progressPosition;
    private Vector3? _recoveryOrigin;
    private readonly List<Vector3> _triedLandings = new();
    private int _failures, _probe;
    private NavMeshPathStatus _status = NavMeshPathStatus.PathInvalid;
    private string _source;
    private float _retainedReportAt;
    internal bool HasOrder => _target.HasValue;
    internal Vector3? Target => _target;
    internal float StalledFor => HasOrder ? Time.time - _progressAt : 0f;
    internal bool RecoveringLocally => _recoveryOrigin.HasValue;

    internal void SetReachDistance(float reach)
    {
        if (!float.IsNaN(reach) && !float.IsInfinity(reach) && reach >= 0f)
            _reach = Mathf.Max(0.1f, reach);
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
        _target = null;
        _source = null;
        _failures = _probe = 0;
        _retryAt = _rescueAt = 0f;
        _recoveryOrigin = null;
        _triedLandings.Clear();
    }

    internal void Suspend()
    {
        _progressAt = Time.time;
        _progressPosition = bot.Position;
    }

    internal bool Blocked(string reason = null, Vector3? attempted = null)
    {
        if (reason != null && Time.time >= _blockedReportAt)
        {
            _blockedReportAt = Time.time + 30f;
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} route blocked: reason={reason} from={bot.Position} attempted={attempted} {Summary}");
        }
        if (!_target.HasValue) return false;
        bot.Mover.ActualPathController.Stop();
        bot.Mover.IsMoving = false;
        _failures++;
        _retryAt = Time.time + RetryInterval;
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
        if (Time.time < _retainedReportAt) return;
        _retainedReportAt = Time.time + 30f;
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} retained native order: {Summary}");
    }

    internal void Update()
    {
        if (!_target.HasValue) return;
        if (NativeGhostSystem.MovementPinned(bot) || bot.Mover.Pause && bot.Mover.RemainPause > 0f)
        { Suspend(); return; }
        if (Vector3.Distance(bot.Position, _target.Value) <= _reach)
        {
            bot.Mover.ActualPathController.Stop();
            bot.Mover.IsMoving = false;
            _status = NavMeshPathStatus.PathComplete;
            Cancel();
            return;
        }
        // Walking out of the local area is progress. A rescue and its return trip are not.
        // Keep the search centred on the original blockage so successive rescues cannot drift.
        if ((bot.Position - _progressPosition).sqrMagnitude > ProgressDistance * ProgressDistance
            && (!_recoveryOrigin.HasValue
                || Vector3.Distance(bot.Position, _recoveryOrigin.Value) > MaxRelocation + ProgressDistance))
        {
            Suspend();
            _failures = _probe = 0;
            _recoveryOrigin = null;
            _triedLandings.Clear();
        }
        if (bot.Mover.ActualPathController.HavePath || Time.time < _retryAt) return;
        if (_failures >= 3 && Time.time - _progressAt >= StuckSeconds && Time.time >= _rescueAt)
        {
            TryRelocate(_target.Value);
            if (bot.Mover.ActualPathController.HavePath) return;
        }
        if (!TakeQuery()) return;
        _retryAt = Time.time + RetryInterval;
        var target = _target.Value;
        var calculated = NavMesh.CalculatePath(bot.Position, target, NavMesh.AllAreas, _path);
        _status = calculated ? _path.status : NavMeshPathStatus.PathInvalid;
        var corners = _path.corners;
        if (calculated && _status != NavMeshPathStatus.PathInvalid && corners.Length > 1
            && Vector3.Distance(bot.Position, corners[corners.Length - 1]) > _reach)
        {
            bot.Mover.ActualPathController.GoToByWay(corners, _reach);
            if (Time.time >= _repathReportAt)
            {
                _repathReportAt = Time.time + 30f;
                Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} repath in Ghost: {_status} target={target} end={corners[corners.Length - 1]}");
            }
            return;
        }
        _failures++;
        Report($"route pending in Ghost: {_status} target={target} remaining={Vector3.Distance(bot.Position, target):F1}m");
    }

    private void TryRelocate(Vector3 target)
    {
        // A small, bounded correction across a broken navmesh seam. Never jump to the leader/goal.
        var origin = bot.Position;
        _recoveryOrigin ??= origin;
        for (var attempt = 0; attempt < 4 && _probe < RescueRings.Length * 8; attempt++)
        {
            if (!TakeQuery()) return;
            var index = _probe++;
            var angle = (index % 8) * Mathf.PI / 4f;
            var candidate = _recoveryOrigin.Value + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * RescueRings[index / 8];
            if (!NavMesh.SamplePosition(candidate, out var hit, 0.4f, NavMesh.AllAreas)) continue;
            candidate = hit.position;
            if (Vector3.Distance(origin, candidate) > MaxRelocation
                || Vector3.Distance(_recoveryOrigin.Value, candidate) > MaxRelocation
                || (candidate - origin).sqrMagnitude < 0.25f || TriedLanding(candidate)
                || Mathf.Abs(candidate.y - origin.y) > 0.75f || DangerZones.IsInside(candidate)) continue;
            if (!NativeGhostRelocation.IsSafe(bot, origin, candidate, doors)) continue;
            if (!NavMesh.CalculatePath(candidate, target, NavMesh.AllAreas, _path)
                || _path.status != NavMeshPathStatus.PathComplete) continue;
            var corners = _path.corners;
            if (corners.Length < 2 || Vector3.Distance(corners[corners.Length - 1], target) > _reach
                || !CanStartRoute(candidate, corners)) continue;
            _triedLandings.Add(candidate);
            bot.GetPlayer.Transform.position = candidate;
            NativeGhostSystem.SyncMover(bot);
            bot.Mover.ActualPathController.GoToByWay(corners, _reach);
            _status = NavMeshPathStatus.PathComplete;
            _rescueAt = Time.time + 30f;
            // Preserve failures, search cursor and elapsed stuck time until real walking leaves
            // this area. Record the landing only as the start of the next walking measurement.
            _progressPosition = candidate;
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} relocated in Ghost: {Vector3.Distance(origin, candidate):F1}m from={origin} to={candidate} nativeTarget={target}");
            return;
        }
        if (_probe >= RescueRings.Length * 8)
        {
            _probe = 0;
            _rescueAt = Time.time + 30f;
            Report($"route unresolved in Ghost: no safe connected point within {MaxRelocation:F0}m, target={target}");
        }
    }

    private bool TriedLanding(Vector3 candidate)
    {
        if (_triedLandings.Count >= RescueRings.Length * 8) return true;
        foreach (var previous in _triedLandings)
            if ((candidate - previous).sqrMagnitude < 0.5625f) return true;
        return false;
    }

    // PathComplete alone can still lead straight back into a seam. Check the first two metres
    // with the same sampling and edge checks as the Ghost mover, including short corner segments.
    internal static bool CanStartRoute(Vector3 from, Vector3[] corners)
    {
        var remaining = 2f;
        var moved = false;
        for (var i = 0; i < corners.Length && i < 16 && remaining > 0.001f; i++)
        {
            var corner = corners[i];
            if (!Finite(corner)) return false;
            for (var step = 0; step < 9 && remaining > 0.001f; step++)
            {
                var distance = Vector3.Distance(from, corner);
                if (distance < 0.01f) break;
                var amount = Mathf.Min(0.25f, Mathf.Min(remaining, distance));
                var next = Vector3.MoveTowards(from, corner, amount);
                if (DangerZones.IsInside(next)
                    || !NavMesh.SamplePosition(next, out var hit, 0.75f, NavMesh.AllAreas)
                    || !Finite(hit.position) || DangerZones.IsInside(hit.position)
                    || NavMesh.Raycast(from, hit.position, out _, NavMesh.AllAreas)
                    || (hit.position - from).sqrMagnitude < 0.000001f) return false;
                from = hit.position;
                remaining -= amount;
                moved = true;
                if (distance <= amount) break;
            }
        }
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
