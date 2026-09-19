using System.Text.Json;
using Orbit.Server.Zones;

namespace Orbit.Server.Presets;

public sealed class PresetSnapshot
{
    public JsonElement Config { get; set; }
    public Dictionary<string, MapZoneModel> Maps { get; set; } = new();
}

public sealed class UserPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public PresetSnapshot Snapshot { get; set; } = new();
    public string? AddonRevision { get; set; }
}

public sealed class PresetLibrary
{
    public string Format { get; set; } = "orbit-presets/1";
    public string ActiveId { get; set; } = "default";
    public List<UserPreset> Presets { get; set; } = new();
    // Retain a partial addon's inherited settings across restarts; its revision tracks source updates.
    public UserPreset? ActiveAddon { get; set; }
}

public sealed record PresetChoice(string Id, string Name, bool ReadOnly, string Contents, string? Source = null);

public sealed class PresetAddon
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Revision { get; init; } = "";
    public JsonElement? Config { get; init; }
    public Dictionary<string, MapZoneModel> Maps { get; init; } = new();
    public string Contents => Config.HasValue
        ? Maps.Count > 0 ? $"Settings + {Maps.Count} map(s)" : "Settings only"
        : $"{Maps.Count} map(s) only";
}
