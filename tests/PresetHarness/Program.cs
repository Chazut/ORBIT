using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Server.Config;
using Orbit.Server.Presets;
using Orbit.Server.Zones;
using SPTarkov.Common.Models.Logging;

var checks = 0;
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); } catch { checks++; return; }
    throw new Exception(message);
}
static bool Equal(string a, string b) => JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(a), JsonSerializer.Deserialize<JsonElement>(b));
static float LootDistance(string json) => JsonNode.Parse(json)!["loot"]!["detect_distance"]!.GetValue<float>();
var root = Path.Combine(Path.GetTempPath(), "orbit-preset-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var clean = Open("clean");
    Check(clean.Presets.Error == null && clean.Presets.ActiveId == "default" && clean.Presets.Choices.Count == 1, "Fresh installation selects Default");
    Check(Directory.Exists(clean.Presets.AddonDirectory), "Addon directory created automatically");
    Check(Equal(clean.Presets.ConfigForGame(), ConfigService.DefaultJson), "Default config matches shipped config");
    var shippedZones = clean.Presets.ZonesForGame();
    clean.Zones.GetWorking("RezervBase");
    Check(!clean.Presets.TrackEdits(), "Opening a map is not an edit");
    clean.Configs.Config.Loot.DetectDistance = 130;
    Check(LootDistance(clean.Presets.ConfigForGame()) == 80, "Unsaved config cannot leak into game endpoint");
    Check(clean.Presets.TrackEdits() && clean.Presets.ActiveName == "Custom" && !clean.Presets.ReadOnly, "First edit creates personal Custom");
    var customId = clean.Presets.ActiveId;
    Check(clean.Configs.GetPendingChanges().Count == 1, "Auto fork leaves edit pending");
    Check(!clean.Presets.TrackEdits() && clean.Presets.Choices.Count == 2, "Repeated polling creates only one Custom");
    clean.Zones.GetWorking("RezervBase").CustomZones.Add(new() { Name = "Bunker PMC", FloorId = "bunkers", BotTypes = ["PMC"] });
    Check(Equal(clean.Presets.ZonesForGame(), shippedZones), "Unsaved zones cannot leak into game endpoint");
    clean.Presets.Save();
    Check(LootDistance(clean.Presets.ConfigForGame()) == 130 && clean.Presets.ZonesForGame().Contains("Bunker PMC"), "Save applies config and zones together");
    Check(clean.Configs.GetPendingChanges().Count == 0 && clean.Zones.GetPendingMaps().Count == 0, "Save clears both pending baselines");
    clean.History.Track(); clean.Configs.Config.Loot.DetectDistance = 145; clean.History.Track();
    clean.Presets.Switch("default");
    Check(Equal(clean.Presets.ConfigForGame(), ConfigService.DefaultJson) && Equal(clean.Presets.ZonesForGame(), shippedZones), "Default is unchanged by edits to Custom");
    Check(!clean.History.CanUndo && !clean.History.CanRedo, "Switch clears undo history across presets");
    clean.Presets.Switch(customId);
    Check(LootDistance(clean.Presets.ConfigForGame()) == 145 && clean.Presets.ZonesForGame().Contains("Bunker PMC"), "Switch saved outgoing edits");
    clean.Presets.Duplicate("Quiet raids");
    var quietId = clean.Presets.ActiveId;
    clean.Configs.Config.General.SquadRally = false;
    clean.Presets.Save();
    clean.Presets.Switch(customId);
    Check(clean.Configs.Config.General.SquadRally, "Editing a duplicate leaves source preset intact");
    clean.Presets.Rename("Original tuning");
    Check(clean.Presets.ActiveName == "Original tuning", "Rename preserves active identity");
    Reject(() => clean.Presets.Rename("default"), "Reserved name accepted");
    Reject(() => clean.Presets.Duplicate("Quiet raids"), "Duplicate name accepted");
    Reject(() => clean.Presets.Delete(customId), "Active preset deletion accepted");
    Reject(() => clean.Presets.Delete("default"), "Default deletion accepted");
    clean.Presets.Delete(quietId);
    Check(clean.Presets.Choices.All(p => p.Id != quietId), "Inactive personal preset deleted");
    var reopened = Open("clean");
    Check(reopened.Presets.ActiveId == customId && reopened.Presets.ActiveName == "Original tuning" && reopened.Presets.ZonesForGame().Contains("Bunker PMC"), "Selection and content survive restart");

    clean.Presets.Switch("default");
    clean.Zones.GetWorking("RezervBase").CustomZones.Add(new() { Name = "Zone only" });
    Check(clean.Presets.TrackEdits() && clean.Presets.ActiveName == "Custom", "Zone-only edit forks Default");
    clean.Zones.DiscardAllPending();
    Check(!clean.Presets.ZonesForGame().Contains("Zone only") && clean.Zones.GetPendingMaps().Count == 0, "Discard removes pending zone edit");
    clean.Zones.GetWorking("RezervBase").CustomZones.Add(new() { Name = "Reset test" });
    clean.Presets.Save();
    clean.Zones.ResetWorkingToDefault("RezervBase");
    Check(clean.Presets.ZonesForGame().Contains("Reset test") && clean.Zones.GetPendingMaps().Contains("RezervBase"), "Reset map is unsaved until Save");
    clean.Zones.DiscardAllPending();
    Check(clean.Zones.GetWorking("RezervBase").CustomZones.Any(z => z.Name == "Reset test"), "Discard restores zone reset");

    var legacyRoot = Path.Combine(root, "legacy");
    Directory.CreateDirectory(Path.Combine(legacyRoot, "zones"));
    var legacyConfig = """{"config_version":2,"loot":{"detect_distance":92},"general":{"squad_rally":false}}""";
    var legacyZones = """{"BuiltinZones":{"ZoneSubCommand":{"Radius":{"Min":45,"Max":99},"Force":{"Min":2,"Max":3},"FloorId":"base"}},"CustomZones":[{"Name":"2.0 custom","Position":{"x":3,"y":5}}]}""";
    File.WriteAllText(Path.Combine(legacyRoot, "config.json"), legacyConfig);
    File.WriteAllText(Path.Combine(legacyRoot, "zones", "RezervBase.json"), legacyZones);
    var legacy = Open("legacy");
    Check(legacy.Presets.Error == null && legacy.Presets.ActiveName == "Custom", "2.0 tuning migrated to Custom");
    Check(LootDistance(legacy.Presets.ConfigForGame()) == 92 && !legacy.Configs.Config.General.SquadRally && legacy.Configs.Config.GhostMode.NativeGhostMovement, "Migration retains old settings and fills new defaults");
    Check(legacy.Zones.GetWorking("RezervBase").BuiltinZones["ZoneSubCommand"].FloorId == "bunkers", "Migration refreshes built-in floors");
    Check(legacy.Zones.GetWorking("RezervBase").CustomZones[0].FloorId == null && legacy.Zones.GetWorking("RezervBase").CustomZones[0].Position.X == 3, "Migration preserves legacy custom scope and position");
    var backup = Path.Combine(legacyRoot, "presets", "legacy-2.0");
    Check(File.ReadAllText(Path.Combine(backup, "config.json")) == legacyConfig && File.ReadAllText(Path.Combine(backup, "zones", "RezervBase.json")) == legacyZones, "Migration keeps exact original files");
    var legacyCount = legacy.Presets.Choices.Count;
    var legacyAgain = Open("legacy");
    Check(legacyAgain.Presets.Choices.Count == legacyCount && File.ReadAllText(Path.Combine(backup, "config.json")) == legacyConfig, "Migration is idempotent");
    var defaultRoot = Path.Combine(root, "old-default");
    Directory.CreateDirectory(Path.Combine(defaultRoot, "zones"));
    var oldDefault = JsonNode.Parse(ConfigService.DefaultJson)!;
    oldDefault["ai_limiter"]!.AsObject().Remove("native_ghost_movement");
    File.WriteAllText(Path.Combine(defaultRoot, "config.json"), oldDefault.ToJsonString());
    var oldDefaultMap = JsonSerializer.SerializeToNode(clean.Zones.DefaultSnapshot()["RezervBase"])!;
    oldDefaultMap.AsObject().Remove("SchemaVersion");
    foreach (var zone in oldDefaultMap["BuiltinZones"]!.AsObject()) zone.Value!.AsObject().Remove("FloorId");
    File.WriteAllText(Path.Combine(defaultRoot, "zones", "RezervBase.json"), oldDefaultMap.ToJsonString());
    var oldDefaults = Open("old-default");
    Check(oldDefaults.Presets.ActiveId == "default" && oldDefaults.Presets.Choices.Count == 1, "Untouched old defaults do not create a spurious Custom");
    var zonesOnlyRoot = Path.Combine(root, "old-zones");
    Directory.CreateDirectory(Path.Combine(zonesOnlyRoot, "zones"));
    File.WriteAllText(Path.Combine(zonesOnlyRoot, "zones", "RezervBase.json"), legacyZones);
    Check(Open("old-zones").Presets.ActiveName == "Custom", "Zone-only legacy tuning also creates Custom");

    var addons = legacy.Presets.AddonDirectory;
    var globalFile = Path.Combine(addons, "global.json");
    var partialGlobal = """{"loot":{"detect_distance":175}}""";
    File.WriteAllText(globalFile, partialGlobal);
    File.WriteAllText(Path.Combine(addons, "zones.json"), """{"Format":"orbit-zones/1","Name":"Bunkers","Maps":{"RezervBase":{"BuiltinZones":{"ZoneSubCommand":{"FloorId":"base"}},"CustomZones":[{"Name":"Addon zone","FloorId":"bunkers","BotTypes":["PMC"]}]}}}""");
    Directory.CreateDirectory(Path.Combine(addons, "nested"));
    File.WriteAllText(Path.Combine(addons, "nested", "Woods.json"), """{"BuiltinZones":{},"CustomZones":[{"Name":"Woods addon"}]}""");
    File.WriteAllText(Path.Combine(addons, "bad.json"), """{"Format":"orbit-preset/99"}""");
    File.WriteAllText(Path.Combine(addons, "broken.json"), "{");
    File.WriteAllText(Path.Combine(addons, "null.json"), """{"general":null}""");
    legacy.Presets.RefreshAddons(force: true);
    Check(legacy.Presets.Choices.Count(p => p.Source != null) == 3 && legacy.Presets.AddonErrors.Count == 3, "Discovery classifies valid files and isolates malformed addons");
    var globalId = legacy.Presets.Choices.Single(p => p.Name == "global").Id;
    var zoneId = legacy.Presets.Choices.Single(p => p.Name == "Bunkers").Id;
    var beforeAddonZones = legacy.Presets.ZonesForGame();
    legacy.Presets.Switch(globalId);
    Check(LootDistance(legacy.Presets.ConfigForGame()) == 175 && !legacy.Configs.Config.General.SquadRally && Equal(beforeAddonZones, legacy.Presets.ZonesForGame()), "Partial settings addon keeps other settings and all zones");
    legacy.Presets.Switch(zoneId);
    Check(LootDistance(legacy.Presets.ConfigForGame()) == 175 && legacy.Presets.ZonesForGame().Contains("Addon zone"), "Zone-only addon keeps global settings");
    Check(legacy.Zones.GetWorking("RezervBase").BuiltinZones["ZoneSubCommand"].FloorId == "bunkers", "Addon cannot override native built-in floors");
    Check(Equal(JsonNode.Parse(beforeAddonZones)!["Woods"]!.ToJsonString(), JsonNode.Parse(legacy.Presets.ZonesForGame())!["Woods"]!.ToJsonString()), "Unspecified maps preserved");
    var addonRestart = Open("legacy");
    Check(addonRestart.Presets.ActiveId == zoneId && LootDistance(addonRestart.Presets.ConfigForGame()) == 175 && addonRestart.Presets.ZonesForGame().Contains("Addon zone"), "Partial addon snapshot survives restart with inherited settings");
    File.Delete(Path.Combine(addons, "zones.json"));
    legacy.Presets.RefreshAddons(force: true);
    Check(legacy.Presets.ActiveId == zoneId && legacy.Presets.Choices.Any(p => p.Id == zoneId) && legacy.Presets.ZonesForGame().Contains("Addon zone"), "Removing active addon source preserves active snapshot");
    Check(Open("legacy").Presets.ZonesForGame().Contains("Addon zone"), "Missing addon snapshot survives restart");
    legacy.Configs.Config.Loot.DetectDistance = 188;
    Check(legacy.Presets.TrackEdits() && legacy.Presets.ActiveName == "Custom 2", "Editing addon forks a uniquely named personal copy");
    legacy.Presets.Save();
    Check(File.ReadAllText(globalFile) == partialGlobal, "Editing addon never modifies source files");
    var export = legacy.Presets.Export();
    var parsedExport = AddonDiscovery.Parse(export, "shared.json", legacy.Zones);
    Check(parsedExport.Config.HasValue && parsedExport.Maps.Count == ZoneStoreService.MapIds.Length && parsedExport.Maps["RezervBase"].CustomZones[0].BotTypes![0] == "PMC", "Full preset export round-trips settings, maps, floors and bot types");
    File.WriteAllText(Path.Combine(addons, "shared.json"), export);
    legacy.Presets.RefreshAddons(force: true);
    var sharedId = legacy.Presets.Choices.Single(p => p.Source == "shared.json").Id;
    legacy.Presets.Switch("default"); legacy.Presets.Switch(sharedId);
    Check(LootDistance(legacy.Presets.ConfigForGame()) == 188 && legacy.Presets.ZonesForGame().Contains("Addon zone"), "Combined addon applies settings and maps");

    var updates = Open("updates");
    var updateFile = Path.Combine(updates.Presets.AddonDirectory, "updatable.json");
    static string UpdatePayload(string name, int distance) => JsonSerializer.Serialize(new
    {
        Format = "orbit-preset/1", Name = name,
        Config = new { loot = new { detect_distance = distance } },
        Maps = new Dictionary<string, MapZoneModel> { ["RezervBase"] = new() { CustomZones = [new() { Name = name, FloorId = "bunkers" }] } },
    });
    File.WriteAllText(updateFile, UpdatePayload("Version 1", 160));
    updates.Presets.RefreshAddons(force: true);
    var updateId = updates.Presets.Choices.Single(p => p.Source != null).Id;
    updates.Presets.Switch(updateId);
    var updateOtherMap = JsonNode.Parse(updates.Presets.ZonesForGame())!["Woods"]!.ToJsonString();
    File.WriteAllText(updateFile, UpdatePayload("Version 2", 170));
    updates.Presets.RefreshAddons(force: true);
    Check(updates.Presets.ActiveId == updateId && updates.Presets.ActiveName == "Version 2" && LootDistance(updates.Presets.ConfigForGame()) == 170 && updates.Presets.ZonesForGame().Contains("Version 2"), "Active addon update automatically applies config and maps without switching");
    Check(Equal(updateOtherMap, JsonNode.Parse(updates.Presets.ZonesForGame())!["Woods"]!.ToJsonString()), "Addon update preserves unspecified maps");
    Check(!updates.History.CanUndo && updates.Configs.GetPendingChanges().Count == 0 && updates.Zones.GetPendingMaps().Count == 0, "Addon update rebinds editor baselines and resets old history");
    File.WriteAllText(updateFile, "{");
    updates.Presets.RefreshAddons(force: true);
    Check(LootDistance(updates.Presets.ConfigForGame()) == 170 && updates.Presets.AddonErrors.Count == 1, "Incomplete addon update keeps last valid version");
    File.WriteAllText(updateFile, UpdatePayload("Version 3", 180));
    var restartedUpdate = Open("updates");
    Check(restartedUpdate.Presets.ActiveId == updateId && restartedUpdate.Presets.ActiveName == "Version 3" && LootDistance(restartedUpdate.Presets.ConfigForGame()) == 180, "Server restart loads a newer addon automatically");
    restartedUpdate.Configs.Config.Loot.DetectDistance = 200;
    File.WriteAllText(updateFile, UpdatePayload("Version 4", 190));
    restartedUpdate.Presets.RefreshAddons(force: true);
    Check(restartedUpdate.Presets.ActiveName == "Custom" && restartedUpdate.Configs.Config.Loot.DetectDistance == 200 && LootDistance(restartedUpdate.Presets.ConfigForGame()) == 180, "Addon update cannot discard a concurrent unsaved personal edit");
    restartedUpdate.Presets.Save();
    var editedAddonCopy = restartedUpdate.Presets.ActiveId;
    restartedUpdate.Presets.Switch(updateId);
    Check(LootDistance(restartedUpdate.Presets.ConfigForGame()) == 190 && restartedUpdate.Presets.ZonesForGame().Contains("Version 4"), "Updated addon remains selectable after personal copy protection");
    restartedUpdate.Presets.Switch(editedAddonCopy);
    Check(LootDistance(restartedUpdate.Presets.ConfigForGame()) == 200 && restartedUpdate.Presets.ZonesForGame().Contains("Version 3"), "Personal copy stays independent from addon updates");
    restartedUpdate.Presets.Switch(updateId);
    var updateLibrary = Path.Combine(root, "updates", "presets", "library.json");
    File.Move(updateLibrary, updateLibrary + ".held"); Directory.CreateDirectory(updateLibrary);
    File.WriteAllText(updateFile, UpdatePayload("Version 5", 205));
    restartedUpdate.Presets.RefreshAddons(force: true);
    Check(restartedUpdate.Presets.ActiveId == updateId && LootDistance(restartedUpdate.Presets.ConfigForGame()) == 190 && restartedUpdate.Presets.AddonErrors.Any(e => e.Contains("update could not be saved")), "Failed addon update keeps saved selection and reports the failure");
    Directory.Delete(updateLibrary); File.Move(updateLibrary + ".held", updateLibrary);
    restartedUpdate.Presets.RefreshAddons(force: true);
    Check(LootDistance(restartedUpdate.Presets.ConfigForGame()) == 205 && restartedUpdate.Presets.AddonErrors.Count == 0, "Addon update retries successfully after persistence is restored");
    File.WriteAllText(updateFile, UpdatePayload("Version 6", 215));
    await Task.Delay(5100); // exercise the real scan interval without an editor polling
    Check(LootDistance(restartedUpdate.Presets.ConfigForGame()) == 215 && restartedUpdate.Presets.ActiveName == "Version 6", "Client fetch detects addon updates while the editor is closed");

    var scene = Open("scene");
    scene.Zones.GetWorking("RezervBase");
    scene.Zones.RecordNativeFloors("RezervBase", new() { ["ZoneSubStorage"] = "base|bunkers" });
    Check(!scene.Presets.TrackEdits() && scene.Presets.ActiveId == "default", "Native metadata refresh is not a user edit");
    scene.Presets.Duplicate("Scene test");
    scene.Zones.GetWorking("RezervBase").CustomZones.Add(new() { Name = "Pending" });
    scene.Zones.RecordNativeFloors("RezervBase", new() { ["ZoneSubStorage"] = "bunkers" });
    Check(scene.Zones.GetPendingMaps().Count == 1 && scene.Zones.GetWorking("RezervBase").CustomZones.Any(z => z.Name == "Pending"), "Scene update retains pending edits in selected preset");
    scene.Presets.Save(); scene.Presets.Switch("default");
    Check(!scene.Presets.ZonesForGame().Contains("Pending"), "Switch does not carry custom zones between complete presets");

    var failure = Open("failure");
    failure.Presets.Duplicate("Failure test");
    var libraryPath = Path.Combine(root, "failure", "presets", "library.json");
    var previous = File.ReadAllText(libraryPath);
    var previousId = failure.Presets.ActiveId;
    File.Move(libraryPath, libraryPath + ".held");
    Directory.CreateDirectory(libraryPath);
    failure.Configs.Config.Loot.DetectDistance = 166;
    Reject(() => failure.Presets.Switch("default"), "Blocked persistence unexpectedly switched preset");
    Check(failure.Presets.ActiveId == previousId && LootDistance(failure.Presets.ConfigForGame()) == 80 && failure.Configs.Config.Loot.DetectDistance == 166, "Failed save retains active snapshot and pending edits");
    Check(File.ReadAllText(libraryPath + ".held") == previous, "Failed transaction preserves previous library");
    Directory.Delete(libraryPath); File.Move(libraryPath + ".held", libraryPath);
    failure.Presets.Save();
    Check(LootDistance(failure.Presets.ConfigForGame()) == 166, "Save can be retried after failure");
    File.WriteAllText(libraryPath, "{");
    var brokenLibrary = Open("failure");
    Check(brokenLibrary.Presets.Error != null && File.ReadAllText(libraryPath) == "{", "Malformed library is never overwritten");
    Reject(() => brokenLibrary.Presets.Save(), "Malformed library permitted overwrite");

    var brokenRoot = Path.Combine(root, "bad-legacy");
    Directory.CreateDirectory(brokenRoot);
    File.WriteAllText(Path.Combine(brokenRoot, "config.json"), "{");
    var badLegacy = Open("bad-legacy");
    Check(badLegacy.Presets.Error != null && File.ReadAllText(Path.Combine(brokenRoot, "config.json")) == "{" && !File.Exists(Path.Combine(brokenRoot, "presets", "library.json")), "Malformed legacy config blocks migration without loss");
    Reject(() => AddonDiscovery.Parse("""{"Format":"orbit-zones/1","Maps":{"../escape":{}}}""", "bad-map.json", clean.Zones), "Unsafe map ID accepted");
    Reject(() => AddonDiscovery.Parse("""{"Format":"orbit-zones/1","Maps":{"RezervBase":{"BuiltinZones":null}}}""", "null-map.json", clean.Zones), "Null zone collection accepted");
    Reject(() => AddonDiscovery.Parse("""{"Format":"orbit-zones/1","Maps":{"RezervBase":{"CustomZones":[{"FloorId":"nonexistent"}]}}}""", "bad-floor.json", clean.Zones), "Unknown floor accepted");
    Reject(() => AddonDiscovery.Parse("""{"config_version":99,"loot":{"detect_distance":60}}""", "future.json", clean.Zones), "Future config version accepted");
    var clamped = AddonDiscovery.Parse("""{"Format":"orbit-zones/1","Maps":{"RezervBase":{"CustomZones":[{"Radius":{"Min":-10,"Max":0},"Force":{"Min":-99,"Max":99}}]}}}""", "clamp.json", clean.Zones);
    Check(clamped.Maps["RezervBase"].CustomZones[0].Radius.Min == 10 && clamped.Maps["RezervBase"].CustomZones[0].Force.Max == 10, "Addon zone values use the existing pack import bounds");
    Console.WriteLine($"PASS: {checks} preset checks (production services, isolated files, no SPT host or raid)");
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(resolved).StartsWith("orbit-preset-tests-")) throw new InvalidOperationException("Unexpected test cleanup path.");
    Directory.Delete(resolved, recursive: true);
}

(ConfigService Configs, ZoneStoreService Zones, EditHistoryService History, PresetService Presets) Open(string name)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    var configs = new ConfigService(new TestLogger<ConfigService>()) { ModDirectory = directory };
    var zones = new ZoneStoreService(new TestLogger<ZoneStoreService>()) { ModDirectory = directory };
    var history = new EditHistoryService(configs, zones);
    var presets = new PresetService(configs, zones, history, new TestLogger<PresetService>());
    presets.Initialize();
    return (configs, zones, history, presets);
}
