using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace Orbit.Server.Config;

/// <summary>
/// Owns the server-side ORBIT config: loads user/mods/ORBIT/config.json (creating it with defaults
/// on first run), exposes it to the web UI and the /orbit/config client endpoint, and persists
/// edits. Unknown JSON fields are preserved-by-rewrite: the file is regenerated from the typed
/// model on save, so schema evolution goes through ConfigVersion.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ConfigService(ISptLogger<ConfigService> logger)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public OrbitServerConfig Config { get; private set; } = new();

    // Serialized snapshot of the config as last loaded/saved — the baseline the web UI diffs against
    // to surface unsaved changes. Normalized through the same serializer options as ToJson().
    private string? _savedJson;

    private string ConfigPath
    {
        get
        {
            var modDir = Path.GetDirectoryName(typeof(ConfigService).Assembly.Location)!;
            return Path.Combine(modDir, "config.json");
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var parsed = JsonSerializer.Deserialize<OrbitServerConfig>(File.ReadAllText(ConfigPath), _jsonOptions);
                if (parsed != null)
                {
                    Config = parsed;
                    _savedJson = ToJson();
                    logger.Info($"[ORBIT] Server config loaded ({ConfigPath})");
                    return;
                }
            }
            Save();
            logger.Info($"[ORBIT] Server config created with defaults ({ConfigPath})");
        }
        catch (Exception ex)
        {
            logger.Error($"[ORBIT] Failed to load server config, using defaults: {ex.Message}");
            Config = new OrbitServerConfig();
            _savedJson = ToJson();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Config, _jsonOptions);
        File.WriteAllText(ConfigPath, json);
        _savedJson = json;
        logger.Info("[ORBIT] Server config saved");
    }

    public string ToJson() => JsonSerializer.Serialize(Config, _jsonOptions);

    /// <summary>Raised when the config OBJECT is swapped out from under the pages (Discard all): their
    /// bindings point at the old instance, so they must re-render. Subscribed by OrbitConfigPage.</summary>
    public event Action? ConfigReplaced;

    /// <summary>
    /// Replaces the in-memory config with an imported JSON payload (the web UI's config import).
    /// Unknown fields are dropped, missing fields fall back to compiled defaults. The result lands as
    /// UNSAVED changes — it is diffed against the last saved snapshot so the user reviews then Saves.
    /// Throws on malformed JSON; returns false when the payload deserializes to nothing.
    /// </summary>
    public bool ImportJson(string json)
    {
        var parsed = JsonSerializer.Deserialize<OrbitServerConfig>(json, _jsonOptions);
        if (parsed == null) return false;
        Config = parsed;
        ConfigReplaced?.Invoke();
        return true;
    }

    /// <summary>Reverts the in-memory config to the last loaded/saved state (the web UI's "Discard all").</summary>
    public void DiscardChanges()
    {
        if (_savedJson == null) return;
        var restored = JsonSerializer.Deserialize<OrbitServerConfig>(_savedJson, _jsonOptions);
        if (restored == null) return;
        Config = restored;
        ConfigReplaced?.Invoke();
    }

    /// <summary>
    /// Leaf-level diff between the current config and the last saved snapshot, as (path, from, to)
    /// tuples with snake_case JSON paths. Empty when everything is saved. Drives the AppBar's
    /// "unsaved changes" button.
    /// </summary>
    public List<(string Path, string From, string To)> GetPendingChanges()
    {
        var result = new List<(string, string, string)>();
        if (_savedJson == null) return result;
        var current = ToJson();
        if (current == _savedJson) return result;

        using var oldDoc = JsonDocument.Parse(_savedJson);
        using var newDoc = JsonDocument.Parse(current);
        DiffElement(oldDoc.RootElement, newDoc.RootElement, "", result);
        return result;
    }

    private static void DiffElement(JsonElement a, JsonElement b, string path, List<(string, string, string)> result)
    {
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in b.EnumerateObject())
            {
                var childPath = path.Length == 0 ? prop.Name : $"{path}.{prop.Name}";
                if (a.TryGetProperty(prop.Name, out var oldValue))
                    DiffElement(oldValue, prop.Value, childPath, result);
                else
                    result.Add((childPath, "", Render(prop.Value)));
            }
            return;
        }
        if (a.GetRawText() != b.GetRawText())
            result.Add((path, Render(a), Render(b)));
    }

    private static string Render(JsonElement el)
    {
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
        return s.Length > 28 ? s[..25] + "..." : s;
    }
}
