using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EFT.Interactive;
using UnityEngine;

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
        if (FindWall(head, direction, distance, target.transform, out hit)) return false;

        // Some map meshes have one-sided faces. Use the same segment from the other side.
        if (!FindWall(end, -direction, distance, target.transform, out hit)) return true;
        hit.distance = distance - hit.distance;
        return false;
    }

    private static bool FindWall(Vector3 origin, Vector3 direction, float distance, Transform target, out RaycastHit wallHit)
    {
        var hits = scratch;
        var count = Physics.RaycastNonAlloc(origin, direction, hits, distance,
            LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
        // NonAlloc does not guarantee the nearest hits when the buffer fills.
        if (count == hits.Length)
        {
            hits = Physics.RaycastAll(origin, direction, distance,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
            count = hits.Length;
        }
        wallHit = default;
        var nearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            var contact = hits[i];
            var collider = contact.collider;
            if (collider == null || contact.distance <= 0f || contact.distance >= distance || contact.distance >= nearest) continue;
            var surface = surfaces.GetValue(collider, classify).Value;
            if (surface != Surface.Wall && (surface != Surface.Building || !IsPartitionFace(collider, contact))) continue;
            if (collider.transform.IsChildOf(target)) continue;
            nearest = contact.distance;
            wallHit = contact;
        }
        return nearest < float.PositiveInfinity;
    }

    // Maps also batch a room's walls, floor and trim into one ballistic mesh. Only a vertical
    // face that continues overhead on the very same plane is evidence of a partition there.
    private static bool IsPartitionFace(Collider collider, RaycastHit hit)
    {
        if (Mathf.Abs(hit.normal.y) > .25f) return false;
        var probe = new Ray(hit.point + Vector3.up * 1.5f + hit.normal * .1f, -hit.normal);
        return collider.Raycast(probe, out var upper, .2f)
            && Mathf.Abs(upper.distance - .1f) <= .04f && Vector3.Dot(upper.normal, hit.normal) > .95f;
    }

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
