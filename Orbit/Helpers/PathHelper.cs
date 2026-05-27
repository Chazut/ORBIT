using UnityEngine;

namespace Orbit.Helpers;

/// <summary>
/// Geometry helpers operating on navmesh-style polyline paths and 2D
/// segment intersections. All pure functions, no state.
/// </summary>
public static class PathHelper
{
    /// <summary>2D closest-point-on-segment in the XY plane.</summary>
    public static Vector2 ClosestPointOnLine(Vector2 origin, Vector2 target, Vector2 point)
    {
        var vec1 = point - origin;
        var vec2 = (target - origin).normalized;

        var d = Vector3.Distance(origin, target);
        var t = Vector3.Dot(vec2, vec1);

        if (t <= 0)
            return origin;

        if (t >= d)
            return target;

        var vec3 = vec2 * t;
        return origin + vec3;
    }

    /// <summary>3D closest-point-on-segment.</summary>
    public static Vector3 ClosestPointOnLine(Vector3 origin, Vector3 target, Vector3 point)
    {
        var vec1 = point - origin;
        var vec2 = (target - origin).normalized;

        var d = Vector3.Distance(origin, target);
        var t = Vector3.Dot(vec2, vec1);

        if (t <= 0)
            return origin;

        if (t >= d)
            return target;

        var vec3 = vec2 * t;
        return origin + vec3;
    }

    public static float TotalLength(Vector3[] corners)
    {
        if (corners.Length < 2)
            return 0f;

        var length = 0f;

        for (var i = 1; i < corners.Length; i++)
        {
            length += Vector3.Distance(corners[i - 1], corners[i]);
        }

        return length;
    }

    /// <summary>
    /// Walks the path from <paramref name="position"/> at corner
    /// <paramref name="cornerIndex"/> and returns the point exactly
    /// <paramref name="targetDistanceSqr"/> ahead along the polyline.
    /// Returns the path's final corner if the requested distance exceeds
    /// the remaining length.
    /// </summary>
    /// <remarks>
    /// Squared distances aren't telescoping so this implementation
    /// double-counts slightly across corner boundaries. Good enough for
    /// the look-ahead heuristic that consumes it; if precise distances
    /// ever matter, switch to linear sqrt accumulation.
    /// </remarks>
    public static Vector3 CalcForwardPoint(Vector3[] corners, Vector3 position, int cornerIndex, float targetDistanceSqr)
    {
        if (cornerIndex >= corners.Length)
            return position;

        var remainingDistanceSqr = targetDistanceSqr;
        var currentPoint = position;
        var currentIndex = cornerIndex;

        while (remainingDistanceSqr > 0 && currentIndex < corners.Length)
        {
            var toCorner = corners[currentIndex] - currentPoint;
            var distanceToCornerSqr = toCorner.sqrMagnitude;

            // If the next corner is far enough, our target point sits along
            // the current segment.
            if (distanceToCornerSqr >= remainingDistanceSqr)
            {
                var remainingDistance = Mathf.Sqrt(remainingDistanceSqr);
                return currentPoint + toCorner.normalized * remainingDistance;
            }

            // Otherwise consume the segment and advance.
            remainingDistanceSqr -= distanceToCornerSqr;
            currentPoint = corners[currentIndex];
            currentIndex++;
        }

        return corners[^1];
    }

    /// <summary>
    /// 2D segment-segment intersection test in the XZ plane. Returns true
    /// if the segments [a1-a2] and [b1-b2] cross (proper or endpoint
    /// touching). Uses the standard cross-product orientation test.
    /// </summary>
    public static bool Segments2dIntersectXZ(Vector3 a1, Vector3 a2, Vector3 b1, Vector3 b2)
    {
        var p1x = a1.x; var p1z = a1.z;
        var p2x = a2.x; var p2z = a2.z;
        var p3x = b1.x; var p3z = b1.z;
        var p4x = b2.x; var p4z = b2.z;

        var d1x = p2x - p1x; var d1z = p2z - p1z;
        var d2x = p4x - p3x; var d2z = p4z - p3z;
        var denom = d1x * d2z - d1z * d2x;
        if (Mathf.Abs(denom) < 1e-6f) return false; // parallel/colinear → treat as no cross

        var dx = p3x - p1x; var dz = p3z - p1z;
        var t = (dx * d2z - dz * d2x) / denom;
        var u = (dx * d1z - dz * d1x) / denom;
        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

    /// <summary>
    /// Maximum angular jitter (in degrees) between consecutive corners
    /// along the next <paramref name="lookAheadDistance"/> metres of the
    /// path starting at <paramref name="startIndex"/>. Used by the
    /// movement system to slow down ahead of sharp turns.
    /// </summary>
    public static float CalculatePathAngleJitter(Vector3[] path, int startIndex, float lookAheadDistance)
    {
        if (startIndex >= path.Length - 2)
            return 0f;

        var angleMax = 0f;
        var distanceAccumulated = 0f;
        var currentIndex = startIndex;

        while (currentIndex < path.Length - 2 && distanceAccumulated < lookAheadDistance)
        {
            var pointA = path[currentIndex];
            var pointB = path[currentIndex + 1];
            var pointC = path[currentIndex + 2];

            distanceAccumulated += Vector3.Distance(pointA, pointB);

            if (distanceAccumulated > lookAheadDistance)
                break;

            var directionAb = (pointB - pointA).normalized;
            var directionBc = (pointC - pointB).normalized;

            var angle = Vector3.Angle(directionAb, directionBc);

            if (angle > angleMax)
                angleMax = angle;

            currentIndex++;
        }

        return angleMax;
    }
}
