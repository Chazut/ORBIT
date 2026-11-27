using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

internal static class FactoryDrawerTests
{
    // Factory level530, concrete MeshCollider 48931, also present in player collider 50692.
    // Local panel extents and doorway measured from the transformed triangles.
    private static RaycastHit? Intersect(Collider panel, Vector3 from, Vector3 to)
    {
        var delta = to - from;
        if (Math.Abs(delta.z) < .00001f) return null;
        float z = delta.z > 0 ? 36.356f : 36.506f;
        float t = (z - from.z) / delta.z;
        if (t <= 0 || t >= 1) return null;
        var point = from + delta * t;
        if (point.x < 11.128f || point.x > 19.391f || point.y < 8.064f || point.y > 11.385f
            || point.x > 14.487f && point.x < 15.487f && point.y < 10.123f) return null;
        return new RaycastHit { collider = panel, point = point, distance = delta.magnitude * t,
            normal = new Vector3(0, 0, delta.z > 0 ? -1 : 1) };
    }

    internal static void Run(Action<bool, string> check)
    {
        var stand = new Vector3(12.30f, 8.0853f, 37.90f);
        var door = new Vector3(15f, 8.09f, 36.2f);
        var partition = new MeshCollider { name = "Admin_office_Floor_03_BALLISTIC_concrete" };
        partition.SurfaceProbe = ray => Intersect(partition, ray.origin, ray.origin + ray.direction * .2f);
        Physics.SegmentHit = (a, b) => Intersect(partition, a, b);
        try
        {
            foreach (var (id, name, y, feet) in new[] {
                (89, "card_file_box_03", 8.589676f, new Vector3(11.50f, 8.14f, 35.40f)),
                (87, "card_file_box_02", 8.899676f, new Vector3(11.57f, 8.14f, 35.38f)),
                (88, "card_file_box_04", 8.309676f, new Vector3(11.58f, 8.14f, 35.39f)) })
            {
                var center = new Vector3(11.667f, y, 37.1814f);
                var drawer = new LootableContainer { Collider = new Collider { bounds = new Bounds {
                    min = center - Vector3.up * .1f, max = center + Vector3.up * .1f } } };
                foreach (float height in new[] { 1f, 1.7f })
                {
                    check(!LootVisibility.Check(feet + Vector3.up * height, drawer, out var hit) && hit.collider == partition, name + " blocked from Alan's side");
                    check(LootVisibility.Check(stand + Vector3.up * height, drawer, out _), name + " accessible from its own side");
                }
                foreach (bool ghost in new[] { false, true })
                {
                    var scene = new Scenario();
                    scene.Squad.ExtractRequested = false;
                    var point = new Waypoint(id, WaypointCategory.ContainerLoot, name, feet, 1f, new(), new(), drawer);
                    var bot = scene.Add(false, feet, ObjectiveStatus.None, location: point);
                    bot.IsDormant = ghost;
                    NavMesh.Sampler = (seed, range) => stand;
                    NavMesh.Route = (a, b, path) =>
                    {
                        bool valid = a.Equals(feet) && b.Equals(stand);
                        path.status = valid ? NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathPartial;
                        return valid;
                    };
                    scene.Tick(1); scene.Tick(2); scene.Tick(3);
                    check(bot.Objective.Status == ObjectiveStatus.Moving && scene.Movement.Destination.Equals(stand), name + " dispatches a detour");
                    bot.Position = door;
                    scene.Tick(9);
                    check(bot.Objective.Status == ObjectiveStatus.Moving, name + " still moving at the doorway");
                    bot.Position = stand;
                    scene.Tick(14);
                    check(bot.Objective.Status == ObjectiveStatus.Looting, name + " opens only on arrival in the other room");
                }
            }
            check(Intersect(partition, door + Vector3.up, door + new Vector3(0, 1, .55f)) == null, "the baked route crosses the physical doorway");
        }
        finally { Physics.SegmentHit = null; NavMesh.Sampler = null; NavMesh.Route = null; }
    }
}
