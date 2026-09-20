using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

/// <summary>
/// Drops NavMeshObstacles on every minefield + sniper firing zone so bot pathing avoids them entirely. Called
/// once per raid from the BotsController init hook. Without this bots happily wander through map boundaries
/// and die instantly to a script that the navmesh wouldn't otherwise see.
/// </summary>
public static class DangerZoneCarver
{
    // Keep the added world-space box size small so border zones do not cut nearby routes.
    // The actual danger-zone checks and damage wakeups remain active.
    private const float Padding = 1f;

    // A zone whose padded hole would swallow an exfil keeps the pre-2.0 padding. Vehicle extracts sit on
    // the map edge right against a minefield (Shoreline's Road to North V-Ex has one beside and behind it):
    // with 8m the ground under the exfil left the navmesh a few frames after its waypoint was sampled, every
    // path to it came back invalid and no squad ever took the car again (2.0 regression).
    private const float PaddingNearExfil = 1f;

    // Extra clearance around the exfil when testing it against a padded zone: the bot has to walk into the
    // trigger, not merely reach its center.
    private const float ExfilClearance = 2f;

    public static void AddNavmeshCutter()
    {
        var exfils = CollectExfilFootprints();

        var mineZones = Object.FindObjectsByType<Minefield>(FindObjectsSortMode.None);
        Log.Debug($"Creating navmesh carvers for {mineZones.Length} minefields");
        foreach (var zone in mineZones)
        {
            AddCutter(zone.gameObject, exfils);
        }

        var sniperZones = Object.FindObjectsByType<SniperFiringZone>(FindObjectsSortMode.None);
        Log.Debug($"Creating navmesh carvers for {sniperZones.Length} sniper zones");
        foreach (var zone in sniperZones)
        {
            AddCutter(zone.gameObject, exfils);
        }
    }

    private static void AddCutter(GameObject gameObject, List<ExfilFootprint> exfils)
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

        var padding = Padding;
        var crowded = FindExfilInside(collider, Padding, exfils);
        if (crowded != null && padding > PaddingNearExfil)
        {
            padding = PaddingNearExfil;
            Log.Info($"Danger zone {gameObject.name} at {gameObject.transform.position} would carve the navmesh under exfil '{crowded}': keeping the {PaddingNearExfil:F0}m padding there instead of {Padding:F0}m so the exfil stays reachable");
        }

        var colliderScale = collider.transform.lossyScale;
        var worldSize = Vector3.Scale(collider.size, colliderScale) + Vector3.one * padding;
        obstacle.size = new Vector3(worldSize.x / colliderScale.x, worldSize.y / colliderScale.y, worldSize.z / colliderScale.z);
    }

    private readonly struct ExfilFootprint(string name, Vector3[] points)
    {
        public readonly string Name = name;
        public readonly Vector3[] Points = points; // trigger center + the corners of its bounds, world space
    }

    private static List<ExfilFootprint> CollectExfilFootprints()
    {
        var result = new List<ExfilFootprint>();
        try
        {
            foreach (var point in LocationScene.GetAllObjects<ExfiltrationPoint>())
            {
                if (point == null) continue;
                var center = point.transform.position;
                var points = new List<Vector3> { center };
                var trigger = point.GetComponent<Collider>();
                if (trigger != null)
                {
                    var b = trigger.bounds;
                    points.Add(new Vector3(b.min.x, center.y, b.min.z));
                    points.Add(new Vector3(b.min.x, center.y, b.max.z));
                    points.Add(new Vector3(b.max.x, center.y, b.min.z));
                    points.Add(new Vector3(b.max.x, center.y, b.max.z));
                }
                result.Add(new ExfilFootprint(point.Settings?.Name ?? point.name, points.ToArray()));
            }
        }
        catch (System.Exception e)
        {
            Log.Debug($"DangerZoneCarver: could not enumerate exfils ({e.Message}), every zone keeps the {Padding:F0}m padding");
        }
        return result;
    }

    /// <summary>Name of the first exfil with a footprint point inside the zone's box once padded (plus the
    /// walk-in clearance), or null. Tested on the horizontal plane in the collider's own space, so rotated
    /// zones are handled; height is ignored, minefield boxes are tall and exfil triggers hug the ground.</summary>
    private static string FindExfilInside(BoxCollider collider, float padding, List<ExfilFootprint> exfils)
    {
        if (exfils.Count == 0) return null;
        var scale = collider.transform.lossyScale;
        var margin = padding * 0.5f + ExfilClearance;
        var halfX = collider.size.x * 0.5f + margin / Mathf.Max(0.0001f, Mathf.Abs(scale.x));
        var halfZ = collider.size.z * 0.5f + margin / Mathf.Max(0.0001f, Mathf.Abs(scale.z));
        for (var i = 0; i < exfils.Count; i++)
        {
            var points = exfils[i].Points;
            for (var p = 0; p < points.Length; p++)
            {
                var local = collider.transform.InverseTransformPoint(points[p]) - collider.center;
                if (Mathf.Abs(local.x) <= halfX && Mathf.Abs(local.z) <= halfZ) return exfils[i].Name;
            }
        }
        return null;
    }
}
