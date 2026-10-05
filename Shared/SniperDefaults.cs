using System.Collections.Generic;

namespace Orbit.Settings;

public static class SniperDefaults
{
    // Reserve uses a native SPT spawn; Interchange uses the map editor's power-station centres
    // and its recorded ground height. These are survey seeds, not validated firing positions.
    // Elevated seeds require a connected roof NavMesh; they never fall back to ground level.
    public static void AddTo(Dictionary<string, List<RushPoint>> maps)
    {
        Add("RezervBase", new RushPoint { Id = "sniper-dome-hill", Name = "Dome hillside", X = -75.67f, Y = 21.234f, Z = 186.87f, WatchYaw = 180, Radius = 12 });
        Add("Interchange", new RushPoint { Id = "sniper-power-roof", Name = "Power station roof", X = -219.1f, Y = 23.2f, Z = -268.6f, WatchYaw = 45, Radius = 18, Elevated = true });
        Add("Interchange@rework", new RushPoint { Id = "sniper-power-roof", Name = "Power station roof", X = -228f, Y = 23.2f, Z = -352.8f, WatchYaw = 45, Radius = 18, Elevated = true });
        void Add(string map, RushPoint point)
        {
            if (!maps.TryGetValue(map, out var points)) maps[map] = points = new List<RushPoint>();
            if (points.Exists(p => p.Id == point.Id)) return;
            point.Kind = "Sniper";
            points.Add(point);
        }
    }
}
