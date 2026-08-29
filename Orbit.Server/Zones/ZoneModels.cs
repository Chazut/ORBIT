using System.Text.Json.Serialization;

namespace Orbit.Server.Zones;

// These models mirror the CLIENT's zone JSON contract byte-for-byte (Orbit/Config/WaypointConfig.cs,
// written by Newtonsoft): PascalCase members, except Vector2 which serializes its lowercase x/y
// fields. Serialized with NO naming policy so the same files round-trip between client and server.

public class ZoneRange
{
    public float Min { get; set; }
    public float Max { get; set; }
}

public class ZoneVec
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
}

public class BuiltinZoneModel
{
    public ZoneRange Radius { get; set; } = new() { Min = 1f, Max = 1f };
    public ZoneRange Force { get; set; } = new() { Min = 1f, Max = 1f };
    public float Decay { get; set; } = 1f;
    // Whether Kills main objectives may anchor on this zone (see client WaypointConfig).
    public bool KillMains { get; set; } = true;
}

public class CustomZoneModel
{
    public ZoneVec Position { get; set; } = new();
    public ZoneRange Radius { get; set; } = new() { Min = 100f, Max = 150f };
    public ZoneRange Force { get; set; } = new() { Min = 0.5f, Max = 1f };
    public float Decay { get; set; } = 1f;
    public bool KillMains { get; set; } = true;
}

public class ConvergenceModel
{
    public ZoneRange Radius { get; set; } = new();
    public ZoneRange Force { get; set; } = new();
    public bool Enabled { get; set; }
}

public class MapZoneModel
{
    public Dictionary<string, BuiltinZoneModel> BuiltinZones { get; set; } = new();
    public List<CustomZoneModel> CustomZones { get; set; } = new();
    // Null = "use the client's compiled-in default for this map" (pre-restore files) — preserved as-is.
    public ConvergenceModel? Convergence { get; set; }
}

/// <summary>
/// Shareable zone pack ("ORBIT addon"): a single JSON file holding zone setups for one or more maps,
/// exported from the zone editor and importable on any other ORBIT server. Published on the Forge.
/// </summary>
public class ZonePackModel
{
    public string Format { get; set; } = "orbit-zones/1";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string OrbitVersion { get; set; } = "";
    public Dictionary<string, MapZoneModel> Maps { get; set; } = new();
}
