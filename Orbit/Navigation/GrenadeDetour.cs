using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Navigation;

// Per awake traveller, independent of the squad's objective and waypoint blacklist.
internal sealed class GrenadeInterruptions
{
    private const float RepeatWindow = 10f;
    private const float MemorySeconds = 20f;
    private int _count;
    private Vector3 _firstDanger;
    private float _last;
    private Vector3 _firstPosition;
    internal Vector3 Danger { get; private set; }
    internal bool Armed(float now) => _count >= 3 && now - _last <= MemorySeconds;
    internal bool Waiting(int revision, float now) => Plan != null && Revision == revision && Armed(now);
    internal GrenadeDetourPlan Plan;
    internal int Revision;

    internal bool Observe(Vector3 danger, Vector3 bot, float now)
    {
        var continuing = _count > 0
            && now - _last <= (_count >= 3 ? MemorySeconds : RepeatWindow)
            && (bot - _firstPosition).sqrMagnitude <= 36f && (danger - _firstDanger).sqrMagnitude <= 16f;
        if (!continuing)
        {
            _count = 0;
            _firstPosition = bot;
            _firstDanger = danger;
            Plan = null;
        }
        Danger = danger;
        _last = now;
        _count = Math.Min(_count + 1, 4);
        return _count == 3;
    }
}

internal sealed class GrenadeDetourPlan
{
    // Leave a little clearance around the 18 m native reaction range.
    internal const float Radius = 20f;
    private readonly Vector3 _origin, _target, _danger, _forward, _side;
    private readonly List<Vector3> _corners = new();
    private int _attempt, _leg;
    internal Vector3[] Result { get; private set; }
    internal bool Exhausted => _attempt >= 4;

    internal GrenadeDetourPlan(Vector3 origin, Vector3 target, Vector3 danger)
    {
        _origin = origin;
        _target = target;
        _danger = danger;
        var direction = target - origin;
        direction.y = 0;
        _forward = direction.normalized;
        _side = new Vector3(-_forward.z, 0, _forward.x);
        if ((_target - danger).sqrMagnitude < Radius * Radius || _forward.sqrMagnitude < .5f)
            _attempt = 4;
    }

    // Starting just inside the radius is normal after an escape. Allow movement out,
    // but never a segment that first moves back toward the grenade or re-enters.
    internal static bool Clears(Vector3 start, Vector3[] corners, Vector3 danger)
    {
        if (corners == null || corners.Length == 0) return false;
        var from = start - danger;
        foreach (var corner in corners)
        {
            var to = corner - danger;
            var segment = to - from;
            var length = segment.sqrMagnitude;
            if (length > .000001f)
            {
                var dot = from.x * segment.x + from.y * segment.y + from.z * segment.z;
                var closest = from + segment * Mathf.Clamp01(-dot / length);
                if (closest.sqrMagnitude + .01f < Math.Min(Radius * Radius, from.sqrMagnitude))
                    return false;
            }
            from = to;
        }
        return from.sqrMagnitude >= Radius * Radius;
    }

    // One step issues at most one path query. The caller shares the frame budget across bots.
    internal void Step(Func<Vector3, Vector3?> sample, Func<Vector3, Vector3, Vector3[]> path)
    {
        if (Exhausted || Result != null) return;
        var from = _leg == 0 ? _origin : _corners[_corners.Count - 1];
        Vector3? destination = _target;
        if (_leg < 2)
        {
            var clearance = _attempt < 2 ? Radius + 6f : Radius + 16f;
            var side = (_attempt % 2 == 0 ? 1 : -1) * clearance;
            var pivot = _danger + _forward * (_leg == 0 ? -clearance : clearance) + _side * side;
            pivot.y = _origin.y;
            destination = sample(pivot);
        }
        var corners = destination.HasValue ? path(from, destination.Value) : null;
        if (!Clears(from, corners, _danger))
        {
            _attempt++;
            _leg = 0;
            _corners.Clear();
            return;
        }
        // The two legs share their sampled endpoint. Keep exactly one copy of the seam.
        for (var i = _leg == 0 ? 0 : 1; i < corners.Length; i++) _corners.Add(corners[i]);
        if (++_leg == 3) Result = _corners.ToArray();
    }
}
