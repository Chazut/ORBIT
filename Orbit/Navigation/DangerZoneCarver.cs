using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

/// <summary>
/// Drops NavMeshObstacles on every minefield + sniper firing zone so bot
/// pathing avoids them entirely. Called once per raid from the BotsController
/// init hook. Without this bots happily wander through map boundaries and
/// die instantly to a script that the navmesh wouldn't otherwise see.
/// </summary>
public static class DangerZoneCarver
{
    public static void AddNavmeshCutter()
    {
        var mineZones = Object.FindObjectsByType<Minefield>(FindObjectsSortMode.None);
        Log.Debug($"Creating navmesh carvers for {mineZones.Length} minefields");
        foreach (var zone in mineZones)
        {
            AddCutter(zone.gameObject);
        }

        var sniperZones = Object.FindObjectsByType<SniperFiringZone>(FindObjectsSortMode.None);
        Log.Debug($"Creating navmesh carvers for {sniperZones.Length} sniper zones");
        foreach (var zone in sniperZones)
        {
            AddCutter(zone.gameObject);
        }
    }

    private static void AddCutter(GameObject gameObject)
    {
        var collider = gameObject.GetComponent<BoxCollider>();
        if (collider == null)
        {
            Log.Debug($"Zone {gameObject.name} does not have a collider, skipping");
            return;
        }

        Log.Debug($"Processing zone {gameObject.name}");

        var obstacle = gameObject.AddComponent<NavMeshObstacle>();
        obstacle.carving = obstacle.enabled = collider.enabled;
        obstacle.center = collider.center;

        // Pad by 1m world-space so bots don't graze the collider boundary.
        var colliderScale = collider.transform.lossyScale;
        var worldSize = Vector3.Scale(collider.size, colliderScale) + Vector3.one;
        obstacle.size = new Vector3(worldSize.x / colliderScale.x, worldSize.y / colliderScale.y, worldSize.z / colliderScale.z);
    }
}
