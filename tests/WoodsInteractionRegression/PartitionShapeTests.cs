using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

internal static class PartitionShapeTests
{
    internal static void Run(Action<bool,string> check)
    {
        var oldIntersections = Physics.Intersections;
        var collider = new MeshCollider { name = "Static_004", sharedMesh = new Mesh { name = "part_17" } };
        var nextTile = new Collider { name = "000182" };
        // Each case has the same meaningless asset names. Only geometry and walkable space change.
        var normal = new Vector3(0, 0, -1);
        var center = new Vector3(10, 1, 10);
        float halfWidth = 3f, halfHeight = 2f;
        bool tiled = false;
        RaycastHit? Cast(Ray ray, float length, Collider owner, bool neighbour)
        {
            var denom = Vector3.Dot(normal, ray.direction);
            if (denom >= -.0001f) return null;
            var t = Vector3.Dot(center - ray.origin, normal) / denom;
            if (t <= 0 || t >= length) return null;
            var p = ray.origin + ray.direction * t;
            var local = p - center;
            var along = new Vector3(0, -normal.z, normal.y);
            float rise = Vector3.Dot(local, along);
            if (Mathf.Abs(local.x) > halfWidth || Mathf.Abs(rise) > halfHeight) return null;
            if (neighbour != (tiled && Mathf.Abs(rise) > .25f)) return null;
            return new RaycastHit { collider = owner, point = p, normal = normal, distance = t };
        }
        bool CanLoot(InteractableObject target)
        {
            target.transform.position = center - normal - Vector3.up * .1f;
            return LootVisibility.Check(center + normal, target, out _);
        }
        try
        {
            Physics.Intersections = (p, d, l) => Cast(new Ray(p, d), l, collider, false) is { } h ? new[] { h } : Array.Empty<RaycastHit>();
            collider.SurfaceProbe = (ray, len) => Cast(ray, len, collider, false);
            Physics.Probe = (ray, len) => Cast(ray, len, nextTile, true);
            var loot = new InteractableObject();
            check(!CanLoot(loot), "unnamed upright partition");
            check((Physics.LastMask & LayersMaskController.LowPolyColliderLayerMask) != 0
                && (Physics.LastMask & (1 << LayersMaskController.DoorLayer)) != 0, "movement-only panels and live doors are queried");
            tiled = true;
            check(!CanLoot(loot), "neighbouring collision tiles form one wall");
            tiled = false;
            normal = new Vector3(0, .6f, -.8f);
            check(!CanLoot(loot), "sloping unnamed tent panel");
            normal = Vector3.up;
            halfHeight = 3f;
            check(!CanLoot(loot), "two storeys or broad roof");
            halfWidth = .6f;
            check(CanLoot(loot), "small furniture top above loose loot");
            normal = new Vector3(0, 0, -1);
            halfWidth = 3f; halfHeight = .65f;
            check(!CanLoot(loot), "long low unnamed partition");
            halfWidth = .7f; halfHeight = .3f;
            check(CanLoot(loot), "short prop over continuous walkable ground");
            NavMesh.Boundary = (a, b) => center;
            check(!CanLoot(loot), "short irregular divider also cuts the walkable floor");
            NavMesh.Boundary = (a, b) => center + new Vector3(3,0,0);
            check(CanLoot(loot), "navigation edge elsewhere is irrelevant");
            halfWidth = halfHeight = 4f;
            NavMesh.Boundary = null;
            // Both scene casts first encounter a small piece of trim on the SAME mesh.
            Physics.Intersections = (p, d, l) => new[] { new RaycastHit { collider = collider,
                distance = .15f, point = p + d * .15f, normal = new Vector3(1,0,0) } };
            collider.SurfaceProbe = (ray, len) => Cast(ray, len, collider, false);
            check(!CanLoot(loot), "decoration merged into both sides of one collider cannot hide the actual divider");
            var crateRoot = new Transform();
            var crate = new LootableContainer { transform = new Transform { parent = crateRoot } };
            AirdropVisibility.Register(crate, crateRoot);
            collider.transform.parent = crateRoot;
            check(CanLoot(crate), "even a large airdrop shell cannot block its sibling interaction");
        }
        finally
        {
            Physics.Intersections = oldIntersections;
            Physics.Probe = null;
            NavMesh.Boundary = null;
        }
    }
}
