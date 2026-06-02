using BepInEx.Configuration;
using Orbit.Helpers;

namespace Orbit.Looting;

public static class LootConfig
{
    public const LootingFaction LootingFactionsDefault =
        LootingFaction.Pmc | LootingFaction.Scav | LootingFaction.PlayerScav;

    public const int LootTimeout = 180;

    // PMC fallback when the SAIN personality system is OFF / unresolved.
    public const float ExtractAtLootValuePmc = 500_000f;

    public static ConfigEntry<LootingFaction> CorpseLootingEnabled;
    public static ConfigEntry<LootingFaction> ContainerLootingEnabled;
    public static ConfigEntry<LootingFaction> LooseItemLootingEnabled;
    public static ConfigEntry<float> DetectItemDistance;
    public static ConfigEntry<float> DetectContainerDistance;
    public static ConfigEntry<float> DetectCorpseDistance;
    public static ConfigEntry<bool> CorpseRequiresSightOrSquadKill;
    public static ConfigEntry<ExtractFaction> ExtractAllowedFor;
    public static ConfigEntry<float> ExtractAtLootValuePlayerScav;

    private static bool _initialized;

    public static void Init(ConfigFile config)
    {
        if (_initialized) return;
        _initialized = true;

        const string finder = "08. Looting (Finder)";
        const string settings = "09. Looting (Settings)";

        CorpseLootingEnabled = config.Bind(finder, "Enable corpse looting", LootingFactionsDefault,
            "Which factions can loot corpses. ORBIT modifies AI for these factions; other selections have no effect.");
        ContainerLootingEnabled = config.Bind(finder, "Enable container looting", LootingFactionsDefault,
            "Which factions can loot containers (jackets, weapon boxes, toolboxes…).");
        LooseItemLootingEnabled = config.Bind(finder, "Enable loose item looting", LootingFactionsDefault,
            "Which factions can pick up loose world items.");
        DetectCorpseDistance = config.Bind(finder, "Detect corpse distance (m)", 80f,
            "Max distance from squad leader at which a corpse POI is still considered. 0 = no cap.");
        DetectContainerDistance = config.Bind(finder, "Detect container distance (m)", 80f,
            "Max distance from squad leader at which a container POI is still considered. 0 = no cap.");
        DetectItemDistance = config.Bind(finder, "Detect loose item distance (m)", 80f,
            "Max distance from squad leader at which a loose-item POI is still considered. 0 = no cap.");
        CorpseRequiresSightOrSquadKill = config.Bind(finder, "Corpse requires LoS or squad kill", true,
            "When ON, a corpse POI is only assigned if the squad leader can see it OR the squad scored the kill. Stops bots magically knowing about corpses across the map.");
        ExtractAllowedFor = config.Bind(finder, "Extract allowed for", ExtractFaction.Pmc | ExtractFaction.PlayerScav,
            "Which factions are allowed to be routed to an exfil. Only PMC and PlayerScav have extract dispatch logic in ORBIT.");

        ExtractAtLootValuePlayerScav = config.Bind(settings, "PlayerScav: extract at loot value (₽)", 200000f,
            "Once a PlayerScav squad's living members have collectively looted this many roubles, the whole squad bee-lines to the nearest exfil. 0 disables.");

        Log.Info($"LootConfig.Init: DONE — containers={ContainerLootingEnabled.Value}, loose={LooseItemLootingEnabled.Value}, corpses={CorpseLootingEnabled.Value}");
    }
}
