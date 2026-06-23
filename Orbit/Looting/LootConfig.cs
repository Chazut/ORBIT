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
    public static ConfigEntry<int> SoloLootExtractChancePct;
    public static ConfigEntry<bool> EmergencyExtractEnabled;
    public static ConfigEntry<int> ScavLootChancePct;
    /// <summary>
    /// Swap threshold shared by every gear kind (weapon / armor / helmet / rig / backpack / headset):
    /// a candidate must score this multiple of the currently-equipped item to trigger a swap. The
    /// per-kind F12 toggles + margins were removed on purpose — the swap layer is always on, this is
    /// a design constant, not a tunable.
    /// </summary>
    public const float SwapMargin = 1.10f;

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
        SoloLootExtractChancePct = config.Bind(section, "Solo extract on own loot threshold (%)", 50,
            new ConfigDescription(
                "When a PMC / PlayerScav member's OWN looted value crosses its OWN extract threshold, the chance it peels off to extract ALONE while the rest of the squad keeps playing. Rolled once per member. 0 = never (it stays with the squad), 100 = always. (The emergency wounded extract is a separate toggle below.)",
                new AcceptableValueRange<int>(0, 100)));
        EmergencyExtractEnabled = config.Bind(section, "Emergency extract when wounded", true,
            "ON (default): a PMC / PlayerScav whose HP is actively bleeding out (a sustained drop with no recovery) or stuck below 50% for a full minute peels off to extract alone, and cancels if it heals back up. OFF: members never self-extract on health — they only leave on the loot / time / squad triggers.");
        ScavLootChancePct = config.Bind(section, "Scav: per-item loot chance (%)", 30,
            new ConfigDescription(
                "Bot scavs (NOT PlayerScavs) skip the per-archetype loot-value gate entirely and instead roll this chance per item on corpses, containers, and loose loot. Mirrors vanilla scav behaviour — opportunistic pickups, not deliberate searches. PlayerScavs and PMCs are unaffected.",
                new AcceptableValueRange<int>(0, 100)));
        Log.Info($"LootConfig.Init: DONE — looting={LootingEnabled.Value}, detectDist={DetectDistance.Value}m, swapMargin={SwapMargin:F2} (const)");
    }
}
