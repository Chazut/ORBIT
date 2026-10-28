using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orbit.Server.Zones;

namespace Orbit.Server.Presets;

/// <summary>A detached selection. Editing this document never touches the working preset.</summary>
public sealed class PresetSelectionReview
{
    internal string StateStamp { get; }
    internal string? AddonRevision { get; }
    internal PresetSnapshot Original { get; }
    public string Id { get; }
    public string Name { get; }
    public PresetParts Parts { get; }
    public bool IsAddon { get; }
    private readonly JsonObject _defaults;
    private JsonObject _draft;

    internal PresetSelectionReview(string id, string name, PresetParts parts, bool isAddon,
        string stateStamp, string? addonRevision, PresetSnapshot original, PresetSnapshot defaults)
    {
        Id = id; Name = name; Parts = parts; IsAddon = isAddon;
        StateStamp = stateStamp; AddonRevision = addonRevision; Original = original;
        _draft = Document(original); _defaults = Document(defaults);
    }

    private static JsonObject Document(PresetSnapshot snapshot) => JsonSerializer.SerializeToNode(snapshot)!.AsObject();
    public PresetSnapshot Snapshot() => _draft.Deserialize<PresetSnapshot>()!;
    public bool Modified => !JsonNode.DeepEquals(_draft, Document(Original));
    public bool CreatesCopy => Modified || Parts != PresetParts.ConfigAndZones;
    public void ResetTweaks() => _draft = Document(Original);

    public IReadOnlyList<PresetReviewField> Fields()
    {
        var result = new List<PresetReviewField>();
        Walk(_draft, _defaults, [], [], true, true, result);
        return result;
    }

    private static void Walk(JsonNode? value, JsonNode? baseline, string[] path, string[] labels,
        bool exists, bool hasDefault, List<PresetReviewField> fields)
    {
        // Versions and identities are implementation details, not preset tuning controls.
        if (path.LastOrDefault() is "config_version" or "SchemaVersion" or "sniper_catalogue_version" or "id" or "Id") return;
        if (!exists)
        {
            fields.Add(new(path, labels, value, baseline, false, hasDefault));
            return;
        }
        if (value is JsonObject obj)
        {
            var previous = baseline as JsonObject;
            foreach (var key in obj.Select(p => p.Key).Concat(previous?.Select(p => p.Key) ?? []).Distinct())
                Walk(obj[key], previous?[key], [.. path, key], [.. labels, Label(key)], obj.ContainsKey(key), previous?.ContainsKey(key) == true, fields);
            return;
        }
        if (value is JsonArray array && (array.Any(n => n is JsonObject) || baseline is JsonArray ba && ba.Any(n => n is JsonObject)))
        {
            var old = baseline as JsonArray ?? [];
            var used = new HashSet<int>();
            for (int i = 0; i < array.Count; i++)
            {
                var identity = Identity(array[i]);
                int match = identity != null ? Enumerable.Range(0, old.Count).FirstOrDefault(j => !used.Contains(j) && Identity(old[j]) == identity, -1)
                    : i < old.Count && !used.Contains(i) && Identity(old[i]) == null ? i : -1;
                if (match >= 0) used.Add(match);
                Walk(array[i], match >= 0 ? old[match] : null, [.. path, i.ToString(CultureInfo.InvariantCulture)],
                    [.. labels, ItemLabel(array[i], i)], true, match >= 0, fields);
            }
            for (int j = 0; j < old.Count; j++)
                if (!used.Contains(j))
                    fields.Add(new([.. path, array.Count.ToString(CultureInfo.InvariantCulture)], [.. labels, ItemLabel(old[j], j)],
                        null, old[j], false, true, $"removed-{j}"));
            return;
        }
        fields.Add(new(path, labels, value, baseline, exists, hasDefault));
    }

    private static string? Identity(JsonNode? node) => node is JsonObject o
        ? (o["id"] ?? o["Id"] ?? o["name"] ?? o["Name"])?.ToString() : null;
    private static string ItemLabel(JsonNode? node, int index) => node is JsonObject o
        ? (o["name"] ?? o["Name"] ?? o["id"] ?? o["Id"])?.ToString() ?? $"Zone {index + 1}" : $"Item {index + 1}";

    public void Set(PresetReviewField field, JsonNode? value)
    {
        JsonNode parent = _draft;
        foreach (var segment in field.Path[..^1])
            parent = parent is JsonArray a ? a[int.Parse(segment, CultureInfo.InvariantCulture)]! : parent[segment]!;
        var key = field.Path[^1];
        if (parent is JsonArray array)
        {
            int index = int.Parse(key, CultureInfo.InvariantCulture);
            if (index == array.Count) array.Add(value?.DeepClone());
            else array[index] = value?.DeepClone();
        }
        else parent[key] = value?.DeepClone();
    }
    public void RestoreDefault(PresetReviewField field)
    {
        if (!field.HasDefault) throw new InvalidOperationException("This entry has no ORBIT default.");
        Set(field, field.Default);
    }

    public static string Label(string key) => ZoneStoreService.MapLabels.TryGetValue(key, out var mapName) ? mapName : key switch
    {
        "Config" => "Settings", "Maps" => "Zones", "ai_limiter" => "Ghost Mode",
        "rush" => "Rush objectives", "maps" => "Rush points", "map_overrides" => "Map overrides",
        "bigmap" => "Customs", "RezervBase" => "Reserve", "sandbox" => "Ground Zero",
        "sandbox_high" => "Ground Zero 21+", "factory4_day" => "Factory (day)", "factory4_night" => "Factory (night)",
        "TarkovStreets" => "Streets", "BuiltinZones" => "Built-in zones", "CustomZones" => "Custom zones",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(key, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ').ToLowerInvariant())
    };
}

public sealed class PresetReviewField(string[] path, string[] labels, JsonNode? value, JsonNode? baseline, bool exists, bool hasDefault, string? discriminator = null)
{
    public string[] Path { get; } = path;
    public string Key => string.Join('/', Path) + (discriminator == null ? "" : "|" + discriminator);
    public string Group => Path[0] == "Maps" ? "Zones / " + labels[1]
        : Path.Length > 3 && Path[1] == "rush" && Path[2] == "maps" ? "Rush points / " + labels[3] : labels[1];
    public string Label => labels[^1];
    public string Context => string.Join(" / ", labels.Skip(Path[0] == "Maps" ? 2 : Group.StartsWith("Rush points / ") ? 4 : 2).SkipLast(1));
    public JsonNode? Value { get; } = value?.DeepClone();
    public JsonNode? Default { get; } = baseline?.DeepClone();
    public bool Exists { get; } = exists;
    public bool HasDefault { get; } = hasDefault;
    public bool Changed => NullableNumber && !HasDefault && Value == null ? false
        : Exists != HasDefault || !JsonNode.DeepEquals(Value, Default);
    public JsonValueKind Kind => Value?.GetValueKind() ?? JsonValueKind.Null;
    public bool NullableNumber => Path.Length > 2 && Path[1] == "map_overrides";
    public bool OptionalText => Path[^1] is "floor_id" or "FloorId" or "Name";
    public string[] Options => Path[^1] switch
    {
        "ghost_fights_mode" => ["simulated", "real", "off"],
        "ghost_awake_behavior" => ["sleep_awake", "wake_ghost"],
        "ghost_fight_frequency" => ["rare", "normal", "frequent"],
        "custom_mode" => ["Search", "Camp", "Loot", "CampThenLoot"],
        "kind" => ["Spawn", "Boss", "Marked", "Sniper", "Custom"],
        _ => [],
    };
    public string DefaultText => !HasDefault ? NullableNumber ? "Inherit (global)" : "Not in Default" : Display(Default);
    public string Text => Display(Value);
    public static string Display(JsonNode? node) => node switch
    {
        null => "Inherit",
        JsonObject => "Included",
        JsonArray a => a.Count == 0 ? "None" : string.Join(", ", a.Select(n => n?.ToString() ?? "Inherit")),
        _ => node.GetValueKind() is JsonValueKind.True ? "On" : node.GetValueKind() is JsonValueKind.False ? "Off" : node.ToString()
    };
}
