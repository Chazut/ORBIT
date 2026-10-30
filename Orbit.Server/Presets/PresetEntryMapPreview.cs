using System.Globalization;
using System.Text.Json.Nodes;
using Orbit.Server.Zones;
using Orbit.Zones;

namespace Orbit.Server.Presets;

public sealed record PresetMapShape(float X, float Z, float MinRadius, float MaxRadius, string? FloorId, string Label);

/// <summary>Detached geometry for one selected review entry, never the whole preset.</summary>
public sealed record PresetEntryMapPreview(IReadOnlyList<PresetMapShape> Default, IReadOnlyList<PresetMapShape> Draft)
{
    public bool HasGeometry => Default.Count + Draft.Count > 0;
    public bool SameFootprint => Default.Count > 0 && Default.SequenceEqual(Draft);

    public static PresetEntryMapPreview Create(string mapId, PresetReviewEntry entry)
        => new(Shapes(mapId, entry, Document(entry, true)), Shapes(mapId, entry, Document(entry, false)));

    private static JsonObject? Document(PresetReviewEntry entry, bool baseline)
    {
        var fields = entry.Fields.Where(f => baseline ? f.HasDefault : f.Exists).ToArray();
        if (fields.Length == 0) return null;
        var depth = PresetReviewSummary.EntryDepth(fields[0]);
        // A removed zone/point is a complete default object, with no draft counterpart.
        if (fields[0].Path.Length == depth)
            return (baseline ? fields[0].Default : fields[0].Value)?.DeepClone() as JsonObject;
        var result = new JsonObject();
        foreach (var field in fields)
        {
            var path = field.Path.Skip(depth).ToArray();
            if (path.Length == 0) continue;
            var parent = result;
            foreach (var segment in path[..^1])
            {
                if (parent[segment] is not JsonObject) parent[segment] = new JsonObject();
                parent = parent[segment]!.AsObject();
            }
            parent[path[^1]] = (baseline ? field.Default : field.Value)?.DeepClone();
        }
        return result;
    }

    private static IReadOnlyList<PresetMapShape> Shapes(string mapId, PresetReviewEntry entry, JsonObject? doc)
    {
        if (doc == null) return [];
        var floor = (doc["FloorId"] ?? doc["floor_id"])?.ToString();
        if (entry.Category == "Rush point")
        {
            var x = Number(doc["x"]); var z = Number(doc["z"]); var radius = Number(doc["radius"]);
            if (x == null || z == null || radius is null or < 0) return [];
            if (string.IsNullOrWhiteSpace(floor) && Number(doc["y"]) is { } y)
                floor = FloorCatalog.For(mapId)?.Resolve(x.Value, y, z.Value);
            var marked = doc["kind"]?.ToString() == "Marked";
            var result = new List<PresetMapShape> { new(x.Value, z.Value, radius.Value, radius.Value, floor, marked ? "Door" : "Rush point") };
            if (marked && Number(doc["loot_x"]) is { } lx && Number(doc["loot_z"]) is { } lz && Number(doc["loot_radius"]) is >= 0 and { } lr)
                result.Add(new(lx, lz, lr, lr, floor, "Loot area"));
            return result;
        }
        float? px, pz;
        if (entry.Category == "Built-in zone")
        {
            if (!BuiltinZonePositions.TryGet(mapId, entry.Fields[0].Path[3], out var position)) return [];
            (px, pz) = (position.X, position.Z);
        }
        else if (entry.Category == "Custom zone")
        {
            px = Number(doc["Position"]?["x"]); pz = Number(doc["Position"]?["y"]);
        }
        else return []; // Convergence follows the player; it has no fixed world position.
        var min = Number(doc["Radius"]?["Min"]); var max = Number(doc["Radius"]?["Max"]);
        if (px == null || pz == null || min is null or < 0 || max is null or < 0 || min > max) return [];
        return [new(px.Value, pz.Value, min.Value, max.Value, floor, "Zone")];
    }

    private static float? Number(JsonNode? node) => float.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
        && float.IsFinite(n) ? n : null;
}
