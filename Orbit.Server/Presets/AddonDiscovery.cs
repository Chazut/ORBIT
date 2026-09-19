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
            if (files.Length > 256) errors.Add("Only the first 256 addon JSON files are loaded.");
            foreach (var file in files.Take(256))
            {
                var relative = Path.GetRelativePath(directory, file);
                try
                {
                    if (new FileInfo(file).Length > 5_000_000) throw new InvalidDataException("File exceeds 5 MB.");
                    addons.Add(Parse(File.ReadAllText(file), relative, zones));
                }
                catch (Exception ex) { errors.Add($"{relative}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { errors.Add($"Cannot scan addon folder: {ex.Message}"); }
        return (addons, errors);
    }

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
            Id = "addon:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace('\\', '/').ToLowerInvariant())))[..24],
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

        static void Merge(JsonObject target, JsonObject source)
        {
            foreach (var (key, value) in source)
                if (target[key] is JsonObject child && value is JsonObject other) Merge(child, other);
                else target[key] = value?.DeepClone();
        }
    }
}
