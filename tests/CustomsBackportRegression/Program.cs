using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Helpers;
using Orbit.Server.Config;
using Orbit.Server.Presets;
using Orbit.Server.Web;
using Orbit.Server.Zones;
using Orbit.Settings;
using Orbit.Zones;
using SPTarkov.Common.Models.Logging;

const string classic = CustomsBackport.BaseId, variant = CustomsBackport.Key;
var checks = 0;
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    checks++;
}
Check(MapVariants.Detect(classic) == "", "Installed but inactive backport selects vanilla");
Fums.Customs.Client.BundleSceneLoader.Active = true;
Check(MapVariants.ZoneKey("BigMap", MapVariants.Detect("BigMap")) == variant, "Active replacement selects its own zones");
Check(MapVariants.Detect("Woods") == "", "The replacement cannot tag other maps");
Fums.Customs.Client.BundleSceneLoader.Active = false;
Check(MapVariants.Detect(classic) == "", "Next vanilla raid uses vanilla zones");
Check(MapViews.Views[variant] == MapViews.Views[classic] && FloorCatalog.For(variant).Find("3rd-floor") != null,
    "Editor projection and authored floors available");
var full = new RushSettings { Maps = new() { [classic] = [] } };
for (var i = 1; i < 40; i++) full.Maps.Add("extra-" + i, []);
RushDefaults.SeedCustomsBackport(full.Maps);
full.Validate();
Check(full.Maps.Count == 41, "An existing full catalogue still fits after migration");

var dir = Directory.CreateTempSubdirectory("orbit-customs-backport-");
try
{
    (PresetService Presets, ConfigService Config, ZoneStoreService Zones) Boot()
    {
        var config = new ConfigService(new Logger<ConfigService>()) { ModDirectory = dir.FullName };
        var zones = new ZoneStoreService(new Logger<ZoneStoreService>()) { ModDirectory = dir.FullName };
        var presets = new PresetService(config, zones, new EditHistoryService(config, zones), new Logger<PresetService>());
        presets.Initialize();
        Check(presets.Error == null && presets.MirrorWarning == null, presets.Error ?? presets.MirrorWarning ?? "Startup");
        return (presets, config, zones);
    }
    var configFile = Path.Combine(dir.FullName, "config.json");
    var zoneDir = dir.CreateSubdirectory("zones");
    var custom = new MapZoneModel { CustomZones = [new() { Name = "My upper floor", FloorId = "3rd-floor" }] };
    var oldConfig = JsonNode.Parse(ConfigService.DefaultJson)!;
    var maps = oldConfig["rush"]!["maps"]!.AsObject();
    maps.Remove(variant);
    maps[classic]!.AsArray().Add(JsonSerializer.SerializeToNode(new RushPoint { Id = "my-site", Name = "My custom site" },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    File.WriteAllText(configFile, oldConfig.ToJsonString());
    File.WriteAllText(Path.Combine(zoneDir.FullName, classic + ".json"), JsonSerializer.Serialize(custom));
    var service = Boot();
    var source = service.Config.Config.Rush.Maps[classic];
    var copy = service.Config.Config.Rush.Maps[variant];
    Check(source.Any(p => p.Kind == "Sniper") && copy.Any(p => p.Id == "my-site")
        && JsonSerializer.Serialize(source) == JsonSerializer.Serialize(copy) && !ReferenceEquals(source[0], copy[0]),
        "Legacy startup copies every field and kind from the user's catalogue");
    Check(service.Zones.GetZones(variant).CustomZones.Single().FloorId == "3rd-floor", "Legacy zones retain floor filters");
    Check(File.Exists(Path.Combine(zoneDir.FullName, variant + ".json")), "Variant mirror created at startup");

    var libraryFile = Path.Combine(dir.FullName, "presets", "library.json");
    var library = JsonNode.Parse(File.ReadAllText(libraryFile))!;
    var saved = library["Presets"]![0]!["Snapshot"]!;
    saved["Config"]!["rush"]!["maps"]!.AsObject().Remove(variant);
    saved["Maps"]!.AsObject().Remove(variant);
    var inactive = library["Presets"]![0]!.DeepClone();
    inactive["Id"] = "local:inactive";
    inactive["Name"] = "Other";
    inactive["Snapshot"]!["Maps"]![classic]!["CustomZones"]![0]!["Name"] = "Other preset";
    inactive["Snapshot"]!["Config"]!["rush"]!["maps"]![variant] = new JsonArray();
    library["Presets"]!.AsArray().Add(inactive);
    File.WriteAllText(libraryFile, library.ToJsonString());
    service = Boot();
    var migrated = JsonNode.Parse(File.ReadAllText(libraryFile))!["Presets"]!;
    Check(migrated[0]!["Snapshot"]!["Config"]!["rush"]!["maps"]![variant]!.AsArray().Count == source.Count,
        "Existing library persists the point copy at startup");
    Check(migrated[1]!["Snapshot"]!["Maps"]![variant]!["CustomZones"]![0]!["Name"]!.GetValue<string>() == "Other preset"
        && migrated[1]!["Snapshot"]!["Config"]!["rush"]!["maps"]![variant]!.AsArray().Count == 0,
        "Inactive preset seeds its own zones and preserves an explicitly empty catalogue");
    service.Config.Config.Rush.Maps[variant].Clear();
    service.Zones.GetWorking(variant).CustomZones.Clear();
    service.Presets.Save();
    var persisted = File.ReadAllText(libraryFile);
    service = Boot();
    Check(persisted == File.ReadAllText(libraryFile), "Second boot leaves the library untouched");
    Check(service.Config.Config.Rush.Maps[variant].Count == 0 && service.Config.Config.Rush.Maps[classic].Count == source.Count
        && service.Zones.GetZones(variant).CustomZones.Count == 0 && service.Zones.GetZones(classic).CustomZones.Count == 1,
        "Deletes survive restart and do not affect classic Customs");

    var addonFile = Path.Combine(service.Presets.AddonDirectory, "Customs pack.json");
    var addon = new ZonePackModel { Maps = new() { [classic] = custom },
        RushPoints = new() { [classic] = [new() { Id = "from-addon", Name = "Addon point" }] } };
    File.WriteAllText(addonFile, JsonSerializer.Serialize(addon));
    service.Presets.RefreshAddons(true);
    service.Presets.Switch(service.Presets.Choices.Single(c => c.Id.StartsWith("addon:")).Id);
    Check(service.Config.Config.Rush.Maps[variant].Single().Id == "from-addon" && service.Zones.GetZones(variant).CustomZones.Count == 1,
        "Selected old addon seeds its own variant");
    addon.Maps[classic].CustomZones.Clear();
    addon.RushPoints[classic].Clear();
    File.WriteAllText(addonFile, JsonSerializer.Serialize(addon));
    service.Presets.RefreshAddons(true);
    Check(service.Config.Config.Rush.Maps[classic].Count == 0 && service.Config.Config.Rush.Maps[variant].Count == 1
        && service.Zones.GetZones(classic).CustomZones.Count == 0 && service.Zones.GetZones(variant).CustomZones.Count == 1,
        "An addon update limited to classic Customs leaves the variant alone");
    Console.WriteLine($"PASS {checks} Customs backport checks");
}
finally { dir.Delete(recursive: true); }
