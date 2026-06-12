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
    public static ConfigEntry<float> WeaponSwapMargin;
    public static ConfigEntry<bool> WeaponSwapEnabled;
    public static ConfigEntry<float> ArmorSwapMargin;
    public static ConfigEntry<bool> ArmorSwapEnabled;
    public static ConfigEntry<float> RigSwapMargin;
    public static ConfigEntry<bool> RigSwapEnabled;
    public static ConfigEntry<float> BackpackSwapMargin;
    public static ConfigEntry<bool> BackpackSwapEnabled;
    public static ConfigEntry<float> HeadsetSwapMargin;
    public static ConfigEntry<bool> HeadsetSwapEnabled;
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
        WeaponSwapMargin = config.Bind(section, "Weapon swap margin", 1.10f,
            new ConfigDescription(
                "PMC / PlayerScav swap threshold: a candidate weapon must score this many times its target slot's current weapon to trigger a swap. 1.00 = swap on any improvement, 1.20 = swap only on a 20% better score. Scavs ignore this — they only equip into empty slots.",
                new AcceptableValueRange<float>(1.0f, 2.0f)));
        WeaponSwapEnabled = config.Bind(section, "Enable weapon swap (Phase 1)", true,
            "Master toggle for the in-raid weapon swap layer. When OFF, looted weapons fall back to the default placement path (equip into empty slot or stash in inventory). Useful for isolating swap-related issues during testing.");
        ArmorSwapMargin = config.Bind(section, "Armor swap margin", 1.10f,
            new ConfigDescription(
                "PMC / PlayerScav body-armor and helmet swap threshold. A candidate's score (driven by armor class) must exceed this multiple of the currently-equipped item's score to trigger a swap. 1.00 = swap on any improvement; 1.20 = swap only on a 20% better score. Scavs ignore this — they only equip into empty slots.",
                new AcceptableValueRange<float>(1.0f, 2.0f)));
        ArmorSwapEnabled = config.Bind(section, "Enable armor & helmet swap (Phase 2)", true,
            "Master toggle for the in-raid armor / helmet swap layer. When OFF, looted armor and helmets fall back to the default placement path. Independent of the weapon swap toggle.");
        RigSwapMargin = config.Bind(section, "Rig swap margin", 1.10f,
            new ConfigDescription(
                "PMC / PlayerScav body-rig swap threshold (simple-rig ↔ simple-rig and armored-rig ↔ armored-rig). Candidate score (cells + armor class) must exceed this multiple of the currently-equipped rig's score to trigger a swap. Cross-type swaps (simple ↔ armored) are never attempted. Scavs ignore this — they only equip into empty slots.",
                new AcceptableValueRange<float>(1.0f, 2.0f)));
        RigSwapEnabled = config.Bind(section, "Enable rig swap (Phase 3)", true,
            "Master toggle for the in-raid TacticalVest swap layer. When OFF, looted rigs fall back to the default placement path. Independent of the weapon and armor toggles.");
        BackpackSwapMargin = config.Bind(section, "Backpack swap margin", 1.10f,
            new ConfigDescription(
                "PMC / PlayerScav backpack swap threshold. Candidate score (grid cells, price tie-breaker) must exceed this multiple of the currently-equipped bag's score to trigger a swap. The bot's carry is transferred into the new bag before the exchange; the swap is skipped when it wouldn't fit. Scavs ignore this — they only equip into empty slots.",
                new AcceptableValueRange<float>(1.0f, 2.0f)));
        BackpackSwapEnabled = config.Bind(section, "Enable backpack swap", true,
            "Master toggle for the in-raid backpack swap layer. When OFF, looted backpacks fall back to the default placement path. Independent of the other swap toggles.");
        HeadsetSwapMargin = config.Bind(section, "Headset swap margin", 1.10f,
            new ConfigDescription(
                "PMC / PlayerScav audio-headset (Earpiece) swap threshold. Candidate handbook price must exceed this multiple of the currently-equipped headset's price to trigger a swap. Scavs ignore this — they only equip into empty slots.",
                new AcceptableValueRange<float>(1.0f, 2.0f)));
        HeadsetSwapEnabled = config.Bind(section, "Enable headset swap", true,
            "Master toggle for the in-raid audio-headset swap layer. When OFF, looted headsets fall back to the default placement path. Independent of the other swap toggles.");
        WeaponStripMinPricePerSlot = config.Bind(section, "Weapon strip min price/slot (₽)", 10000f,
            new ConfigDescription(
                "Phase 5 — when a swap is about to send the bot's old weapon to the corpse, mods on that weapon whose per-slot price exceeds this threshold are stripped into the bot's bag first (so a 300k thermal scope doesn't fall with a beat-up AKM). Mags whose caliber matches a post-swap weapon also strip regardless of price. Set lower to be greedier with stripping, higher to skip cheaper mods.",
                new AcceptableValueRange<float>(0f, 100000f)));
        WeaponStripEnabled = config.Bind(section, "Enable weapon strip on discard (Phase 5)", true,
            "Master toggle for the strip-before-discard layer. When OFF, displaced weapons go to the corpse with all their mods intact (1.0.x behaviour).");

        Log.Info($"LootConfig.Init: DONE — looting={LootingEnabled.Value}, detectDist={DetectDistance.Value}m, weaponSwapMargin={WeaponSwapMargin.Value:F2}");
    }
}
