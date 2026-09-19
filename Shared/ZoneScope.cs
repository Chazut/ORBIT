#nullable disable
using System;
using System.Collections.Generic;

namespace Orbit.Zones;

// Shared JSON contract and floor geometry, compiled into the client and server.
public class ZoneScope
{
    // Missing/null means all types. An empty selection deliberately disables this zone.
    public string[] BotTypes { get; set; }
    // Missing/null means every floor. Named floors use the SPT map variant's catalogue.
    public string FloorId { get; set; }

    public bool Allows(string botType)
        => BotTypes == null || Array.IndexOf(BotTypes, botType) >= 0;

    public ZoneScope Copy() => new ZoneScope
    {
        BotTypes = BotTypes == null ? null : (string[])BotTypes.Clone(),
        FloorId = FloorId,
    };
}

public static class ZoneBotTypes
{
    public static readonly string[] Keys = {
        "PMC", "Scav", "PlayerScav", "Raider", "Rogue", "Boss", "Follower", "Goons",
        "Cultist", "Bloodhound", "UNTAR", "RUAF", "BlackDivision", "ISB", "Combine", "Other"
    };
}

public sealed class FloorExtent
{
    public float MinY;
    public float MaxY;
    // Optional world X/Z rectangles: x1,z1,x2,z2. No rotation is applied to world membership.
    public float[][] Bounds;

    public FloorExtent(float minY, float maxY, params float[][] bounds)
    {
        MinY = minY; MaxY = maxY; Bounds = bounds;
    }

    public bool Contains(float x, float y, float z)
    {
        if (y < MinY || y >= MaxY) return false;
        if (Bounds == null || Bounds.Length == 0) return true;
        foreach (var b in Bounds)
            if (x >= Math.Min(b[0], b[2]) && x <= Math.Max(b[0], b[2])
                && z >= Math.Min(b[1], b[3]) && z <= Math.Max(b[1], b[3])) return true;
        return false;
    }
}

public sealed class MapFloor
{
    public string Id;
    public string Name;
    public string Svg;
    public string TilePath;
    public string[] HideLayers;
    public float[] ImageBounds;
    public FloorExtent[] Extents;

    public bool Contains(float x, float y, float z)
    {
        foreach (var extent in Extents)
            if (extent.Contains(x, y, z)) return true;
        return false;
    }
}

public sealed class FloorMap
{
    public float Rotation;
    public float[] Bounds;
    public float[] Transform;
    public int TileSize = 256;
    public int TileZoom = 3;
    public MapFloor[] Floors;

    public MapFloor Find(string id)
    {
        foreach (var floor in Floors) if (floor.Id == id) return floor;
        return null;
    }

    public string Resolve(float x, float y, float z)
    {
        // More specific upper layers override the broad base and overlapping lower-floor extents.
        for (var i = Floors.Length - 1; i >= 0; i--)
            if (Floors[i].Contains(x, y, z)) return Floors[i].Id;
        return null;
    }

    public bool Matches(string id, float x, float y, float z)
        => FloorSelection.Contains(id, Resolve(x, y, z));
}
