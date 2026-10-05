using Orbit.Settings;
using Orbit.Zones;

namespace Orbit.Server.Web;

public static class RushPointPlacement
{
    private static readonly RushSettings Reference = new();

    public static float SuggestHeight(string mapId, string floorId, float x, float z)
    {
        var map = FloorCatalog.For(mapId);
        var floor = map?.Find(floorId);
        if (floor == null) return 0;

        // Authored defaults provide real nearby heights, including terrain slopes.
        // Never use a reference from another floor or another building's height band.
        var nearest = Reference.Points(mapId)
            .Where(p => map!.Matches(floorId, p.X, p.Y, p.Z) && map.Matches(floorId, x, p.Y, z))
            .OrderBy(p => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z))
            .FirstOrDefault();
        if (nearest != null) return nearest.Y;

        foreach (var extent in floor.Extents)
        {
            // The wide sentinel ranges describe membership, not physical floor height.
            var y = extent.MaxY - extent.MinY < 20 ? (extent.MinY + extent.MaxY) / 2
                : extent.MinY > -1000 ? extent.MinY + 1
                : extent.MaxY < 1000 ? extent.MaxY - 1 : 0;
            if (map!.Matches(floorId, x, y, z)) return y;
        }
        // A click outside this floor's footprint still receives its representative height.
        var reference = Reference.Points(mapId).FirstOrDefault(p => map!.Matches(floorId, p.X, p.Y, p.Z));
        if (reference != null) return reference.Y;
        var first = floor.Extents[0];
        return first.MinY > -1000 ? first.MinY + 1 : first.MaxY < 1000 ? first.MaxY - 1 : 0;
    }
}
