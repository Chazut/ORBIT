using System.Collections.Generic;

namespace Orbit.Settings;

public static class SniperDefaults
{
    // Authored posts and survey seeds. Runtime still checks navigation and sightlines.
    // Elevated seeds require a connected roof; they never fall back to ground level.
    public static void AddTo(Dictionary<string, List<RushPoint>> maps)
    {
        Add("bigmap", new RushPoint {
            Id = "2f4305d88fee4c2995594b8cef3633ee", Name = "Sniper point", Enabled = true,
            Boss = "bossBully", DoorId = "", X = 195.45055f,
            Y = 6.011f, Z = 172.34886f, LootX = 195.45055f,
            LootY = 6.011f, LootZ = 172.34886f, Radius = 20f,
            SearchSeconds = 25f, Weight = 1f, FloorId = "3rd-floor",
            WatchYaw = 140f, WatchArc = 140f, WatchDistance = 160f,
            WatchPitch = -5f, Elevated = false
        });
        Add("Interchange", new RushPoint {
            Id = "1e7132bc704c48e1acdb02f9e769708a", Name = "Sniper point", Enabled = true,
            Boss = "bossKilla", DoorId = "", X = -174.90305f,
            Y = 21.452f, Z = -348.70786f, LootX = -174.90305f,
            LootY = 21.452f, LootZ = -348.70786f, Radius = 20f,
            SearchSeconds = 25f, Weight = 1f, FloorId = null,
            WatchYaw = 0f, WatchArc = 360f, WatchDistance = 400f,
            WatchPitch = -5f, Elevated = true
        });
        Add("RezervBase", new RushPoint {
            Id = "1455da6c776640439384c1d5aaee4f86", Name = "Sniper point", Enabled = true,
            Boss = "bossGluhar", DoorId = "", X = -7.653855f,
            Y = 32.65f, Z = 167.3848f, LootX = -7.653855f,
            LootY = 32.65f, LootZ = 167.3848f, Radius = 20f,
            SearchSeconds = 25f, Weight = 1f, FloorId = "4th-floor",
            WatchYaw = 175f, WatchArc = 105f, WatchDistance = 400f,
            WatchPitch = -10f, Elevated = false
        });
        Add("Shoreline", new RushPoint {
            Id = "73fd948d52b149b8a0dad648ae5f5b23", Name = "Sniper point", Enabled = true,
            Boss = "bossSanitar", DoorId = "", X = -196.50992f,
            Y = 3f, Z = -80.77049f, LootX = -196.50992f,
            LootY = 3f, LootZ = -80.77049f, Radius = 20f,
            SearchSeconds = 25f, Weight = 1f, FloorId = "3rd-floor",
            WatchYaw = 320f, WatchArc = 360f, WatchDistance = 300f,
            WatchPitch = -5f, Elevated = true
        });
        Add("Shoreline", new RushPoint {
            Id = "3bc9877f14434b6b9d504c463a4dc0d0", Name = "Sniper point copy", Enabled = true,
            Boss = "bossSanitar", DoorId = "", X = -307.17947f,
            Y = 3f, Z = -79.59503f, LootX = -307.17947f,
            LootY = 3f, LootZ = -79.59503f, Radius = 20f,
            SearchSeconds = 25f, Weight = 1f, FloorId = "3rd-floor",
            WatchYaw = 320f, WatchArc = 360f, WatchDistance = 300f,
            WatchPitch = -5f, Elevated = true
        });
        Add("Interchange@rework", new RushPoint {
            Id = "ccd9c3c805594f2494b46a7ee91414cc", Name = "Sniper point", Enabled = true,
            Boss = "bossKilla", DoorId = "", X = -165.069f,
            Y = 21.452f, Z = -346.02585f, LootX = -163.90903f,
            LootY = 21.452f, LootZ = -350.24094f, Radius = 20f,
            SearchSeconds = 25f, Weight = 1f, FloorId = null,
            WatchYaw = 25f, WatchArc = 150f, WatchDistance = 400f,
            WatchPitch = -5f, Elevated = true
        });
        void Add(string map, RushPoint point)
        {
            if (!maps.TryGetValue(map, out var points)) maps[map] = points = new List<RushPoint>();
            if (points.Exists(p => p.Id == point.Id)) return;
            point.Kind = "Sniper";
            points.Add(point);
        }
    }
}
