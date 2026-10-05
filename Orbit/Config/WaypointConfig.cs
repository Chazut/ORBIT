using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using Orbit.Zones;

namespace Orbit.Config;

/// <summary>
/// Per-map navigation tuning: the grid geometry (origin, extent, cell size) that drives waypoint binning,
/// plus the advection zones (positive attractors / negative repellers) that bias squad pull. Backed by JSON
/// files under <c>Config/Maps/</c>; defaults shipped inline so first-run users have a sensible baseline for
/// every supported map.
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

    public class MapZone(Dictionary<string, BuiltinZone> builtinZones, List<CustomZone> customZones, Convergence convergence = null)
    {
        [JsonRequired] public Dictionary<string, BuiltinZone> BuiltinZones { get; set; } = builtinZones;
        [JsonRequired] public List<CustomZone> CustomZones { get; set; } = customZones;
        // Deliberately NOT JsonRequired: zone JSONs written before the player-convergence restore lack
        // this key. Null means "use the compiled-in default for this map" (resolved in WaypointSystem)
        // so pre-existing files don't fail to load or silently disable the feature.
        public Convergence Convergence { get; set; } = convergence;
    }

    /// <summary>
    /// Player-convergence force: every cell of the grid receives a pull toward the living human
    /// player(s), scaled by inverse distance within Radius. Keeps the world drifting toward where the
    /// action actually is without scripting anything. Radius/Force are sampled once per raid.
    /// </summary>
    public class Convergence(Range? radius = null, Range? force = null, bool enabled = true)
    {
        [JsonRequired] public Range Radius { get; set; } = radius ?? new Range(0f, 0f);
        [JsonRequired] public Range Force { get; set; } = force ?? new Range(0f, 0f);
        [JsonRequired] public bool Enabled { get; set; } = enabled;

        public Convergence() : this(enabled: false)
        {
        }
    }

    /// <summary>Compiled-in convergence default for a map — the fallback when a zone JSON predates the
    /// convergence key. Variant keys ("Interchange@rework") fall back to the base id; unknown ids resolve
    /// to disabled.</summary>
    public static Convergence DefaultConvergenceFor(string zoneKey, string mapId = null)
    {
        if (Defaults.MapZones.TryGetValue(zoneKey, out var zone) && zone.Convergence != null) return zone.Convergence;
        if (mapId != null && mapId != zoneKey && Defaults.MapZones.TryGetValue(mapId, out zone) && zone.Convergence != null) return zone.Convergence;
        return new Convergence();
    }

    /// <summary>Zone bundle for a variant key, then the base id. Unknown ids get an empty set backed by
    /// its own file, so a map mod with a new location id runs on a plain grid instead of throwing.</summary>
    public ConfigBundle<MapZone> ResolveZones(string zoneKey, string mapId)
    {
        if (MapZones.TryGetValue(zoneKey, out var bundle) || MapZones.TryGetValue(mapId, out bundle)) return bundle;
        Log.Warning($"No zone defaults for map '{mapId}': starting with an empty zone set (Config/Maps/Zones/{mapId}.json)");
        bundle = new ConfigBundle<MapZone>($"Maps/Zones/{mapId}.json", new MapZone(new Dictionary<string, BuiltinZone>(), [], new Convergence()));
        MapZones[mapId] = bundle;
        return bundle;
    }

    /// <summary>Grid geometry for a variant key, then the base id. Unknown ids get a 75m grid that the
    /// waypoint system fits to the waypoints it finds in the scene.</summary>
    public MapGeometry ResolveGeometry(string zoneKey, string mapId)
    {
        var geometries = MapGeometries.Value;
        if (geometries.TryGetValue(zoneKey, out var geometry) || geometries.TryGetValue(mapId, out geometry)) return geometry;
        Log.Warning($"No grid geometry for map '{mapId}': using a 75m grid fitted to the scene's waypoints");
        return new MapGeometry(Vector2.zero, Vector2.zero, 75f);
    }

    /// <summary>Zone keyed on a BSG-defined trigger name (e.g. ZoneDormitory).
    /// Position comes from the BSG trigger; we just tune radius/force/decay.</summary>
    public class BuiltinZone(Range radius, Range? force = null, float decay = 1f, bool killMains = true) : ZoneScope
    {
        // Use property defaults for omitted optional JSON members, including pre-editor zone files.
        public BuiltinZone() : this(new Range(1f, 1f)) { }
        [JsonRequired] public Range Radius { get; set; } = radius;
        [JsonRequired] public Range Force { get; set; } = force ?? new Range(1f, 1f);
        [JsonRequired] public float Decay { get; set; } = decay;
        // Deliberately NOT JsonRequired (files predating the flag default to true): whether Kills main
        // objectives may anchor on this zone. OFF = pure routing hotspot, never a kill arena.
        public bool KillMains { get; set; } = killMains;
    }

    /// <summary>Mod-defined zone with hand-picked world position. Used to
    /// add attractors/repellers where BSG didn't put a trigger.</summary>
    public class CustomZone(Vector2 position, Range radius, Range? force = null, float decay = 1f, bool killMains = true) : ZoneScope
    {
        public CustomZone() : this(Vector2.zero, new Range(100f, 150f)) { }
        [JsonRequired] public Vector2 Position { get; set; } = position;
        [JsonRequired] public Range Radius { get; set; } = radius;
        [JsonRequired] public Range Force { get; set; } = force ?? new Range(1f, 1f);
        [JsonRequired] public float Decay { get; set; } = decay;
        // See BuiltinZone.KillMains.
        public bool KillMains { get; set; } = killMains;
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
            // Interchange Rework (LennoxP90): the 1.0 layout, bounds from the tarkov.dev interactive map.
            { "Interchange@rework", new MapGeometry(new Vector2(-433, -442), new Vector2(598, 426), 75) },
            { "laboratory", new MapGeometry(new Vector2(-292, -441), new Vector2(96, 223), 50) },
            { "Labyrinth", new MapGeometry(new Vector2(-53, -37), new Vector2(51, 76), 25) },
            { "Lighthouse", new MapGeometry(new Vector2(-545, -998), new Vector2(512, 721), 125) },
            // Lighthouse 1.0 backport (Manimal): same footprint as vanilla.
            { "Lighthouse@rework", new MapGeometry(new Vector2(-545, -998), new Vector2(512, 721), 125) },
            { "RezervBase", new MapGeometry(new Vector2(-304, -275), new Vector2(292, 272), 50) },
            { "Shoreline", new MapGeometry(new Vector2(-1060, -415), new Vector2(508, 622), 125) },
            { "TarkovStreets", new MapGeometry(new Vector2(-279, -299), new Vector2(324, 533), 50) },
            { "Woods", new MapGeometry(new Vector2(-756, -915), new Vector2(647, 443), 125) },
        };

        // Keep these fallback zones aligned with Orbit.Server/Resources/Zones.
        public static readonly Dictionary<string, MapZone> MapZones = new()
        {
            {
                "bigmap", new MapZone(
                    new()
                    {
                        { "ZoneDormitory", new BuiltinZone(new Range(250f, 300f), new Range(-0.75f, 1.5f)) { FloorId = "base|2nd-floor|3rd-floor" } },
                        { "ZoneScavBase", new BuiltinZone(new Range(350f, 400f), new Range(-0.75f, 1.5f)) { FloorId = "base|2nd-floor" } },
                        { "ZoneOldAZS", new BuiltinZone(new Range(100f, 150f), new Range(-0.25f, 0.5f)) { FloorId = "base" } },
                        { "ZoneGasStation", new BuiltinZone(new Range(200f, 250f), new Range(-0.25f, 0.75f)) { FloorId = "base" } },
                    },
                    [
                        new CustomZone(new Vector2(-200f, -100f), new Range(350f, 400f), new Range(-0.25f, 0.5f)),
                        new CustomZone(new Vector2(550f, 125f), new Range(150f, 200f), new Range(-0.25f, 0.5f)),
                    ],
                    new Convergence(new Range(200f, 400f), new Range(0f, 0.5f), enabled: true)
                )
            },
            {
                "factory4_day", new MapZone(
                    new()
                    {
                    },
                    [
                    ],
                    new Convergence(new Range(0f, 0f), new Range(0f, 0f), enabled: false)
                )
            },
            {
                "factory4_night", new MapZone(
                    new()
                    {
                    },
                    [
                    ],
                    new Convergence(new Range(0f, 0f), new Range(0f, 0f), enabled: false)
                )
            },
            {
                "Interchange", new MapZone(
                    new()
                    {
                        { "ZoneCenterBot", new BuiltinZone(new Range(500f, 650f), new Range(-0.25f, 1f), decay: 0.8f) { FloorId = "2nd-floor|3rd-floor" } },
                    },
                    [
                        // Power
                        new CustomZone(new Vector2(-203f, -352f), new Range(179f, 519f), new Range(-0.25f, 0.75f)),
                        // Loot
                        new CustomZone(new Vector2(81f, 54f), new Range(150f, 250f), new Range(0f, 1f)) { FloorId = "3rd-floor" },
                    ],
                    new Convergence(new Range(300f, 500f), new Range(0f, 0.5f), enabled: true)
                )
            },
            {
                "Interchange@rework", new MapZone(
                    new()
                    {
                        { "ZoneCenterBot", new BuiltinZone(new Range(500f, 650f), new Range(-0.25f, 1f), decay: 0.8f) { FloorId = "2nd-floor|3rd-floor" } },
                        { "ZoneBearCamp", new BuiltinZone(new Range(350f, 600f), new Range(-0.5f, 1.5f)) { FloorId = "base" } },
                    },
                    [
                        // Power
                        new CustomZone(new Vector2(-203f, -352f), new Range(179f, 519f), new Range(-0.25f, 0.75f)),
                        // Loot
                        new CustomZone(new Vector2(81f, 54f), new Range(150f, 250f), new Range(0f, 1f)) { FloorId = "3rd-floor" },
                        // Camp
                        new CustomZone(new Vector2(-122f, 237f), new Range(150f, 300f), new Range(-0.5f, 1f)),
                        // Camp2
                        new CustomZone(new Vector2(-327f, -52f), new Range(150f, 300f), new Range(-0.5f, 1f)),
                    ],
                    new Convergence(new Range(300f, 500f), new Range(0f, 0.5f), enabled: true)
                )
            },
            {
                "laboratory", new MapZone(
                    new()
                    {
                    },
                    [
                    ],
                    new Convergence(new Range(0f, 0f), new Range(0f, 0f), enabled: false)
                )
            },
            {
                "Labyrinth", new MapZone(
                    new()
                    {
                    },
                    [
                    ],
                    new Convergence(new Range(0f, 0f), new Range(0f, 0f), enabled: false)
                )
            },
            {
                "Lighthouse", new MapZone(
                    new()
                    {
                        { "Zone_Chalet", new BuiltinZone(new Range(300f, 350f), new Range(-0.5f, 1.25f)) { FloorId = "base", BotTypes = ["PMC"] } },
                        { "Zone_Village", new BuiltinZone(new Range(400f, 450f), new Range(-0.5f, 1.25f)) { FloorId = "base" } },
                    },
                    [
                        // Snip
                        new CustomZone(new Vector2(-65f, 448f), new Range(500f, 600f), new Range(-0.25f, 0.75f)) { BotTypes = ["PMC"] },
                        // Base
                        new CustomZone(new Vector2(-61f, -581f), new Range(200f, 450f), new Range(-0.25f, 0.75f)),
                        // Scav camp
                        new CustomZone(new Vector2(11f, -19f), new Range(250f, 600f), new Range(0.5f, 1.5f), decay: 0.5f) { BotTypes = ["Scav"] },
                    ],
                    new Convergence(new Range(250f, 1000f), new Range(0.25f, 0.75f), enabled: true)
                )
            },
            {
                "Lighthouse@rework", new MapZone(
                    new()
                    {
                        { "Zone_Chalet", new BuiltinZone(new Range(300f, 350f), new Range(-0.5f, 1.25f)) { FloorId = "base", BotTypes = ["PMC"] } },
                        { "Zone_Village", new BuiltinZone(new Range(400f, 450f), new Range(-0.5f, 1.25f)) { FloorId = "base" } },
                    },
                    [
                        // Snip
                        new CustomZone(new Vector2(-65f, 448f), new Range(500f, 600f), new Range(-0.25f, 0.75f)) { BotTypes = ["PMC"] },
                        // Base
                        new CustomZone(new Vector2(-61f, -581f), new Range(200f, 450f), new Range(-0.25f, 0.75f)),
                        // Scav camp
                        new CustomZone(new Vector2(11f, -19f), new Range(250f, 600f), new Range(0.5f, 1.5f), decay: 0.5f) { BotTypes = ["Scav"] },
                    ],
                    new Convergence(new Range(250f, 1000f), new Range(0.25f, 0.75f), enabled: true)
                )
            },
            {
                "RezervBase", new MapZone(
                    new()
                    {
                        { "ZoneSubStorage", new BuiltinZone(new Range(100f, 250f), new Range(-0.25f, 0.5f)) { FloorId = "bunkers", BotTypes = ["PMC", "Scav", "PlayerScav"] } },
                        { "ZoneBarrack", new BuiltinZone(new Range(100f, 350f), new Range(-0.25f, 0.75f)) { FloorId = "base|2nd-floor|3rd-floor", BotTypes = ["PMC", "Scav", "PlayerScav"] } },
                        { "ZoneSubCommand", new BuiltinZone(new Range(100f, 400f), new Range(-0.25f, 1.25f), decay: 0.8f) { FloorId = "bunkers" } },
                    },
                    [
                        new CustomZone(new Vector2(-6f, 173f), new Range(100f, 400f), new Range(-0.5f, 1f), decay: 1.2f),
                    ],
                    new Convergence(new Range(0f, 0f), new Range(0f, 0f), enabled: false)
                )
            },
            {
                "Sandbox", new MapZone(
                    new()
                    {
                    },
                    [
                    ],
                    new Convergence(new Range(0f, 0f), new Range(0f, 0f), enabled: false)
                )
            },
            {
                "Sandbox_high", new MapZone(
                    new()
                    {
                    },
                    [
                    ],
                    new Convergence(new Range(0f, 0f), new Range(0f, 0f), enabled: false)
                )
            },
            {
                "Shoreline", new MapZone(
                    new()
                    {
                        { "ZoneMeteoStation", new BuiltinZone(new Range(150f, 500f), new Range(-0.25f, 0.75f)) { FloorId = "base" } },
                        { "ZoneSmuglers", new BuiltinZone(new Range(150f, 500f), new Range(-0.25f, 0.75f)) { FloorId = "base" } },
                    },
                    [
                        // Resort
                        new CustomZone(new Vector2(-250f, -100f), new Range(300f, 800f), new Range(-0.25f, 0.75f)),
                        // Boat
                        new CustomZone(new Vector2(-316f, 484f), new Range(200f, 600f), new Range(-0.25f, 0.75f)),
                        // Villas
                        new CustomZone(new Vector2(128f, 112f), new Range(150f, 500f), new Range(-0.25f, 0.75f)),
                    ],
                    new Convergence(new Range(750f, 1500f), new Range(0.25f, 0.75f), enabled: true)
                )
            },
            {
                "TarkovStreets", new MapZone(
                    new()
                    {
                        { "ZoneCard1", new BuiltinZone(new Range(150f, 250f), new Range(-0.25f, 0.75f)) { FloorId = "base" } },
                        { "ZoneCarShowroom", new BuiltinZone(new Range(150f, 250f), new Range(-0.25f, 0.75f)) { FloorId = "base" } },
                        { "ZoneColumn", new BuiltinZone(new Range(150f, 250f), new Range(-0.25f, 0.75f)) { FloorId = "base" } },
                        { "ZoneFactory", new BuiltinZone(new Range(150f, 250f), new Range(-0.25f, 0.75f)) { FloorId = "base" } },
                    },
                    [
                    ],
                    new Convergence(new Range(150f, 300f), new Range(0f, 0.5f), enabled: true)
                )
            },
            {
                "Woods", new MapZone(
                    new()
                    {
                    },
                    [
                        // Old Sawmill
                        new CustomZone(new Vector2(-550f, -200f), new Range(300f, 600f), new Range(-0.25f, 0.75f)),
                        // Sawmill
                        new CustomZone(new Vector2(0f, 0f), new Range(400f, 800f), new Range(-0.35f, 1.25f), decay: 0.8f),
                        // HouseCamping
                        new CustomZone(new Vector2(400f, 250f), new Range(300f, 700f), new Range(-0.25f, 0.75f)),
                        // Scav base
                        new CustomZone(new Vector2(142f, -736f), new Range(300f, 600f), new Range(-0.25f, 0.75f)),
                        // Usec Base
                        new CustomZone(new Vector2(294f, -450f), new Range(300f, 600f), new Range(-0.25f, 0.75f)),
                        // Village
                        new CustomZone(new Vector2(-470f, -379f), new Range(300f, 600f), new Range(-0.25f, 0.75f)),
                        // Lootspot
                        new CustomZone(new Vector2(-196f, 221f), new Range(300f, 600f), new Range(-0.25f, 0.75f)),
                    ],
                    new Convergence(new Range(500f, 1250f), new Range(0.25f, 0.75f), enabled: true)
                )
            },
        };
    }
}
