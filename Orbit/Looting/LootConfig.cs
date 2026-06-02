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

    public static ConfigEntry<LootingFaction> LootingEnabled;
    public static ConfigEntry<float> DetectDistance;
    public static ConfigEntry<bool> CorpseRequiresSightOrSquadKill;
    public static ConfigEntry<ExtractFaction> ExtractAllowedFor;
    public static ConfigEntry<float> ExtractAtLootValuePlayerScav;

    private static bool _initialized;

    public static void Init(ConfigFile config)
    {
        if (_initialized) return;
        _initialized = true;

        const string section = "08. Looting";

        LootingEnabled = config.Bind(section, "Enable looting", LootingFactionsDefault,
            "Which factions can loot (containers, loose items, corpses). ORBIT modifies AI for these factions; other selections have no effect.");
        DetectDistance = config.Bind(section, "Detect loot distance (m)", 80f,
            "Max distance from squad leader at which a loot POI (container / loose item / corpse) is still considered. 0 = no cap.");
        CorpseRequiresSightOrSquadKill = config.Bind(section, "Corpse requires LoS or squad kill", true,
            "When ON, a corpse POI is only assigned if the squad leader can see it OR the squad scored the kill. Stops bots magically knowing about corpses across the map.");
        ExtractAllowedFor = config.Bind(section, "Extract allowed for", ExtractFaction.Pmc | ExtractFaction.PlayerScav,
            "Which factions are allowed to be routed to an exfil. Only PMC and PlayerScav have extract dispatch logic in ORBIT.");
        ExtractAtLootValuePlayerScav = config.Bind(section, "PlayerScav: extract at loot value (₽)", 200000f,
            "Once a PlayerScav squad's living members have collectively looted this many roubles, the whole squad bee-lines to the nearest exfil. 0 disables.");

        Log.Info($"LootConfig.Init: DONE — looting={LootingEnabled.Value}, detectDist={DetectDistance.Value}m");
    }
}
