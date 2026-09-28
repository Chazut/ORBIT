using EFT;
using UnityEngine;

namespace Orbit.Systems;

// Placement is occasional (wake/rescue), never part of Ghost path following. A NavMesh point alone
// does not describe the physical floor or the clearance needed by a capsule on a slope.
internal static class BotGroundPlacement
{
    private static readonly Collider[] Overlaps = new Collider[32];
    private const float FloorRange = 0.65f;

    private struct Body
    {
        internal Collider Collider;
        internal Vector3 Center;
        internal float Radius, HalfSegment, SlopeCos;
        internal int Mask;
    }

    private static bool TryBody(Player player, out Body body)
    {
        body = default;
        var controller = player?.CharacterController;
        var collider = controller?.GetCollider();
        if (collider == null || player.MovementContext == null) return false;
        var transform = collider.transform;
        var scale = transform.lossyScale;
        var radius = controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        var height = controller.height * Mathf.Abs(scale.y);
        if (!Finite(radius) || !Finite(height) || radius < 0.05f || height < radius * 2f
            || Vector3.Dot(transform.up, Vector3.up) < 0.99f) return false;
        // Some impostor controllers have not copied their slope limit yet. Match the movement
        // context's standing/prone limits in that case, instead of treating every slope as a wall.
        var slope = controller.slopeLimit;
        if (!Finite(slope) || slope <= 0f) slope = player.MovementContext.IsInPronePose ? 45f : 60f;
        body = new Body
        {
            Collider = collider,
            Center = transform.TransformPoint(controller.center) - player.Position,
            Radius = radius,
            HalfSegment = height * 0.5f - radius,
            SlopeCos = Mathf.Cos(Mathf.Clamp(slope, 1f, 60f) * Mathf.Deg2Rad),
            Mask = player.MovementContext.GroundMask
        };
        return Finite(body.Center) && body.Mask != 0;
    }

    internal static bool TryResolve(Player player, Vector3 surface, out Vector3 landing, out string reason)
    {
        landing = default;
        reason = "controller-unavailable";
        if (!Finite(surface) || !TryBody(player, out var body)) return false;
        // Short casts stay on the intended floor. Never search from the roof down through a building.
        var origin = new Vector3(surface.x + body.Center.x, surface.y + FloorRange, surface.z + body.Center.z);
        if (!Physics.Raycast(origin, Vector3.down, out var floor, FloorRange * 2f, body.Mask, QueryTriggerInteraction.Ignore))
        { reason = "no-physical-floor"; return false; }
        if (floor.normal.y < body.SlopeCos)
        { reason = "steep-floor"; return false; }
        var radius = body.Radius + 0.02f;
        var sphereStart = new Vector3(origin.x, floor.point.y + radius + FloorRange, origin.z);
        if (Occupied(body, sphereStart, sphereStart, radius))
        { reason = "blocked-probe"; return false; }
        if (!Physics.SphereCast(sphereStart, radius, Vector3.down, out var support, FloorRange * 2f,
                body.Mask, QueryTriggerInteraction.Ignore) || support.normal.y < body.SlopeCos)
        { reason = "unsupported-footprint"; return false; }
        var bottom = sphereStart - Vector3.up * support.distance + Vector3.up * 0.03f;
        landing = bottom - body.Center + Vector3.up * body.HalfSegment;
        if (!Finite(landing) || Mathf.Abs(support.point.y - surface.y) > FloorRange
            || Mathf.Abs(landing.y - surface.y) > FloorRange)
        { reason = "floor-height-mismatch"; return false; }
        if (Occupied(body, bottom, bottom + Vector3.up * (2f * body.HalfSegment), radius))
        { reason = "occupied-capsule"; return false; }
        reason = "supported";
        return true;
    }

    private static bool Occupied(Body body, Vector3 bottom, Vector3 top, float radius)
    {
        var count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, Overlaps, body.Mask, QueryTriggerInteraction.Ignore);
        if (count == Overlaps.Length)
        {
            System.Array.Clear(Overlaps, 0, Overlaps.Length);
            return true; // An incomplete query cannot certify clearance.
        }
        var blocked = false;
        for (var i = 0; i < count; i++)
        {
            var hit = Overlaps[i];
            Overlaps[i] = null;
            if (hit != null && hit != body.Collider && !hit.transform.IsChildOf(body.Collider.transform)) blocked = true;
        }
        return blocked;
    }

    // One short ray, only for awake recovery observations or the brief post-placement watch.
    internal static bool HasSupport(Player player)
    {
        if (player?.MovementContext?.IsGrounded != true || !TryBody(player, out var body)) return false;
        var bottom = player.Position + body.Center - Vector3.up * body.HalfSegment;
        if (!Physics.Raycast(bottom + Vector3.up * 0.15f, Vector3.down, out var hit,
                body.Radius / body.SlopeCos + 0.3f, body.Mask, QueryTriggerInteraction.Ignore)
            || hit.normal.y < body.SlopeCos) return false;
        var gap = bottom.y - hit.point.y - body.Radius / hit.normal.y;
        return gap >= -0.08f && gap <= 0.18f;
    }

    internal static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
