using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Orbit.Config;

/// <summary>
/// Per-map navigation tuning: the grid geometry (origin, extent, cell
/// size) that drives waypoint binning, plus the advection zones (positive
/// attractors / negative repellers) that bias squad pull. Backed by JSON
/// files under <c>Config/Maps/</c>; defaults shipped inline so first-run
/// users have a sensible baseline for every supported map.
/// </summary>
public class WaypointConfig
{
    public readonly ConfigBundle<Dictionary<string, MapGeometry>> MapGeometries;
    public readonly Dictionary<string, ConfigBundle<MapZone>> MapZones;

    public WaypointConfig()
    {
        MapGeometries = new ConfigBundle<Dictionary<string, MapGeometry>>("Maps/Geometry.json", Defaults.MapGeometries);
        MapZones = new Dictionary<string, ConfigBundle<MapZone>>();

        foreach (var item in Defaults.MapZones)
        {
            MapZones[item.Key] = new ConfigBundle<MapZone>($"Maps/Zones/{item.Key}.json", item.Value);
        }
    }

    public class MapGeometry(Vector2 min, Vector2 max, float cellSize)
    {
        [JsonRequired] public Vector2 Min { get; set; } = min;
        [JsonRequired] public Vector2 Max { get; set; } = max;
        [JsonRequired] public float CellSize { get; set; } = cellSize;
    }

    public class MapZone(Dictionary<string, BuiltinZone> builtinZones, List<CustomZone> customZones)
    {
        [JsonRequired] public Dictionary<string, BuiltinZone> BuiltinZones { get; set; } = builtinZones;
        [JsonRequired] public List<CustomZone> CustomZones { get; set; } = customZones;
    }

    /// <summary>Zone keyed on a BSG-defined trigger name (e.g. ZoneDormitory).
    /// Position comes from the BSG trigger; we just tune radius/force/decay.</summary>
    public class BuiltinZone(Range radius, Range? force = null, float decay = 1f)
    {
        [JsonRequired] public Range Radius { get; set; } = radius;
        [JsonRequired] public Range Force { get; set; } = force ?? new Range(1f, 1f);
        [JsonRequired] public float Decay { get; set; } = decay;
    }

    /// <summary>Mod-defined zone with hand-picked world position. Used to
    /// add attractors/repellers where BSG didn't put a trigger.</summary>
    public class CustomZone(Vector2 position, Range radius, Range? force = null, float decay = 1f)
    {
        [JsonRequired] public Vector2 Position { get; set; } = position;
        [JsonRequired] public Range Radius { get; set; } = radius;
        [JsonRequired] public Range Force { get; set; } = force ?? new Range(1f, 1f);
        [JsonRequired] public float Decay { get; set; } = decay;
    }

    private static class Defaults
    {
        public static readonly Dictionary<string, MapGeometry> MapGeometries = new()
        {
            { "bigmap", new MapGeometry(new Vector2(-372, -306), new Vector2(698, 235), 75) },
            { "factory4_day", new MapGeometry(new Vector2(-65, -65f), new Vector2(78, 68), 25) },
            { "factory4_night", new MapGeometry(new Vector2(-65, -65f), new Vector2(78, 68), 25) },
            { "Sandbox", new MapGeometry(new Vector2(-99, -124), new Vector2(249, 364), 50) },
            { "Sandbox_high", new MapGeometry(new Vector2(-99, -124), new Vector2(249, 364), 50) },
            { "Interchange", new MapGeometry(new Vector2(-364, -443), new Vector2(534, 452), 75) },
            { "laboratory", new MapGeometry(new Vector2(-292, -441), new Vector2(96, 223), 50) },
            { "Labyrinth", new MapGeometry(new Vector2(-53, -37), new Vector2(51, 76), 25) },
            { "Lighthouse", new MapGeometry(new Vector2(-545, -998), new Vector2(512, 721), 125) },
            { "RezervBase", new MapGeometry(new Vector2(-304, -275), new Vector2(292, 272), 50) },
            { "Shoreline", new MapGeometry(new Vector2(-1060, -415), new Vector2(508, 622), 125) },
            { "TarkovStreets", new MapGeometry(new Vector2(-279, -299), new Vector2(324, 533), 50) },
            { "Woods", new MapGeometry(new Vector2(-756, -915), new Vector2(647, 443), 125) },
        };

        public static readonly Dictionary<string, MapZone> MapZones = new()
        {
            {
                "bigmap", new MapZone(
                    new()
                    {
                        { "ZoneDormitory", new BuiltinZone(new Range(250, 300), new Range(-0.75f, 1.5f)) },
                        { "ZoneScavBase", new BuiltinZone(new Range(350, 400), new Range(-0.75f, 1.5f)) },
                        { "ZoneOldAZS", new BuiltinZone(new Range(100, 150), new Range(-0.25f, 0.25f)) },
                        { "ZoneGasStation", new BuiltinZone(new Range(200, 250), new Range(-0.25f, 0.75f)) },
                    },
                    [
                        new CustomZone(new Vector2(-200, -100), new Range(350, 400), new Range(-0.25f, 0.5f)),
                        new CustomZone(new Vector2(550, 125), new Range(150, 200), new Range(-0.25f, 0.5f))
                    ]
                )
            },
            { "factory4_day", new MapZone([], []) },
            { "factory4_night", new MapZone([], []) },
            { "Sandbox", new MapZone([], []) },
            { "Sandbox_high", new MapZone([], []) },
            {
                "Interchange", new MapZone(
                    new()
                    {
                        { "ZoneCenter", new BuiltinZone(new Range(500, 650), new Range(-0.25f, 1.0f), decay: 0.75f) }
                    },
                    []
                )
            },
            { "laboratory", new MapZone([], []) },
            { "Labyrinth", new MapZone([], []) },
            {
                "Lighthouse", new MapZone(
                    new()
                    {
                        { "Zone_Chalet", new BuiltinZone(new Range(300, 350), new Range(-0.5f, 1.25f)) },
                        { "Zone_Village", new BuiltinZone(new Range(400, 450), new Range(-0.5f, 1.25f)) }
                    },
                    [
                        new CustomZone(new Vector2(0, 475), new Range(500, 600), new Range(-0.25f, 0.75f)),
                        new CustomZone(new Vector2(-55, -775), new Range(400, 450), new Range(-0.25f, 0.75f))
                    ]
                )
            },
            {
                "RezervBase", new MapZone(
                    new()
                    {
                        { "ZoneSubStorage", new BuiltinZone(new Range(300, 350), new Range(-0.25f, 0.5f)) },
                        { "ZoneBarrack", new BuiltinZone(new Range(300, 350), new Range(-0.25f, 0.5f)) }
                    },
                    []
                )
            },
            {
                "Shoreline", new MapZone(
                    new(),
                    [
                        new CustomZone(new Vector2(-250, -100), new Range(500, 600), new Range(-0.25f, 0.75f)),
                        new CustomZone(new Vector2(160, -270), new Range(500, 600), new Range(0f, 0.25f)),
                        new CustomZone(new Vector2(-345, 455), new Range(500, 600), new Range(-0.25f, 0.75f)),
                        new CustomZone(new Vector2(-925, 275), new Range(500, 600), new Range(-0.25f, 0.75f))
                    ]
                )
            },
            { "TarkovStreets", new MapZone([], []) },
            {
                "Woods", new MapZone(
                    [],
                    [
                        new CustomZone(new Vector2(-550, -200), new Range(500, 600), new Range(-0.25f, 0.25f)), // Old Sawmill
                        new CustomZone(new Vector2(0, 0), new Range(700, 800), new Range(-0.35f, 1.25f)),       // New Sawmill
                        new CustomZone(new Vector2(400, 250), new Range(600, 700), new Range(-0.25f, 0.5f)),    // Outskirts
                        new CustomZone(new Vector2(135, -750), new Range(800, 1000), new Range(-0.35f, 1.0f))   // Friendship Bridge
                    ]
                )
            },
        };
    }
}
