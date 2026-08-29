using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

/// <summary>
/// Raid-scoped registry of BSG's punitive border zones (Minefield and the other BorderZone types).
/// Waypoint generation refuses positions inside them: minefields deal damage by position, inactive
/// bodies included (observed on dormant ghosts three test raids in a row: -50 first blast, -200
/// second, plus bleeds a sleeper cannot treat), and no POI is worth routing bots into one.
/// </summary>
public static class DangerZones
{
    private static BorderZone[] _zones = System.Array.Empty<BorderZone>();
    private static int _skipped;

    /// <summary>Called at raid init (OrbitInitPatch), before the waypoint gatherer runs.</summary>
    public static void Refresh()
    {
        _zones = Object.FindObjectsOfType<BorderZone>();
        _skipped = 0;
        Log.Info($"DangerZones: {_zones.Length} border zone(s) (minefields etc.) on this map");
    }

    public static bool IsInside(Vector3 position)
    {
        var zones = _zones;
        for (var i = 0; i < zones.Length; i++)
        {
            try
            {
                if (zones[i] != null && zones[i].IsInTriggerZone(position))
                {
                    if (++_skipped <= 25)
                        Log.Debug($"DangerZones: {position} is inside {zones[i].name}");
                    return true;
                }
            }
            catch
            {
                // A zone type with exotic trigger math must never break waypoint generation.
            }
        }
        return false;
    }
}
