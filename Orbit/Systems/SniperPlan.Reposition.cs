using System.Collections.Generic;
using EFT;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

internal sealed partial class SniperPlan
{
    private const float RoofRadius = 12f, RoofHeightTolerance = .75f;
    private readonly List<CoverPoint> _shiftCovers = new(64);
    private readonly List<Vector3> _shiftViews = new(5);
    private readonly Vector3[] _shiftCorners = new Vector3[64];
    private Vector3 _roofOrigin, _shiftTarget, _shiftProgress;
    private float _nextShift, _shiftDeadline, _shiftProgressAt, _shiftYaw, _shiftPose;
    private int _shiftCandidate;
    private bool _roofSet, _shiftSearch, _shifting, _shiftYielded;

    // Keep the first occupied post as the boundary, so repeated moves cannot drift off the roof.
    private bool WithinRoof(Vector3 point) => _roofSet && Mathf.Abs(point.y - _roofOrigin.y) <= RoofHeightTolerance
        && (point - _roofOrigin).sqrMagnitude <= RoofRadius * RoofRadius;

    private void SchedulePostShift(float now)
    { _nextShift = now + Random.Range(45f, 75f); _shiftSearch = false; _shiftCandidate = 0; }

    internal bool IsPostShift(Agent agent) => _shifting && !_paused && _main is { Completed: false }
        && agent == _actor && agent.IsActive && _orders.TryGetValue(agent, out var order)
        && agent.Objective.Location == order;

    // The path returned later by the movement queue is checked too, including retries.
    internal bool AllowPostShiftPath(Agent agent, Vector3 target, IReadOnlyList<Vector3> corners, NavMeshPathStatus status)
        => !IsPostShift(agent) || status == NavMeshPathStatus.PathComplete
            && (target - _shiftTarget).sqrMagnitude <= .25f && SafePostShiftPath(corners, corners?.Count ?? 0, _shiftTarget);

    private bool SafePostShiftPath(IReadOnlyList<Vector3> corners, int count, Vector3 target)
    {
        if (count < 2 || count >= _shiftCorners.Length || !WithinRoof(_actor.Position)
            || (corners[0] - _actor.Position).sqrMagnitude > 2.25f
            || (corners[count - 1] - target).sqrMagnitude > .25f) return false;
        var length = 0f;
        for (var i = 0; i < count; i++)
        {
            if (!WithinRoof(corners[i])) return false;
            if (i > 0) length += Vector3.Distance(corners[i - 1], corners[i]);
        }
        return length <= 24f;
    }

    private bool ShiftUnsafe() => !_actor.IsActive || _actor.Bot.Memory.IsUnderFire
        || _actor.Bot.Memory.HaveEnemy || Time.time - _actor.LastHpDropTime < 5f
        || _actor.Bot.WeaponManager.Reload.Reloading || CorpseEscort.InFlight(_actor)
        || _actor.SoloExtractRequested || _squad.ExtractRequested;

    private bool TickPostShift(Squad squad, WaypointSystem w, float now)
    {
        if (_shifting)
        {
            if (ShiftUnsafe() || !IsPostShift(_actor))
            {
                InterruptPostShift("danger-or-transfer");
                _shiftYielded = true;
                Release(squad, w); _paused = true;
                SetStatus(squad, "paused");
                return true;
            }
            if (!WithinRoof(_actor.Position))
            {
                InterruptPostShift("left-roof");
                Release(squad, w); _paused = true;
                return true; // Existing alert-return logic owns a later return from danger.
            }
            if ((_actor.Position - _shiftProgress).sqrMagnitude > .25f)
            { _shiftProgress = _actor.Position; _shiftProgressAt = now; }
            if (_actor.Objective.Status == ObjectiveStatus.Finished && Near(_actor.Position, _shiftTarget, 1.8f))
            {
                _post = _leg = _shiftTarget; _pose = _shiftPose;
                _watchDirections.Clear(); _watchDirections.AddRange(_shiftViews);
                _shifting = false;
                UpdateOccupiedPost(w, now);
                ShiftLog("arrived", "new-post");
            }
            else if (_actor.Objective.Status == ObjectiveStatus.Failed || now >= _shiftDeadline
                || now - _shiftProgressAt >= 4f)
            {
                // This is optional movement. Stay where we safely got to, without a teleport,
                // a new ascent or resetting the camp clock just to finish a short reposition.
                MovementSystem.StopSniperPostShift(_actor);
                _post = _leg = _actor.Position;
                _shifting = false;
                Assign(_actor, _post, w, "Sniper post");
                _actor.Objective.Status = ObjectiveStatus.Finished;
                UpdateOccupiedPost(w, now);
                ShiftLog("cancelled", "blocked-or-timeout");
            }
            return true;
        }
        if (!_roofSet || now < _nextShift) return false;
        if (ShiftUnsafe() || !WithinRoof(_actor.Position) || !Near(_actor.Position, _post, 3f)
            || _actor.Objective.Status != ObjectiveStatus.Finished || _duration - _held < 20f)
        { SchedulePostShift(now); return false; }
        if (now < _nextWork) return false;
        if (!w.TryOperationWork(this)) return true;
        _nextWork = now + .15f;
        if (!_shiftSearch)
        {
            _shiftSearch = true; _shiftCandidate = 0; _shiftYaw = Random.Range(0f, 360f);
            w.CollectCorpseEscortCover(_roofOrigin, _shiftCovers, ambush: true);
            _shiftCovers.RemoveAll(c => c.Category == CoverCategory.None || !WithinRoof(c.Position)
                || (c.Position - _post).sqrMagnitude < 9f);
            if (_shiftCovers.Count > 8) _shiftCovers.RemoveRange(8, _shiftCovers.Count - 8);
            return false;
        }
        var index = _shiftCandidate++;
        var covered = index < _shiftCovers.Count;
        var radial = index - _shiftCovers.Count;
        var sample = covered ? _shiftCovers[index].Position
            : _roofOrigin + Direction(_shiftYaw + radial * 137.5f, 0) * (4f + radial % 3 * 3f);
        if (TryShiftCandidate(sample, out var target))
        {
            _shiftTarget = target; _shiftProgress = _actor.Position; _shiftProgressAt = now;
            _shiftDeadline = now + 25f; _shifting = true; _shiftSearch = false;
            SniperCombat.Remove(_actor.Bot); // No stationary-post lease while walking to a new post.
            Assign(_actor, target, w, "Sniper reposition");
            squad.Objective.Location = _orders[_actor]; squad.Objective.Status = SquadObjectiveState.Active;
            SetStatus(squad, "changing post");
            ShiftLog("moving", covered ? "cover-and-view" : "open-view");
            return true;
        }
        if (_shiftCandidate >= _shiftCovers.Count + 8)
        { ShiftLog("skipped", "no-safe-post"); SchedulePostShift(now); }
        return false;
    }

    private bool TryShiftCandidate(Vector3 sample, out Vector3 target)
    {
        target = default;
        if (!NavMesh.SamplePosition(sample, out var nav, .75f, NavMesh.AllAreas)
            || !WithinRoof(nav.position) || (nav.position - _post).sqrMagnitude < 9f) return false;
        foreach (var member in _squad.Members)
            if (member != _actor && !member.Bot.IsDead && (member.Position - nav.position).sqrMagnitude < 4f) return false;
        if (!NavMesh.CalculatePath(_actor.Position, nav.position, NavMesh.AllAreas, _path)
            || _path.status != NavMeshPathStatus.PathComplete
            || !SafePostShiftPath(_shiftCorners, _path.GetCornersNonAlloc(_shiftCorners), nav.position)) return false;
        _shiftViews.Clear();
        _shiftPose = .5f;
        for (var stance = 0; stance < 2 && _shiftViews.Count == 0; stance++)
        {
            _shiftPose = stance == 0 ? .5f : 1f;
            var head = nav.position + Vector3.up * (stance == 0 ? 1.25f : 1.65f);
            var count = _site.WatchArc >= 359 ? 4 : 5;
            for (var i = 0; i < count; i++)
            {
                var direction = Direction(_site.WatchYaw - _site.WatchArc / 2 + i * _site.WatchArc / 4, _site.WatchPitch);
                if (OpenView(head, direction)) _shiftViews.Add(direction);
            }
        }
        if (_shiftViews.Count == 0) return false;
        target = nav.position;
        return true;
    }

    private void UpdateOccupiedPost(WaypointSystem w, float now)
    {
        _main.Position = _post; _main.CellCoords = w.WorldToCell(_post);
        _actor.Guard.CoverPoint = new CoverPoint(_post, Direction(_site.WatchYaw, _site.WatchPitch), CoverCategory.None, CoverLevel.Stay);
        _actor.Guard.WatchTimeout = 0;
        _squad.Objective.Location = _orders[_actor];
        SchedulePostShift(now); SetStatus(_squad, "holding");
        SniperCombat.Refresh(_actor, _post, now + 2);
    }

    private void InterruptPostShift(string reason)
    {
        if (_shifting)
        { MovementSystem.StopSniperPostShift(_actor); ShiftLog("cancelled", reason); }
        _shifting = false;
        SchedulePostShift(Time.time);
    }

    private void ResetPostShift()
    { InterruptPostShift("objective-ended"); _roofSet = _shiftYielded = false; }

    private void ShiftLog(string state, string reason)
        => Log.Info($"SNIPER REPOSITION: {_actor} state={state} reason={reason} from={_actor.Position} target={_shiftTarget} roof={_roofOrigin} held={_held:F1}s duration={_duration:F1}s");
}
