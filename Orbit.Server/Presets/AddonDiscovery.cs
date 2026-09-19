using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Server.Config;
using Orbit.Server.Zones;

namespace Orbit.Server.Presets;

public static class AddonDiscovery
{
    public static (List<PresetAddon> Addons, List<string> Errors) Scan(string directory, ZoneStoreService zones)
    {
        var addons = new List<PresetAddon>();
        var errors = new List<string>();
        if (!Directory.Exists(directory)) return (addons, errors);
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            var files = Directory.EnumerateFiles(directory, "*.json", options).Order(StringComparer.OrdinalIgnoreCase).Take(257).ToArray();
            if (files.Length > 256)
            {
                errors.Add("Too many addon JSON files (maximum 256). No addons loaded to avoid applying incomplete folders.");
                return (addons, errors);
            }
            var sources = files.Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'));
            foreach (var group in sources.GroupBy(source => source.Contains('/') ? source[..(source.IndexOf('/') + 1)] : source,
                         StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // A folder is loaded as one unit. One invalid file must not apply half an update.
                    var parts = group.Select(source =>
                    {
                        try
                        {
                            var file = Path.Combine(directory, source);
                            if (new FileInfo(file).Length > 5_000_000) throw new InvalidDataException("File exceeds 5 MB.");
                            return Parse(File.ReadAllText(file), source, zones);
                        }
                        catch (Exception ex) { throw new InvalidDataException($"{source}: {ex.Message}", ex); }
                    }).ToArray();
                    addons.Add(group.Key.EndsWith('/') ? Combine(group.Key, parts) : parts[0]);
                }
                catch (Exception ex) { errors.Add($"{group.Key}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { errors.Add($"Cannot scan addon folder: {ex.Message}"); }
        return (addons, errors);
    }

    private static PresetAddon Combine(string source, PresetAddon[] parts)
    {
        JsonObject? config = null;
        var maps = new Dictionary<string, MapZoneModel>();
        // Files are sorted by relative path. Later files override supplied settings and whole maps.
        foreach (var part in parts)
        {
            if (part.Config is { } patch)
            {
                config ??= new JsonObject();
                Merge(config, JsonNode.Parse(patch.GetRawText())!.AsObject());
            }
            foreach (var (map, content) in part.Maps) maps[map] = content;
        }
        return new PresetAddon
        {
            Id = SourceId(source), Name = source.TrimEnd('/'), Source = source,
            Config = config == null ? null : JsonSerializer.SerializeToElement(config), Maps = maps,
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(parts.Select(p => new { p.Source, p.Revision }))))),
            LegacyFileIds = parts.Select(p => p.Id).ToArray(),
        };
    }

    private static string SourceId(string source) => "addon:" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(source.Replace('\\', '/').ToLowerInvariant())))[..24];

    public static PresetAddon Parse(string json, string source, ZoneStoreService zones)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an ORBIT JSON object.");
        var name = Path.GetFileNameWithoutExtension(source);
        if (root.TryGetProperty("Name", out var label) && label.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(label.GetString())) name = label.GetString()!;
        JsonElement? config = null;
        var maps = new Dictionary<string, MapZoneModel>();

        if (root.TryGetProperty("Format", out var format))
        {
            switch (format.GetString())
            {
                case "orbit-preset/1":
                    if (root.TryGetProperty("Config", out var cfg)) config = cfg.Clone();
                    if (root.TryGetProperty("Maps", out var presetMaps)) ReadMaps(presetMaps);
                    break;
                case "orbit-zones/1":
                    if (!root.TryGetProperty("Maps", out var zoneMaps)) throw new InvalidDataException("Zone pack has no maps.");
                    ReadMaps(zoneMaps);
                    break;
                default: throw new InvalidDataException("Unknown addon format or a newer format version.");
            }
        }
        else if (IsConfig(root)) config = root.Clone();
        else if (root.TryGetProperty("Maps", out var legacyMaps)) ReadMaps(legacyMaps);
        else if (root.TryGetProperty("BuiltinZones", out _) || root.TryGetProperty("CustomZones", out _))
        {
            var id = ZoneStoreService.MapIds.FirstOrDefault(id => id.Equals(Path.GetFileNameWithoutExtension(source), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("A single-map file must use its map ID as its filename.");
            maps[id] = zones.NormalizeAddon(id, root.GetRawText());
        }
        else throw new InvalidDataException("Not an ORBIT config, zone pack or preset.");

        if (config.HasValue)
        {
            if (!IsConfig(config.Value)) throw new InvalidDataException("No recognized settings in Config.");
            ConfigService.NormalizeJson(config.Value.GetRawText());
        }
        if (!config.HasValue && maps.Count == 0) throw new InvalidDataException("The addon contains no settings or maps.");
        return new PresetAddon
        {
            Id = SourceId(source),
            Name = name.Trim()[..Math.Min(name.Trim().Length, 80)], Source = source, Config = config, Maps = maps,
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
        };

        void ReadMaps(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Maps must be an object.");
            foreach (var map in value.EnumerateObject())
            {
                var id = ZoneStoreService.MapIds.FirstOrDefault(id => id.Equals(map.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Unknown map '{map.Name}'.");
                maps[id] = zones.NormalizeAddon(id, map.Value.GetRawText());
            }
        }
    }

    private static bool IsConfig(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        using var defaults = JsonDocument.Parse(ConfigService.DefaultJson);
        return value.EnumerateObject().Any(p => p.Name != "config_version" && defaults.RootElement.TryGetProperty(p.Name, out _));
    }

    // Partial settings preserve the unspecified values of the current preset, including nested knobs.
    public static string OverlayConfig(string baseline, JsonElement patch)
    {
        var merged = JsonNode.Parse(baseline)!.AsObject();
        Merge(merged, JsonNode.Parse(patch.GetRawText())!.AsObject());
        return ConfigService.NormalizeJson(merged.ToJsonString());
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
            if (target[key] is JsonObject child && value is JsonObject other) Merge(child, other);
            else target[key] = value?.DeepClone();
    }
}
