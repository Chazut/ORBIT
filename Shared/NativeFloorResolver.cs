#nullable disable
using System;
using System.Collections.Generic;

namespace Orbit.Zones;

// A native zone may occupy several floors. Its FloorId stores their IDs separated by '|'.
// Custom zones still select one floor, or null for unrestricted height.
public static class FloorSelection
{
    public static bool Contains(string selection, string id)
    {
        if (string.IsNullOrEmpty(selection)) return true;
        if (string.IsNullOrEmpty(id)) return false;
        var start = 0;
        while (start < selection.Length)
        {
            var end = selection.IndexOf('|', start);
            if (end < 0) end = selection.Length;
            if (end - start == id.Length && string.CompareOrdinal(selection, start, id, 0, id.Length) == 0) return true;
            start = end + 1;
        }
        return false;
    }

    public static bool IsKnown(string mapId, string selection)
    {
        if (string.IsNullOrEmpty(selection)) return true;
        var map = FloorCatalog.For(mapId);
        if (map == null) return false;
        foreach (var id in selection.Split('|')) if (map.Find(id) == null) return false;
        return true;
    }

    public static string Label(string mapId, string selection)
    {
        if (string.IsNullOrEmpty(selection)) return "All floors";
        var names = new List<string>();
        foreach (var id in selection.Split('|')) names.Add(FloorCatalog.For(mapId)?.Find(id)?.Name ?? id);
        return string.Join(" / ", names);
    }
}

public readonly struct NativeFloorPoint(float x, float y, float z)
{
    public readonly float X = x, Y = y, Z = z;
}

public static class NativeFloorResolver
{
    public static string Resolve(string mapId, IEnumerable<NativeFloorPoint> points)
    {
        var map = FloorCatalog.For(mapId);
        if (map == null) return null;
        var found = new HashSet<string>();
        foreach (var point in points)
        {
            var id = map.Resolve(point.X, point.Y, point.Z);
            if (id != null) found.Add(id);
        }
        if (found.Count == 0) return null;
        var ordered = new List<string>();
        foreach (var floor in map.Floors) if (found.Contains(floor.Id)) ordered.Add(floor.Id);
        return string.Join("|", ordered);
    }
}
