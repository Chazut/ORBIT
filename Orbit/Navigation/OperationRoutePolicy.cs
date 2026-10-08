using System;
using Orbit.Zones;
using UnityEngine;

namespace Orbit.Navigation;

internal static class OperationRoutePolicy
{
    private static readonly MapFloor Bunker = FloorCatalog.Maps["RezervBase"].Find("bunkers");
    internal static readonly Func<Vector3, bool> D2 = InD2Bunker;
    internal static bool InD2Bunker(Vector3 point) => Bunker.Contains(point.x, point.y, point.z);

    internal static bool AllowsPath(Vector3[] corners, Func<Vector3, bool> allowed)
    {
        if (allowed == null) return true;
        if (corners == null || corners.Length == 0) return false;
        for (var i = 0; i < corners.Length; i++)
        {
            if (!allowed(corners[i])) return false;
            if (i == 0) continue;
            // Floor bounds vary across the map. Check segments as well as their ends.
            var count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(corners[i - 1], corners[i]) / 2f));
            for (var j = 1; j < count; j++)
                if (!allowed(corners[i - 1] + (corners[i] - corners[i - 1]) * (j / (float)count))) return false;
        }
        return true;
    }
}
