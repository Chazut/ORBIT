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
    public static ConfigEntry<int> ScavLootChancePct;
    /// <summary>
    /// Swap threshold shared by every gear kind (weapon / armor / helmet / rig / backpack / headset):
    /// a candidate must score this multiple of the currently-equipped item to trigger a swap. The
    /// per-kind F12 toggles + margins were removed on purpose — the swap layer is always on, this is
    /// a design constant, not a tunable.
    /// </summary>
    public const float SwapMargin = 1.10f;

    public static ConfigEntry<float> WeaponStripMinPricePerSlot;
    public static ConfigEntry<bool> WeaponStripEnabled;

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
        ScavLootChancePct = config.Bind(section, "Scav: per-item loot chance (%)", 30,
            new ConfigDescription(
                "Bot scavs (NOT PlayerScavs) skip the per-archetype loot-value gate entirely and instead roll this chance per item on corpses, containers, and loose loot. Mirrors vanilla scav behaviour — opportunistic pickups, not deliberate searches. PlayerScavs and PMCs are unaffected.",
                new AcceptableValueRange<int>(0, 100)));
        WeaponStripMinPricePerSlot = config.Bind(section, "Weapon strip min price/slot (₽)", 10000f,
            new ConfigDescription(
                "Phase 5 — when a swap is about to send the bot's old weapon to the corpse, mods on that weapon whose per-slot price exceeds this threshold are stripped into the bot's bag first (so a 300k thermal scope doesn't fall with a beat-up AKM). Mags whose caliber matches a post-swap weapon also strip regardless of price. Set lower to be greedier with stripping, higher to skip cheaper mods.",
                new AcceptableValueRange<float>(0f, 100000f)));
        WeaponStripEnabled = config.Bind(section, "Enable weapon strip on discard (Phase 5)", true,
            "Master toggle for the strip-before-discard layer. When OFF, displaced weapons go to the corpse with all their mods intact (1.0.x behaviour).");

        Log.Info($"LootConfig.Init: DONE — looting={LootingEnabled.Value}, detectDist={DetectDistance.Value}m, swapMargin={SwapMargin:F2} (const)");
    }
}
