using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// A marked-room search uses the room linked to its door, not a sphere spanning nearby rooms.
internal sealed class MarkedRoomScope
{
    private readonly List<(Matrix4x4 Inverse, Bounds Bounds)> _volumes = new();
    private readonly float _floor;
    internal Vector3 Center { get; private set; }
    internal float Radius { get; private set; }
    internal string Source { get; private set; } = "configured area";

    internal MarkedRoomScope(Vector3 center, float radius, SpatialAudioRoom room = null)
    {
        Center = center; _floor = center.y; Radius = radius;
        if (room == null) return;
        if (room.Colliders != null)
            foreach (var box in room.Colliders)
                if (box != null)
                    _volumes.Add((box.transform.worldToLocalMatrix, new Bounds(box.center, box.size)));
        if (_volumes.Count == 0)
            _volumes.Add((Matrix4x4.identity, room.Bounds));
        Source = "native room " + room.name;
        if (!Contains(center)) Center = new Vector3(room.Bounds.center.x, center.y, room.Bounds.center.z);
        if (NavMesh.SamplePosition(Center, out var hit, 2f, NavMesh.AllAreas) && Contains(hit.position)) Center = hit.position;
        Radius = Mathf.Max(radius, room.Bounds.extents.magnitude + Vector3.Distance(Center, room.Bounds.center));
    }

    internal bool Contains(Vector3 point)
    {
        if (Mathf.Abs(point.y - _floor) > 2.5f) return false;
        if (_volumes.Count == 0) return (point - Center).sqrMagnitude <= Radius * Radius;
        // Waypoints and path corners lie on the floor, while room triggers start just above it.
        point += Vector3.up * .4f;
        foreach (var volume in _volumes)
            if (volume.Bounds.Contains(volume.Inverse.MultiplyPoint3x4(point))) return true;
        return false;
    }

    internal bool ContainsPath(Vector3[] corners)
    {
        if (corners == null || corners.Length == 0) return false;
        var samples = 0;
        for (var i = 0; i < corners.Length; i++)
        {
            if (!Contains(corners[i])) return false;
            if (i == 0) continue;
            var steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(corners[i - 1], corners[i])));
            if ((samples += steps) > 128) return false;
            for (var n = 1; n < steps; n++)
                if (!Contains(corners[i - 1] + (corners[i] - corners[i - 1]) * (n / (float)steps))) return false;
        }
        return true;
    }
}
