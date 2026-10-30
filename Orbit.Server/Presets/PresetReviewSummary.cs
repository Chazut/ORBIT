namespace Orbit.Server.Presets;

/// <summary>Groups the detached review by settings section and complete map entries.</summary>
public sealed class PresetReviewSummary
{
    public IReadOnlyList<PresetReviewSection> Settings { get; }
    public IReadOnlyList<PresetReviewMap> Maps { get; }
    public int SettingChanges => Settings.Sum(s => s.Changes);
    public int MapChanges => Maps.Count(m => m.Changes > 0);
    public int EntryChanges => Maps.Sum(m => m.Changes);

    public PresetReviewSummary(IReadOnlyList<PresetReviewField> fields)
    {
        Settings = fields.Where(f => MapId(f) == null).GroupBy(f => f.Group)
            .Select(g => new PresetReviewSection(g.Key, g.ToArray())).OrderBy(s => s.Name).ToArray();
        Maps = fields.Where(f => MapId(f) != null && !EmptyCollection(f)).GroupBy(f => MapId(f)!)
            .Select(map => new PresetReviewMap(map.Key, map.GroupBy(EntryKey)
                .Select(g => new PresetReviewEntry(g.Key, g.ToArray())).OrderBy(e => e.Category).ThenBy(e => e.Name).ToArray()))
            .OrderBy(m => m.Name).ToArray();
    }

    private static string? MapId(PresetReviewField f) => f.Path[0] == "Maps" ? f.Path[1]
        : f.Path.Length >= 4 && f.Path[1] == "rush" && f.Path[2] == "maps" ? f.Path[3] : null;

    internal static int EntryDepth(PresetReviewField f) => f.Path[0] == "Config" ? 5
        : f.Path.Length >= 3 && f.Path[2] is "BuiltinZones" or "CustomZones" ? 4 : 3;

    // Empty point/zone arrays are leaf fields in the editor, not actual map entries.
    private static bool EmptyCollection(PresetReviewField f) => f.Path.Length < EntryDepth(f)
        && f.Value is System.Text.Json.Nodes.JsonArray { Count: 0 };

    private static string EntryKey(PresetReviewField f)
    {
        var depth = Math.Min(EntryDepth(f), f.Path.Length);
        // Removed array entries share the insertion index but have different identities.
        return string.Join('/', f.Path.Take(depth)) + (!f.Exists && f.Path.Length == depth ? "|" + f.Key : "");
    }
}

public sealed record PresetReviewSection(string Name, IReadOnlyList<PresetReviewField> Fields)
{
    public int Changes => Fields.Count(f => f.Changed);
}

public sealed record PresetReviewMap(string Id, IReadOnlyList<PresetReviewEntry> Entries)
{
    public string Name => PresetSelectionReview.Label(Id);
    public int Changes => Entries.Count(e => e.Changed);
    public int Added => Entries.Count(e => e.Status == "Added");
    public int Updated => Entries.Count(e => e.Status == "Modified");
    public int Removed => Entries.Count(e => e.Status == "Removed");
    public int CurrentZones => Entries.Count(e => (e.Category is "Built-in zone" or "Custom zone") && e.Status != "Removed");
    public int CurrentRushPoints => Entries.Count(e => e.Category == "Rush point" && e.Status != "Removed");
}

public sealed record PresetReviewEntry(string Key, IReadOnlyList<PresetReviewField> Fields)
{
    public bool Changed => Fields.Any(f => f.Changed);
    public string Status => Fields.All(f => !f.Exists) ? "Removed" : Fields.All(f => !f.HasDefault) ? "Added"
        : Changed ? "Modified" : "Unchanged";
    public string Category => Fields[0].Path[0] == "Config" ? "Rush point" : Fields[0].Path.ElementAtOrDefault(2) switch
    {
        "BuiltinZones" => "Built-in zone", "CustomZones" => "Custom zone", "Convergence" => "Convergence", _ => "Map settings"
    };
    public string Name => Fields[0].Labels[Math.Min(PresetReviewSummary.EntryDepth(Fields[0]), Fields[0].Labels.Length) - 1];
    public string Description
    {
        get
        {
            if (Status == "Removed") return "Present in ORBIT Default; not included in this setup.";
            var kind = Fields.FirstOrDefault(f => f.Path[^1] == "kind")?.Text;
            var mode = Fields.FirstOrDefault(f => f.Path[^1] == "custom_mode")?.Text;
            var enabled = Fields.FirstOrDefault(f => f.Path[^1] == "enabled")?.Text;
            var tags = new List<string>();
            if (kind != null) tags.Add(kind == "Marked" ? "Room rush" : PresetSelectionReview.Label(kind));
            if (kind == "Custom" && mode != null) tags.Add(PresetSelectionReview.Label(mode));
            if (enabled != null) tags.Add(enabled == "On" ? "Enabled" : "Disabled");
            if (Status == "Added") tags.Add("New entry");
            else
            {
                var labels = Fields.Where(f => f.Changed).Select(f => IsPosition(f) ? "Position / door"
                    : f.Labels.ElementAtOrDefault(PresetReviewSummary.EntryDepth(f)) ?? f.Label).Distinct().ToArray();
                if (labels.Length > 0) tags.Add(string.Join(", ", labels.Take(3)) + (labels.Length > 3 ? $" +{labels.Length - 3} more" : ""));
            }
            return tags.Count == 0 ? "Matches ORBIT Default" : string.Join(" · ", tags);
        }
    }

    public static bool IsPosition(PresetReviewField f) => f.Path.Any(p => p is "Position" or "door_position" or "room_loot_position")
        || f.Path[^1] is "x" or "y" or "z" or "loot_x" or "loot_y" or "loot_z" or "room_loot_x" or "room_loot_y" or "room_loot_z" or "door_id" or "floor_id" or "FloorId";
}
