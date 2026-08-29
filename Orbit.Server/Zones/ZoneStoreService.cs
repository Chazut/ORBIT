using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace Orbit.Server.Zones;

/// <summary>
/// Owns the server-side per-map advection zones (hotspots). Files live in user/mods/ORBIT/zones/,
/// seeded on first access from embedded defaults (a copy of the client's compiled-in zone JSONs).
/// The web UI zone editor edits these; the client fetches the whole set via /orbit/zones at boot and
/// raid start and overrides its local Config/Maps/Zones files with them.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ZoneStoreService(ISptLogger<ZoneStoreService> logger)
{
    // ORBIT map ids (BSG location ids as the client sees them).
    public static readonly string[] MapIds =
    [
        "bigmap", "factory4_day", "factory4_night", "Interchange", "laboratory", "Labyrinth",
        "Lighthouse", "RezervBase", "Sandbox", "Sandbox_high", "Shoreline", "TarkovStreets", "Woods",
    ];

    public static readonly Dictionary<string, string> MapLabels = new()
    {
        ["bigmap"] = "Customs",
        ["factory4_day"] = "Factory (day)",
        ["factory4_night"] = "Factory (night)",
        ["Interchange"] = "Interchange",
        ["laboratory"] = "The Lab",
        ["Labyrinth"] = "Labyrinth",
        ["Lighthouse"] = "Lighthouse",
        ["RezervBase"] = "Reserve",
        ["Sandbox"] = "Ground Zero",
        ["Sandbox_high"] = "Ground Zero (21+)",
        ["Shoreline"] = "Shoreline",
        ["TarkovStreets"] = "Streets of Tarkov",
        ["Woods"] = "Woods",
    };

    // Client-contract serialization: exact member names (see ZoneModels.cs).
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private string ZonesDir
    {
        get
        {
            var modDir = Path.GetDirectoryName(typeof(ZoneStoreService).Assembly.Location)!;
            return Path.Combine(modDir, "zones");
        }
    }

    private string PathFor(string mapId) => Path.Combine(ZonesDir, mapId + ".json");

    /// <summary>Zones for one map: the saved file, seeded from the embedded default on first access.</summary>
    public MapZoneModel GetZones(string mapId)
    {
        try
        {
            var path = PathFor(mapId);
            if (!File.Exists(path))
            {
                var seed = ReadEmbeddedDefault(mapId);
                Directory.CreateDirectory(ZonesDir);
                File.WriteAllText(path, JsonSerializer.Serialize(seed, _json));
                return seed;
            }
            return JsonSerializer.Deserialize<MapZoneModel>(File.ReadAllText(path), _json) ?? new MapZoneModel();
        }
        catch (Exception ex)
        {
            logger.Error($"[ORBIT] Zone load failed for {mapId}: {ex.Message}");
            return ReadEmbeddedDefault(mapId);
        }
    }

    public void Save(string mapId, MapZoneModel zones)
    {
        Directory.CreateDirectory(ZonesDir);
        File.WriteAllText(PathFor(mapId), JsonSerializer.Serialize(zones, _json));
        logger.Info($"[ORBIT] Zones saved for {mapId}");
    }

    /// <summary>Rewrites the map's file from the embedded default and returns the fresh model.</summary>
    public MapZoneModel ResetToDefault(string mapId)
    {
        var seed = ReadEmbeddedDefault(mapId);
        Save(mapId, seed);
        return seed;
    }

    /// <summary>The whole set as one JSON object keyed by map id — the /orbit/zones client payload.</summary>
    public string ToJsonAll()
    {
        var all = new Dictionary<string, MapZoneModel>();
        foreach (var mapId in MapIds)
            all[mapId] = GetZones(mapId);
        return JsonSerializer.Serialize(all, _json);
    }

    // ── Working set (web UI zone editor) ───────────────────────────────
    // The editor binds to these cached instances; the AppBar's global Save/Discard picks them up like
    // ConfigService changes, so the zone editor behaves exactly like every other config page.

    private readonly Dictionary<string, MapZoneModel> _working = new();
    private readonly Dictionary<string, string> _workingSavedJson = new();

    /// <summary>Raised when working copies are replaced from disk (Discard all / Reset) — the editor
    /// page re-binds. Mirrors ConfigService.ConfigReplaced.</summary>
    public event Action? ZonesReplaced;

    public MapZoneModel GetWorking(string mapId)
    {
        lock (_working)
        {
            if (_working.TryGetValue(mapId, out var working)) return working;
            var loaded = GetZones(mapId);
            _working[mapId] = loaded;
            _workingSavedJson[mapId] = JsonSerializer.Serialize(loaded, _json);
            return loaded;
        }
    }

    /// <summary>Map ids whose working copy differs from the last save (for the unsaved-changes UI).</summary>
    public List<string> GetPendingMaps()
    {
        lock (_working)
        {
            var pending = new List<string>();
            foreach (var kv in _working)
            {
                if (_workingSavedJson.TryGetValue(kv.Key, out var saved)
                    && saved != JsonSerializer.Serialize(kv.Value, _json))
                    pending.Add(kv.Key);
            }
            pending.Sort(StringComparer.OrdinalIgnoreCase);
            return pending;
        }
    }

    public void SaveAllPending()
    {
        lock (_working)
        {
            foreach (var mapId in GetPendingMaps())
            {
                Save(mapId, _working[mapId]);
                _workingSavedJson[mapId] = JsonSerializer.Serialize(_working[mapId], _json);
            }
        }
    }

    public void DiscardAllPending()
    {
        lock (_working)
        {
            foreach (var mapId in GetPendingMaps())
            {
                var reloaded = GetZones(mapId);
                _working[mapId] = reloaded;
                _workingSavedJson[mapId] = JsonSerializer.Serialize(reloaded, _json);
            }
        }
        ZonesReplaced?.Invoke();
    }

    /// <summary>Resets the map to shipped defaults, persists, refreshes the working copy.</summary>
    public MapZoneModel ResetWorkingToDefault(string mapId)
    {
        MapZoneModel seed;
        lock (_working)
        {
            seed = ResetToDefault(mapId);
            _working[mapId] = seed;
            _workingSavedJson[mapId] = JsonSerializer.Serialize(seed, _json);
        }
        ZonesReplaced?.Invoke();
        return seed;
    }

    // ── Zone packs (export / import, "ORBIT addons" on the Forge) ──────

    /// <summary>Builds the shareable pack JSON from the CURRENT working copies of the given maps.</summary>
    public string ExportPack(IEnumerable<string> mapIds, string name, string author, string description)
    {
        var pack = new ZonePackModel
        {
            Name = name?.Trim() ?? "",
            Author = author?.Trim() ?? "",
            Description = description?.Trim() ?? "",
            OrbitVersion = typeof(ZoneStoreService).Assembly.GetName().Version?.ToString(3) ?? "",
        };
        foreach (var mapId in mapIds)
        {
            if (Array.IndexOf(MapIds, mapId) < 0) continue;
            pack.Maps[mapId] = GetWorking(mapId);
        }
        return JsonSerializer.Serialize(pack, _json);
    }

    /// <summary>
    /// Imports a pack into the WORKING copies (not saved: the edits ride the unsaved-changes button so
    /// the user reviews them on the map first). Returns applied map ids and skipped entries.
    /// </summary>
    public (List<string> Applied, List<string> Skipped) ImportPack(string json)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        var pack = JsonSerializer.Deserialize<ZonePackModel>(json, _json);
        if (pack?.Maps == null || pack.Maps.Count == 0)
            throw new InvalidDataException("Not a zone pack (no maps inside)");
        if (!string.IsNullOrEmpty(pack.Format) && !pack.Format.StartsWith("orbit-zones/"))
            throw new InvalidDataException($"Unknown pack format '{pack.Format}'");

        foreach (var kv in pack.Maps)
        {
            if (Array.IndexOf(MapIds, kv.Key) < 0 || kv.Value == null)
            {
                skipped.Add(kv.Key);
                continue;
            }
            Sanitize(kv.Value);
            GetWorking(kv.Key); // seed the saved-state snapshot so the diff shows as pending
            lock (_working)
            {
                _working[kv.Key] = kv.Value;
            }
            applied.Add(kv.Key);
        }
        if (applied.Count > 0) ZonesReplaced?.Invoke();
        return (applied, skipped);
    }

    /// <summary>Clamp imported values into the ranges the client accepts (radius < 1 throws in-game).</summary>
    private static void Sanitize(MapZoneModel zones)
    {
        static void ClampRange(ZoneRange range, float lo, float hi)
        {
            range.Min = Math.Clamp(range.Min, lo, hi);
            range.Max = Math.Clamp(range.Max, range.Min, hi);
        }

        foreach (var bz in zones.BuiltinZones.Values)
        {
            ClampRange(bz.Radius, 10f, 2000f);
            ClampRange(bz.Force, -10f, 10f);
            bz.Decay = Math.Clamp(bz.Decay, 0.05f, 20f);
        }
        foreach (var cz in zones.CustomZones)
        {
            ClampRange(cz.Radius, 10f, 2000f);
            ClampRange(cz.Force, -10f, 10f);
            cz.Decay = Math.Clamp(cz.Decay, 0.05f, 20f);
        }
        if (zones.Convergence != null)
        {
            ClampRange(zones.Convergence.Radius, 0f, 5000f);
            ClampRange(zones.Convergence.Force, 0f, 10f);
        }
    }

    private MapZoneModel ReadEmbeddedDefault(string mapId)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream($"Orbit.Server.Resources.Zones.{mapId}.json");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var parsed = JsonSerializer.Deserialize<MapZoneModel>(reader.ReadToEnd(), _json);
                if (parsed != null) return parsed;
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[ORBIT] Embedded zone default missing for {mapId}: {ex.Message}");
        }
        return new MapZoneModel();
    }
}
