using System.Text.Json;
using Orbit.Zones;

namespace Orbit.Server.Zones;

public partial class ZoneStoreService
{
    // Presets own saved tuning once initialized. Legacy zone files remain compatibility copies.
    // Native-floor metadata stays independent, so switching presets cannot restore stale scene data.
    private Dictionary<string, string>? _presetSaved;

    public Dictionary<string, MapZoneModel> DefaultSnapshot()
    {
        lock (_working) return MapIds.ToDictionary(id => id, ReadEmbeddedDefault);
    }

    public MapZoneModel NormalizeSnapshot(string mapId, string json)
    {
        if (!MapIds.Contains(mapId)) throw new InvalidDataException($"Unknown map '{mapId}'.");
        var model = JsonSerializer.Deserialize<MapZoneModel>(json, _json)
            ?? throw new InvalidDataException($"Empty zones for {mapId}.");
        if (model.SchemaVersion > CurrentZoneSchema)
            throw new InvalidDataException($"Zones for {mapId} require a newer ORBIT version.");
        if (model.BuiltinZones == null || model.CustomZones == null
            || model.BuiltinZones.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value == null
                || p.Value.Radius == null || p.Value.Force == null)
            || model.CustomZones.Any(z => z == null || z.Position == null || z.Radius == null || z.Force == null)
            || model.Convergence is { } convergence && (convergence.Radius == null || convergence.Force == null))
            throw new InvalidDataException($"Invalid zones for {mapId}.");
        foreach (var custom in model.CustomZones)
            if (!FloorSelection.IsKnown(mapId, custom.FloorId))
                throw new InvalidDataException($"Unknown custom floor '{custom.FloorId}' on {mapId}.");
        NormalizeNativeFloors(mapId, model);
        return model;
    }

    public Dictionary<string, MapZoneModel> CapturePreset()
    {
        lock (_working)
            return MapIds.ToDictionary(id => id, id => NormalizeSnapshot(id,
                JsonSerializer.Serialize(_working.TryGetValue(id, out var value) ? value : GetZones(id), _json)));
    }

    public MapZoneModel NormalizeAddon(string mapId, string json)
    {
        var model = NormalizeSnapshot(mapId, json);
        Sanitize(model);
        return model;
    }

    public void SelectSnapshot(Dictionary<string, MapZoneModel> maps, bool replaceWorking)
    {
        lock (_working)
        {
            _presetSaved = maps.ToDictionary(p => p.Key,
                p => JsonSerializer.Serialize(NormalizeSnapshot(p.Key, JsonSerializer.Serialize(p.Value, _json)), _json));
            foreach (var id in _working.Keys.ToArray())
            {
                if (replaceWorking) _working[id] = NormalizeSnapshot(id, _presetSaved[id]);
                else NormalizeNativeFloors(id, _working[id]);
                _workingSavedJson[id] = _presetSaved[id];
            }
        }
        if (replaceWorking) ZonesReplaced?.Invoke();
    }
}
