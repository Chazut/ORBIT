using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orbit.Zones;
using SPT.Common.Http;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private Dictionary<string, string> CollectNativeZoneFloors()
    {
        var result = new Dictionary<string, string>();
        foreach (var zone in _botsController.BotSpawner._allBotZones)
        {
            if (zone == null) continue;
            var points = new List<NativeFloorPoint>();
            foreach (var spawn in zone.SpawnPoints)
            {
                var p = spawn.Position;
                points.Add(new NativeFloorPoint(p.x, p.y, p.z));
            }
            if (zone.PatrolWays != null)
                foreach (var way in zone.PatrolWays)
                {
                    if (way?.Points == null) continue;
                    foreach (var point in way.Points)
                    {
                        if (point == null) continue;
                        var p = point.Position;
                        points.Add(new NativeFloorPoint(p.x, p.y, p.z));
                        for (var i = 0; i < point.SubPointsCount; i++)
                        {
                            var sub = point.GetSubPoint(i);
                            if (sub == null) continue;
                            p = sub.Position;
                            points.Add(new NativeFloorPoint(p.x, p.y, p.z));
                        }
                    }
                }
            var floors = NativeFloorResolver.Resolve(_zoneKey, points);
            result[zone.name] = floors;
            if (floors == null) Log.Warning($"ZONE NATIVE: map={_zoneKey} zone={zone.name} floors=unknown points={points.Count}");
            else Log.Info($"ZONE NATIVE: map={_zoneKey} zone={zone.name} floors={floors} points={points.Count}");
        }
        // Only immutable JSON crosses to the HTTP continuation; no Unity objects on background threads.
        _ = ReportNativeZoneFloors(JsonConvert.SerializeObject(new { MapId = _zoneKey, Floors = result }));
        return result;
    }

    private static async Task ReportNativeZoneFloors(string json)
    {
        try
        {
            await RequestHandler.PostJsonAsync("/orbit/zones/native-floors", json);
        }
        catch (Exception ex)
        {
            Log.Warning($"ZONE NATIVE: editor sync failed: {ex.Message}; in-game native floors remain active");
        }
    }
}
