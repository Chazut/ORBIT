using System.Collections.Generic;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Helpers;

/// <summary>
/// TEMPORARY verification instrumentation (1.2.0-pre — remove once the carver-open fix is confirmed in raids).
///
/// WaypointSystem.RollForceUnlockForPick now opens ONLY the door's Carver_Closed (via <see cref="DoorNavMesh"/>)
/// instead of unlocking it world-side, so a locked-room loot point becomes routable while the door stays
/// visually Locked until a bot arrives. This logs the navmesh-blocker state (CalculatePath status from the
/// leader's position + the door's self-Obstacle + the three NavMeshDoorLink carvers) immediately BEFORE the
/// carver flip and ~1s AFTER, from the SAME origin. The AFTER snapshot should show <c>state=Locked</c> with
/// <c>Carver_Closed.carving=False</c> and <c>path=PathComplete</c> — confirming the route opens AND the door
/// stays locked AND nothing re-asserts the carver.
/// </summary>
public static class DoorRoutingDiag
{
    private const float AfterDelaySeconds = 1.0f;

    private struct Pending
    {
        public Door Door;
        public NavMeshDoorLink Link;
        public Vector3 Origin;
        public Vector3 Target;
        public string Label;
        public float DueTime;
    }

    private static readonly List<Pending> _pending = new();
    private static readonly NavMeshPath _path = new();

    /// <summary>Snapshot the locked-door state right before WaypointSystem opens its navmesh carver, and queue
    /// the matching post-flip snapshot from the same origin.</summary>
    public static void LogBefore(Squad squad, Waypoint pick, Door door)
    {
        if (door == null) return;
        var leader = squad?.Leader?.Bot;
        var origin = leader != null ? leader.Position : door.transform.position;
        var target = pick != null ? pick.Position : door.transform.position;
        var link = DoorNavMesh.GetLink(door);

        Log.Info($"DoorRoutingDiag[{door.Id}] BEFORE carver-open (state={door.DoorState}) {pick}: {Snapshot(door, link, origin, target)}");

        _pending.Add(new Pending
        {
            Door = door,
            Link = link,
            Origin = origin,
            Target = target,
            Label = pick != null ? pick.ToString() : door.Id,
            DueTime = Time.time + AfterDelaySeconds
        });
    }

    /// <summary>Drained every frame from WaypointSystem.Update — emits the post-flip snapshot once the delay
    /// elapses so the navmesh carve bake has settled.</summary>
    public static void Tick()
    {
        if (_pending.Count == 0) return;
        var now = Time.time;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (now < p.DueTime) continue;
            if (p.Door != null)
                Log.Info($"DoorRoutingDiag[{p.Door.Id}] AFTER carver-open (+{AfterDelaySeconds:F1}s, state={p.Door.DoorState}) {p.Label}: {Snapshot(p.Door, p.Link, p.Origin, p.Target)}");
            _pending.RemoveAt(i);
        }
    }

    private static string Snapshot(Door door, NavMeshDoorLink link, Vector3 origin, Vector3 target)
    {
        var ok = NavMesh.CalculatePath(origin, target, NavMesh.AllAreas, _path);
        var pathStatus = ok ? _path.status.ToString() : "NoPath";

        var obstacle = door.Obstacle;
        var obs = obstacle != null
            ? $"selfObstacle(enabled={obstacle.enabled}, carving={obstacle.carving}, shape={obstacle.shape})"
            : "selfObstacle(null)";

        var carvers = link != null
            ? $"Carver_Closed({CarverStr(link.Carver_Closed)}) Carver_Opened({CarverStr(link.Carver_Opened)}) Carver_Breached({CarverStr(link.Carver_Breached)})"
            : "navMeshDoorLink(none)";

        return $"path={pathStatus} | {obs} | {carvers}";
    }

    private static string CarverStr(NavMeshObstacle o)
        => o != null ? $"carving={o.carving}, enabled={o.enabled}" : "null";
}
