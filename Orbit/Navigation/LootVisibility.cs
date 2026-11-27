using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

/// <summary>Physical loot access uses the object, never its sampled navigation point.</summary>
internal static class LootVisibility
{
    private enum Surface { Other, Wall, Building }
    private sealed class SurfaceInfo(Surface value) { internal readonly Surface Value = value; }
    private static readonly ConditionalWeakTable<Collider, SurfaceInfo> surfaces = new();
    private static readonly ConditionalWeakTable<Collider, SurfaceInfo>.CreateValueCallback classify = Classify;
    private static readonly RaycastHit[] scratch = new RaycastHit[16];

    // Match structural asset identifiers. In particular a wall-mounted lamp or clock is its own asset.
    private const string Start = @"(?:^|[^a-zA-Z]|(?<=[a-z])(?=[A-Z]))";
    private const string End = @"(?=$|[^a-zA-Z]|(?-i:[A-Z]))";
    private const string WallPart = @"(?i:tile|panel|brick|concrete|wood|metal|plaster|glass|ext|int|block|ballistic|collider|collision|lod)";
    private static readonly Regex wall = new(Start + @"(?i:walls?|partitions?|stena)(?:[_ .-]*"
        + WallPart + End + @"|(?=$|[_ .()-]*[0-9]|[ ()]*$))");
    // Prefabs and collision meshes on Woods and Customs. Restrict suffixes to the shell:
    // a tent lamp, wheel, frame or loose container must never turn into a structural wall.
    private static readonly Regex enclosure = new(
        @"^(?:(?:(?:[a-z0-9]+_)*(?:tents?|palatka)[0-9]*|(?:old_|custom_)?trailer|container_(?:6m|12m|20ft|40ft|refrigerator)|custom_container_[0-9]+|prizep_krone_box_liner|pricep_t83090)"
        + @"(?:[_ .-]+(?:[0-9]+|mchs|fabric|wall|panel|side|canvas|metal(?:thin|thick)?|plastic|glass|ball?istic|collider|collision|lod[0-9]+|open|clos(?:e|ed)|door|backdoor|left|right|[lr]|cap|back|front|center|new|update|fixed|prizep|damage|red|blue|green|orange|yellow|white|black|on|adapt|simple|tarcola|uniq|scale|no|only|roof))*"
        + @"[_ ]*(?:\([0-9]+\))?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex accessory = new(@"(?:^|[_ .-])(?:lamp|light|wire[s]?|rope|pole|wheel[s]?|frame|bed|trunk|soil|rubble|decal[s]?|trigger|portal|floor)(?:$|[_ .-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex building = new(Start + @"(?i:room|hallway|corridor|building|factory|hostel)" + End);
    // The Customs warehouse uses generic concrete meshes under this structural prefab.
    // Its combined mesh also includes floor and trim, so still require a partition face.
    private static readonly Regex customsWarehouse = new(@"^sklad_concrete(?: \([0-9]+\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex collisionNode = new(
        @"^(?:ballistic|colliders?|collision|coll|lod[0-9]*|mesh|fabric|plastic|concrete|stone|metal(?:thin|thick)?|wood(?:thin|thick)?)(?:$|[_ .(0-9-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static Vector3 Target(InteractableObject target)
    {
        if (target is LootableContainer container && container.Collider != null)
        {
            var bounds = container.Collider.bounds;
            if (bounds.size.sqrMagnitude > 0f) return bounds.center;
        }
        // A corpse's root remains at the death position while its pelvis can move with the ragdoll.
        return BodyPosition(target) + Vector3.up * .1f;
    }

    internal static Vector3 BodyPosition(InteractableObject target)
    {
        var track = target.TrackableTransform;
        return track != null ? track.position : target.transform.position;
    }

    internal static bool Check(Vector3 head, InteractableObject target, out RaycastHit hit)
    {
        hit = default;
        if (target == null) return false;
        var end = Target(target);
        var direction = end - head;
        var distance = direction.magnitude;
        if (distance < .01f) return true;
        direction /= distance;
        var container = target as LootableContainer;
        var root = container == null ? target.transform : AirdropVisibility.InteractionRoot(container);
        var ownCollider = container == null ? null : container.Collider;
        if (FindWall(head, direction, distance, root, ownCollider, out hit)) return false;

        // Some map meshes have one-sided faces. Use the same segment from the other side.
        if (!FindWall(end, -direction, distance, root, ownCollider, out hit)) return true;
        hit.distance = distance - hit.distance;
        return false;
    }

    private static bool FindWall(Vector3 origin, Vector3 direction, float distance, Transform target, Collider ownCollider, out RaycastHit wallHit)
    {
        var hits = scratch;
        var count = Physics.RaycastNonAlloc(origin, direction, hits, distance,
            BarrierMask, QueryTriggerInteraction.Ignore);
        // NonAlloc does not guarantee the nearest hits when the buffer fills.
        if (count == hits.Length)
        {
            hits = Physics.RaycastAll(origin, direction, distance,
                BarrierMask, QueryTriggerInteraction.Ignore);
            count = hits.Length;
        }
        wallHit = default;
        var nearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            var contact = hits[i];
            var collider = contact.collider;
            if (collider == null || contact.distance <= 0f || contact.distance >= distance || contact.distance >= nearest) continue;
            if (collider == ownCollider || collider.transform.IsChildOf(target)) continue;
            var surface = surfaces.GetValue(collider, classify).Value;
            if (surface != Surface.Wall && !FindPartitionFace(collider, ref contact, target, ownCollider,
                    surface, origin, direction, distance)) continue;
            if (contact.distance >= nearest) continue;
            nearest = contact.distance;
            wallHit = contact;
        }
        return nearest < float.PositiveInfinity;
    }

    private static bool FindPartitionFace(Collider collider, ref RaycastHit contact, Transform target,
        Collider ownCollider, Surface surface, Vector3 origin, Vector3 direction, float distance)
    {
        var end = origin + direction * distance;
        // Unity returns one scene hit per collider. Trim or a prop baked into a combined mesh
        // can hide another face on the very same collider, even when casting from both ends.
        for (int face = 0; face < 8; face++)
        {
            if (IsPartitionFace(collider, contact, target, ownCollider, surface, origin, end)) return true;
            if (!(collider is MeshCollider) || face == 7) break;
            float offset = contact.distance + .005f;
            if (offset >= distance || !collider.Raycast(new Ray(origin + direction * offset, direction),
                    out var next, distance - offset) || next.distance <= 0f || next.distance >= distance - offset) break;
            next.distance += offset;
            contact = next;
        }
        return false;
    }

    private static int BarrierMask => LayersMaskController.HighPolyWithTerrainMask
        | LayersMaskController.LowPolyColliderLayerMask | (1 << LayersMaskController.DoorLayer);

    // Test the extent of the face we actually crossed, not the bounds or name of a whole building.
    // Samples lie in its tangent plane, including for sloping tent panels.
    private static bool IsPartitionFace(Collider collider, RaycastHit hit, Transform target, Collider ownCollider, Surface surface,
        Vector3 start, Vector3 end)
    {
        var n = hit.normal;
        if (n.sqrMagnitude < .9f) return false;
        float vertical = Mathf.Abs(n.y);
        var across = new Vector3(n.z, 0f, -n.x);
        if (across.sqrMagnitude < .0001f) across = new Vector3(1f, 0f, 0f);
        else across /= across.magnitude;
        var along = new Vector3(n.y * across.z - n.z * across.y, n.z * across.x - n.x * across.z,
            n.x * across.y - n.y * across.x);

        bool At(Vector3 offset)
        {
            var origin = hit.point + offset + n * .1f;
            if (collider.Raycast(new Ray(origin, -n), out var sample, .2f) && Coplanar(sample, n)) return true;
            // Adjacent mesh tiles may contain the next part of the same physical panel.
            return Physics.Raycast(origin, -n, out sample, .2f, BarrierMask, QueryTriggerInteraction.Ignore)
                && sample.collider != null && sample.collider != ownCollider && !sample.collider.transform.IsChildOf(target)
                && Coplanar(sample, n);
        }
        bool Either(Vector3 offset) => At(offset) || At(-offset);

        if (vertical < .85f)
        {
            if (Either(along * 1.5f) && (surface == Surface.Building || Either(across * .4f))) return true;
            // Low partitions can be broad enough to divide a space without reaching eye height.
            if (Either(along * .55f) && Either(across * 2.2f)) return true;
            // Short and irregular partitions can still split the walkable floor at this hit.
            return DividesWalkableSpace(start, end, hit);
        }
        // A broad floor or roof separates levels. A bed, tabletop or the lid of a crate
        // should not prevent reaching a loose item underneath it from the same room.
        return Either(across * 2.2f) && Either(along * 2.2f);
    }

    private static bool DividesWalkableSpace(Vector3 start, Vector3 end, RaycastHit contact)
    {
        float height = Mathf.Min(start.y, end.y);
        var a = new Vector3(start.x, height, start.z);
        var b = new Vector3(end.x, height, end.z);
        if (!NavMesh.SamplePosition(a, out var near, 1.6f, NavMesh.AllAreas)
            || !NavMesh.SamplePosition(b, out var far, 1.6f, NavMesh.AllAreas)) return false;
        // A drawer or a key under furniture may project across the wall or onto another floor.
        // Only use the navigation evidence when both projections still represent the endpoints.
        var axis = new Vector3(contact.normal.x, 0f, contact.normal.z);
        if (HorizontalDistanceSqr(near.position, a) > .3f * .3f || HorizontalDistanceSqr(far.position, b) > .3f * .3f
            || Mathf.Abs(near.position.y - far.position.y) > .65f
            || Vector3.Dot(near.position - contact.point, axis) * Vector3.Dot(far.position - contact.point, axis) >= 0f)
            return false;
        return NavMesh.Raycast(near.position, far.position, out var edge, NavMesh.AllAreas)
            && HorizontalDistanceSqr(edge.position, contact.point) <= .75f * .75f;
    }

    private static float HorizontalDistanceSqr(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x, z = a.z - b.z;
        return x * x + z * z;
    }

    private static bool Coplanar(RaycastHit sample, Vector3 normal)
        => Mathf.Abs(sample.distance - .1f) <= .04f && Vector3.Dot(sample.normal, normal) > .95f;

    private static SurfaceInfo Classify(Collider collider)
    {
        var name = collider.name;
        var surface = AssetSurface(name);
        if (surface == Surface.Other && collider is MeshCollider mesh && mesh.sharedMesh != null)
            surface = AssetSurface(mesh.sharedMesh.name);
        // A generic collision node can belong to a named structural prefab. Stop at the
        // first actual asset so a prop does not inherit the identity of an entire building.
        for (var parent = collider.transform.parent; surface == Surface.Other && parent != null
                && collisionNode.IsMatch(name) && !accessory.IsMatch(name); parent = parent.parent)
        {
            name = parent.name;
            surface = AssetSurface(name);
        }
        return new SurfaceInfo(surface);
    }

    private static Surface AssetSurface(string name)
    {
        if (wall.IsMatch(name)) return Surface.Wall;
        if (enclosure.IsMatch(name) && !accessory.IsMatch(name)) return Surface.Wall;
        return building.IsMatch(name) || customsWarehouse.IsMatch(name) ? Surface.Building : Surface.Other;
    }
}

// Shared by every loot continuation, including equipment swaps. Never retain Unity objects.
internal sealed class LootAccessGuard
{
    private int checkedFrame = -1;
    private bool checkedDormant;
    private bool consumed;
    internal void Reset() { checkedFrame = -1; consumed = false; }
    // A successful loose-item transaction removes its world object. Only completion may
    // bypass further visibility checks; cancellation and session ownership still apply.
    internal void CompleteLoosePickup(bool succeeded) { if (succeeded) consumed = true; }
    internal bool Check(int frame, bool dormant, Vector3 head, InteractableObject target, out RaycastHit hit)
    {
        hit = default;
        if (consumed) return true;
        if (target == null) return false;
        if (checkedFrame == frame && checkedDormant == dormant) return true;
        if (!LootVisibility.Check(head, target, out hit)) return false;
        checkedDormant = dormant;
        checkedFrame = frame;
        return true;
    }
}
