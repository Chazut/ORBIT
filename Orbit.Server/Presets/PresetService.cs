using System.Text.Json;
using Orbit.Server.Config;
using Orbit.Server.Zones;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace Orbit.Server.Presets;

/// <summary>
/// One atomic library owns saved presets and their active selection. Editors use detached working
/// copies; the game only reads committed snapshots. Compatibility files are mirrors, never the
/// authority after migration. The built-in Default is rebuilt from shipped defaults on each load.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class PresetService(ConfigService configs, ZoneStoreService zones, EditHistoryService history, ISptLogger<PresetService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private PresetLibrary _library = new();
    private PresetSnapshot _active = new();
    private List<PresetAddon> _addons = new();
    private List<string> _addonErrors = new();
    private DateTime _nextScan;
    private bool _initialized;
    private bool _loadAttempted;
    public string? Error { get; private set; }
    public string? MirrorWarning { get; private set; }
    private string LibraryPath => Path.Combine(configs.ModDirectory, "presets", "library.json");
    public string AddonDirectory => Path.Combine(configs.ModDirectory, "addon");
    public event Action? Changed;

    public string ActiveId { get { lock (_gate) return _library.ActiveId; } }
    public string ActiveName { get { lock (_gate) return _library.ActiveId == "default" ? "Default"
        : _library.ActiveAddon?.Name ?? _library.Presets.First(p => p.Id == _library.ActiveId).Name; } }
    public bool ReadOnly { get { lock (_gate) return _library.ActiveId == "default" || _library.ActiveAddon != null; } }
    public IReadOnlyList<string> AddonErrors { get { lock (_gate) return _addonErrors.ToArray(); } }
    public IReadOnlyList<PresetChoice> Choices
    {
        get
        {
            lock (_gate)
            {
                var result = new List<PresetChoice> { new("default", "Default", true, "Settings + all maps") };
                result.AddRange(_library.Presets.Select(p => new PresetChoice(p.Id, p.Name, false, "Settings + all maps")));
                result.AddRange(_addons.Select(a => new PresetChoice(a.Id, a.Name, true, a.Contents, a.Source)));
                if (_library.ActiveAddon is { } active && result.All(p => p.Id != active.Id))
                    result.Add(new(active.Id, active.Name, true, "Saved addon snapshot (source unavailable)", "Source unavailable"));
                return result;
            }
        }
    }

    public void Initialize()
    {
        lock (_gate)
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            try
            {
                Directory.CreateDirectory(AddonDirectory);
                if (File.Exists(LibraryPath))
                {
                    _library = JsonSerializer.Deserialize<PresetLibrary>(File.ReadAllText(LibraryPath), Json)
                        ?? throw new InvalidDataException("Empty preset library.");
                    ValidateLibrary(_library);
                }
                else
                {
                    var legacy = ReadLegacy(); // strict: never replace malformed user data with defaults
                    BackupLegacy();
                    zones.MigrateExistingZoneFiles();
                    _library = new();
                    if (!Same(legacy, Defaults()))
                    {
                        var custom = NewPreset("Custom", legacy);
                        _library.Presets.Add(custom);
                        _library.ActiveId = custom.Id;
                    }
                    Persist(_library);
                    logger.Info($"[ORBIT] PRESETS: migration complete; active={(_library.ActiveId == "default" ? "Default" : "Custom")}; legacy files backed up");
                }
                _active = Resolve(_library, _library.ActiveId);
                _initialized = true;
                ApplyWorking(replace: true);
                RefreshAddonsLocked();
                WriteMirrors();
            }
            catch (Exception ex)
            {
                Error = $"Presets could not load: {ex.Message} Original files were kept. Fix the file and restart the server.";
                logger.Error($"[ORBIT] PRESETS: {Error}");
                _library = new();
                _initialized = false;
                configs.Load();
            }
        }
    }

    // Called by the editor's existing change poll. Fork the saved baseline, leaving edits unsaved.
    public bool TrackEdits()
    {
        lock (_gate)
        {
            if (!_initialized || Error != null || !ReadOnly || !HasEdits()) return false;
            var next = Clone(_library);
            var custom = NewPreset(UniqueName("Custom"), Normalize(_active));
            next.Presets.Add(custom);
            next.ActiveId = custom.Id;
            next.ActiveAddon = null;
            Persist(next);
            _library = next;
            logger.Info($"[ORBIT] PRESETS: created {custom.Name} from protected preset");
        }
        Changed?.Invoke();
        return true;
    }

    public bool RefreshAddons(bool force = false)
    {
        bool changed;
        lock (_gate)
        {
            if (!_initialized || Error != null || (!force && DateTime.UtcNow < _nextScan)) return false;
            changed = RefreshAddonsLocked();
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    private bool RefreshAddonsLocked()
    {
        _nextScan = DateTime.UtcNow.AddSeconds(5);
        var (addons, errors) = AddonDiscovery.Scan(AddonDirectory, zones);
        var previousErrors = _addonErrors.ToArray();
        var changed = JsonSerializer.Serialize(_addons, Json) != JsonSerializer.Serialize(addons, Json)
            || !_addonErrors.SequenceEqual(errors);
        _addons = addons;
        _addonErrors = errors;
        var active = _library.ActiveAddon;
        var update = active == null ? null : addons.FirstOrDefault(a => a.Id == active.Id && a.Revision != active.AddonRevision);
        if (update != null)
        {
            try
            {
                // Protect a slider edit that arrived just before the scan, even before the normal UI poll.
                TrackEdits();
                if (_library.ActiveAddon != null)
                {
                    var next = Clone(_library);
                    var snapshot = ApplyAddon(_active, update);
                    next.ActiveAddon = new UserPreset
                        { Id = update.Id, Name = update.Name, AddonRevision = update.Revision, Snapshot = snapshot };
                    Persist(next);
                    _library = next;
                    _active = snapshot;
                    ApplyWorking(replace: true);
                    WriteMirrors();
                    logger.Info($"[ORBIT] PRESETS: addon updated {update.Name}; applies on the next client fetch");
                }
                changed = true;
            }
            catch (Exception ex)
            {
                var error = $"{update.Source}: update could not be saved; the previous preset was kept. {ex.Message}";
                _addonErrors.Add(error);
                if (!previousErrors.Contains(error)) logger.Error($"[ORBIT] PRESETS: {error}");
                changed = true;
            }
        }
        // Missing or temporarily invalid files keep their last valid active snapshot.
        return changed;
    }

    public void Save()
    {
        lock (_gate)
        {
            RequireReady();
            TrackEdits();
            var next = WithSavedEdits();
            Persist(next);
            _library = next;
            _active = Resolve(next, next.ActiveId);
            ApplyWorking(replace: false);
            WriteMirrors();
        }
        Changed?.Invoke();
    }

    public void Switch(string id)
    {
        lock (_gate)
        {
            RequireReady();
            if (id == _library.ActiveId) return;
            RefreshAddonsLocked();
            if (id != "default" && _library.Presets.All(p => p.Id != id) && _addons.All(a => a.Id != id))
                throw new InvalidDataException("This preset is no longer available.");
            TrackEdits();
            var next = WithSavedEdits();
            var addon = _addons.FirstOrDefault(a => a.Id == id);
            if (addon != null)
            {
                next.ActiveAddon = new UserPreset
                    { Id = id, Name = addon.Name, AddonRevision = addon.Revision, Snapshot = ApplyAddon(Capture(), addon) };
            }
            else next.ActiveAddon = null;
            next.ActiveId = id;
            var target = Resolve(next, id);
            Persist(next); // save outgoing edits and the new selection in the same transaction
            _library = next;
            _active = target;
            ApplyWorking(replace: true);
            WriteMirrors();
            logger.Info($"[ORBIT] PRESETS: selected {ActiveName}");
        }
        Changed?.Invoke();
    }

    public void Duplicate(string name)
    {
        lock (_gate)
        {
            RequireReady();
            name = ValidateName(name);
            var snapshot = Capture();
            // The new copy includes current edits. Preserve edits in an outgoing editable preset too.
            var next = ReadOnly ? Clone(_library) : WithSavedEdits();
            var copy = NewPreset(name, snapshot);
            next.Presets.Add(copy);
            next.ActiveId = copy.Id;
            next.ActiveAddon = null;
            Persist(next);
            _library = next;
            _active = snapshot;
            ApplyWorking(replace: true);
            WriteMirrors();
        }
        Changed?.Invoke();
    }

    public void Rename(string name)
    {
        lock (_gate)
        {
            RequireReady();
            if (ReadOnly) throw new InvalidOperationException("Duplicate this protected preset to rename it.");
            name = ValidateName(name, _library.ActiveId);
            var next = Clone(_library);
            next.Presets.First(p => p.Id == next.ActiveId).Name = name;
            Persist(next);
            _library = next;
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            RequireReady();
            if (!_library.Presets.Any(p => p.Id == id)) throw new InvalidOperationException("Only personal presets can be deleted.");
            if (id == _library.ActiveId) throw new InvalidOperationException("Select another preset before deleting this one.");
            var next = Clone(_library);
            next.Presets.RemoveAll(p => p.Id == id);
            Persist(next);
            _library = next;
        }
        Changed?.Invoke();
    }

    public string Export()
    {
        lock (_gate)
        {
            RequireReady();
            var snapshot = Capture();
            return JsonSerializer.Serialize(new { Format = "orbit-preset/1", Name = ActiveName, snapshot.Config, snapshot.Maps }, Json);
        }
    }

    public string ConfigForGame()
    {
        RefreshAddons();
        lock (_gate) return _initialized && Error == null ? _active.Config.GetRawText() : configs.SavedJson;
    }

    public string ZonesForGame()
    {
        RefreshAddons();
        lock (_gate) return _initialized && Error == null
            ? JsonSerializer.Serialize(Normalize(_active).Maps, Json) : zones.ToJsonAll();
    }

    private bool HasEdits() => configs.GetPendingChanges().Count > 0 || zones.GetPendingMaps().Count > 0;
    private PresetLibrary WithSavedEdits()
    {
        var next = Clone(_library);
        if (!ReadOnly) next.Presets.First(p => p.Id == next.ActiveId).Snapshot = Capture();
        return next;
    }
    private PresetSnapshot Capture() => Normalize(new PresetSnapshot
        { Config = JsonSerializer.Deserialize<JsonElement>(configs.ToJson()), Maps = zones.CapturePreset() });
    private PresetSnapshot Defaults() => new()
        { Config = JsonSerializer.Deserialize<JsonElement>(ConfigService.DefaultJson), Maps = zones.DefaultSnapshot() };

    private PresetSnapshot ApplyAddon(PresetSnapshot baseline, PresetAddon addon)
    {
        var combined = Normalize(baseline);
        if (addon.Config is { } patch)
            combined.Config = JsonSerializer.Deserialize<JsonElement>(AddonDiscovery.OverlayConfig(combined.Config.GetRawText(), patch));
        foreach (var (map, content) in addon.Maps) combined.Maps[map] = content;
        return Normalize(combined);
    }

    private PresetSnapshot Normalize(PresetSnapshot snapshot)
    {
        var result = new PresetSnapshot { Config = JsonSerializer.Deserialize<JsonElement>(ConfigService.NormalizeJson(snapshot.Config.GetRawText())) };
        if (snapshot.Maps == null) throw new InvalidDataException("Preset maps cannot be null.");
        foreach (var (id, model) in snapshot.Maps)
            result.Maps[id] = zones.NormalizeSnapshot(id, JsonSerializer.Serialize(model, Json));
        // Future releases can add maps without invalidating existing presets.
        foreach (var (id, model) in zones.DefaultSnapshot()) result.Maps.TryAdd(id, model);
        return result;
    }

    private PresetSnapshot Resolve(PresetLibrary library, string id) => id == "default" ? Defaults()
        : Normalize(library.ActiveAddon?.Id == id ? library.ActiveAddon.Snapshot
            : library.Presets.First(p => p.Id == id).Snapshot);

    private void ApplyWorking(bool replace)
    {
        if (replace) configs.SelectSnapshot(_active.Config.GetRawText());
        else configs.AcceptSnapshot(_active.Config.GetRawText());
        zones.SelectSnapshot(_active.Maps, replace);
        if (replace) history.Reset();
    }

    private PresetSnapshot ReadLegacy()
    {
        var snapshot = Defaults();
        var configFile = Path.Combine(configs.ModDirectory, "config.json");
        if (File.Exists(configFile)) snapshot.Config = JsonSerializer.Deserialize<JsonElement>(ConfigService.NormalizeJson(File.ReadAllText(configFile)));
        foreach (var id in ZoneStoreService.MapIds)
        {
            var file = Path.Combine(configs.ModDirectory, "zones", id + ".json");
            if (File.Exists(file)) snapshot.Maps[id] = zones.NormalizeSnapshot(id, File.ReadAllText(file));
        }
        return snapshot;
    }

    private void BackupLegacy()
    {
        var destination = Path.Combine(configs.ModDirectory, "presets", "legacy-2.0");
        var paths = new[] { "config.json" }.Concat(ZoneStoreService.MapIds.Select(id => Path.Combine("zones", id + ".json")));
        foreach (var relative in paths)
        {
            var source = Path.Combine(configs.ModDirectory, relative);
            var backup = Path.Combine(destination, relative);
            if (!File.Exists(source) || File.Exists(backup)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(source, backup);
        }
    }

    private void ValidateLibrary(PresetLibrary library)
    {
        if (library.Format != "orbit-presets/1" || library.Presets == null) throw new InvalidDataException("Unsupported preset library.");
        var ids = new HashSet<string>();
        foreach (var preset in library.Presets)
        {
            if (preset == null || !preset.Id.StartsWith("local:") || !ids.Add(preset.Id) || string.IsNullOrWhiteSpace(preset.Name))
                throw new InvalidDataException("Invalid or duplicate personal preset.");
            preset.Snapshot = Normalize(preset.Snapshot);
        }
        if (library.ActiveAddon is { } addon)
        {
            if (!addon.Id.StartsWith("addon:") || addon.Id != library.ActiveId) throw new InvalidDataException("Invalid active addon.");
            addon.Snapshot = Normalize(addon.Snapshot);
        }
        if (library.ActiveId != "default" && !ids.Contains(library.ActiveId) && library.ActiveAddon == null)
            throw new InvalidDataException("The active preset is missing.");
    }

    private void Persist(PresetLibrary library) => AtomicWrite(LibraryPath, JsonSerializer.Serialize(library, Json));
    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void WriteMirrors()
    {
        MirrorWarning = null;
        try
        {
            AtomicWrite(Path.Combine(configs.ModDirectory, "config.json"), _active.Config.GetRawText());
            foreach (var (id, model) in Normalize(_active).Maps)
                AtomicWrite(Path.Combine(configs.ModDirectory, "zones", id + ".json"), JsonSerializer.Serialize(model, Json));
        }
        catch (Exception ex)
        {
            MirrorWarning = $"The preset is saved, but a compatibility copy could not be updated: {ex.Message}";
            logger.Error($"[ORBIT] PRESETS: {MirrorWarning}");
        }
    }

    private void RequireReady()
    {
        if (!_initialized || Error != null) throw new InvalidOperationException(Error ?? "Presets are not initialized.");
    }
    private string ValidateName(string name, string? except = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) throw new InvalidDataException("Use a name between 1 and 60 characters.");
        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
            || _library.Presets.Any(p => p.Id != except && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This preset name is already in use.");
        return name;
    }
    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; _library.Presets.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++) candidate = $"{name} {i}";
        return candidate;
    }
    private static UserPreset NewPreset(string name, PresetSnapshot snapshot) => new()
        { Id = "local:" + Guid.NewGuid().ToString("N"), Name = name, Snapshot = snapshot };
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static bool Same(PresetSnapshot a, PresetSnapshot b)
        => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(a, Json), JsonSerializer.SerializeToElement(b, Json));
}
