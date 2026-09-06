namespace Orbit.Server.Web;

/// <summary>One map render: svg + rotation + overlay bounds straight from the tarkov.dev interactive
/// map configs (same data raid-review uses). Null svg = no public render, callers draw a grid.</summary>
public sealed record MapView(string? Svg, float Rot, float X1, float Z1, float X2, float Z2);

/// <summary>Map render metadata + world/view transforms shared by the zone editor and the
/// convergence preview on the Zones page.</summary>
public static class MapViews
{
    public static readonly Dictionary<string, MapView> Views = new()
    {
        ["bigmap"] = new("https://assets.tarkov.dev/maps/svg/Customs-Ground_Level.svg", 180, 698, -307, -372, 237),
        ["factory4_day"] = new("https://assets.tarkov.dev/maps/svg/Factory-Ground_Floor.svg", 90, 77, -64.5f, -65.5f, 67.4f),
        ["factory4_night"] = new("https://assets.tarkov.dev/maps/svg/Factory-Ground_Floor.svg", 90, 77, -64.5f, -65.5f, 67.4f),
        ["Sandbox"] = new("https://assets.tarkov.dev/maps/svg/GroundZero-Ground_Level.svg", 180, 249, -124, -99, 364),
        ["Sandbox_high"] = new("https://assets.tarkov.dev/maps/svg/GroundZero-Ground_Level.svg", 180, 249, -124, -99, 364),
        ["Interchange"] = new("https://assets.tarkov.dev/maps/svg/Interchange-Ground_Level.svg", 180, 530, -439, -364, 452),
        ["laboratory"] = new(null, 270, -80, -477, -287, -193),
        ["Labyrinth"] = new(null, 270, -52, -37, 53, 76),
        ["Lighthouse"] = new("https://assets.tarkov.dev/maps/svg/Lighthouse.svg", 180, 515, -998, -545, 725),
        ["RezervBase"] = new("https://assets.tarkov.dev/maps/svg/Reserve-Ground_Level.svg", 180, 289, -293, -303, 244),
        ["Shoreline"] = new("https://assets.tarkov.dev/maps/svg/Shoreline-Ground_Level.svg", 180, 504, -415, -1056, 618),
        ["TarkovStreets"] = new("https://assets.tarkov.dev/maps/svg/StreetsOfTarkov-Ground_Level.svg", 180, 323, -295, -280, 532),
        ["Woods"] = new("https://assets.tarkov.dev/maps/svg/Woods.svg", 180, 646, -914, -761, 442),
    };

    public static (float x, float z) Rot(float x, float z, float deg)
    {
        var a = deg * MathF.PI / 180f;
        var c = MathF.Cos(a);
        var s = MathF.Sin(a);
        return (x * c - z * s, x * s + z * c);
    }
}
