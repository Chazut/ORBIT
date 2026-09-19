using System.Text.Json;
using Orbit.Zones;

namespace Orbit.Server.Zones;

public partial class ZoneStoreService
{
    public const int CurrentZoneSchema = 2;
    public void MigrateExistingZoneFiles()
    {
        foreach (var mapId in MapIds) if (File.Exists(PathFor(mapId))) GetZones(mapId);
    }
    private Dictionary<string, Dictionary<string, string?>>? _nativeFloorCache;
    private string NativeCachePath => Path.Combine(ZonesDir, "native-floors.json");

    private Dictionary<string, Dictionary<string, string?>> NativeCache
    {
        get
        {
            if (_nativeFloorCache != null) return _nativeFloorCache;
            try
            {
                _nativeFloorCache = File.Exists(NativeCachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(File.ReadAllText(NativeCachePath), _json)
                    : null;
            }
            catch (Exception ex) { logger.Error($"[ORBIT] Native floor cache load failed: {ex.Message}"); }
            return _nativeFloorCache ??= new();
        }
    }

    public string? NativeFloorsFor(string mapId, string zoneName)
    {
        lock (_working)
        {
            if (NativeCache.TryGetValue(mapId, out var actual) && actual.TryGetValue(zoneName, out var floors)
                && FloorSelection.IsKnown(mapId, floors)) return floors;
            return NativeFloorDefaults.ByMap.TryGetValue(mapId, out var map) && map.TryGetValue(zoneName, out var initial)
                ? initial : null;
        }
    }

    public bool HasLiveNativeFloors(string mapId, string zoneName)
    {
        lock (_working) return NativeCache.TryGetValue(mapId, out var map) && map.ContainsKey(zoneName);
    }

    private bool NormalizeNativeFloors(string mapId, MapZoneModel zones)
    {
        var changed = zones.SchemaVersion < CurrentZoneSchema;
        if (changed) zones.SchemaVersion = CurrentZoneSchema;
        foreach (var (name, zone) in zones.BuiltinZones)
        {
            var native = NativeFloorsFor(mapId, name);
            if (zone.FloorId == native) continue;
            zone.FloorId = native;
            changed = true;
        }
        return changed;
    }

    private static void BackupBeforeNativeMigration(string path)
    {
        var backup = path + ".pre-native-floors.bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
    }

    // Scene metadata is separate from user tuning. Updating it must preserve pending editor changes.
    public void RecordNativeFloors(string mapId, Dictionary<string, string?> floors)
    {
        if (!MapIds.Contains(mapId) || floors == null || floors.Count > 1000)
            throw new InvalidDataException("Invalid native floor report");
        foreach (var (name, selection) in floors)
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !FloorSelection.IsKnown(mapId, selection))
                throw new InvalidDataException("Invalid native floor selection");
        lock (_working)
        {
            NativeCache[mapId] = new(floors);
            Directory.CreateDirectory(ZonesDir);
            File.WriteAllText(NativeCachePath + ".tmp", JsonSerializer.Serialize(NativeCache, _json));
            File.Move(NativeCachePath + ".tmp", NativeCachePath, overwrite: true);
            // Migrate the saved file too, without replacing any unsaved sliders, names or custom zones.
            GetZones(mapId);
            if (_working.TryGetValue(mapId, out var current)) NormalizeNativeFloors(mapId, current);
            if (_workingSavedJson.TryGetValue(mapId, out var saved))
            {
                var baseline = JsonSerializer.Deserialize<MapZoneModel>(saved, _json);
                if (baseline != null)
                {
                    NormalizeNativeFloors(mapId, baseline);
                    _workingSavedJson[mapId] = JsonSerializer.Serialize(baseline, _json);
                }
            }
        }
        ZonesReplaced?.Invoke();
    }
}
