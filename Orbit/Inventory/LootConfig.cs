using BepInEx.Configuration;
using Orbit.Helpers;

namespace Orbit.Inventory;

/// <summary>
/// Loot-subsystem configuration anchor + static handles. Constants block
/// at the top is locked at the values we want to ship — they used to be
/// F12 knobs but stayed at one setting in practice. F12 entries below
/// are the ones that influence raid-to-raid tuning (loot reach, faction
/// toggles, corpse LoS, per-faction extract thresholds, scav item value
/// floors/ceilings).
///
/// Phase 5a declares the entries; Phase 6 will wire the actual
/// <c>Config.Bind</c> calls in <c>Plugin.Awake</c>.
/// </summary>
public static class LootConfig
{
    // Default: PMCs, scavs, player-scavs loot. Goons are opt-in
    // (surfaced in the F12 mask but disabled by default — vanilla
    // Tarkov has them kill-focused, not loot-focused).
    public const LootingFaction LootingFactionsDefault =
        LootingFaction.Pmc | LootingFaction.Scav | LootingFaction.PlayerScav;

    public const EquipmentType CanPickupEquipmentDefaults =
        EquipmentType.ArmoredRig
        | EquipmentType.Chest
        | EquipmentType.Backpack
        | EquipmentType.Grenade
        | EquipmentType.Helmet
        | EquipmentType.TacticalRig
        | EquipmentType.Weapon
        | EquipmentType.Dogtag
        | EquipmentType.Earpiece
        | EquipmentType.FaceCover
        | EquipmentType.Eyewear
        | EquipmentType.Armband;

    // ── Hardcoded constants (previously F12 knobs, locked at shipping values) ──
    public const bool BotsAlwaysCloseContainers = true;
    public const double TransactionDelay = 3000D;
    public const bool UseExamineTime = true;
    public const bool ValueFromMods = true;
    public const bool CanStripAttachments = true;
    public const int LootTimeout = 180;
    public const CanEquipEquipmentType PMCGearToEquip = CanEquipEquipmentType.All;
    public const CanEquipEquipmentType ScavGearToEquip = CanEquipEquipmentType.All;
    public const EquipmentType PMCGearToPickup = CanPickupEquipmentDefaults;
    public const EquipmentType ScavGearToPickup = CanPickupEquipmentDefaults;

    // PMC fallback knobs. PMC squads with SAIN personality resolve their
    // thresholds from the per-archetype Vector2 ranges in section 07.1-5.
    // These constants are the fallback used by non-SAIN PMCs or when the
    // personality system is OFF / unresolved.
    public const float ExtractAtLootValuePmc = 500_000f;
    public const float PMCMinLootThreshold = 10_000f;
    public const float PMCMaxLootThreshold = 0f;

    // ── F12 entries — Loot Finder (which factions / how far / corpse LoS) ──
    public static ConfigEntry<LootingFaction> CorpseLootingEnabled;
    public static ConfigEntry<LootingFaction> ContainerLootingEnabled;
    public static ConfigEntry<LootingFaction> LooseItemLootingEnabled;
    public static ConfigEntry<float> DetectItemDistance;
    public static ConfigEntry<float> DetectContainerDistance;
    public static ConfigEntry<float> DetectCorpseDistance;
    public static ConfigEntry<bool> CorpseRequiresSightOrSquadKill;
    public static ConfigEntry<ExtractFaction> ExtractAllowedFor;

    // ── F12 entries — Loot Settings (PlayerScav + scav only; PMC uses SAIN) ──
    public static ConfigEntry<float> ExtractAtLootValuePlayerScav;
    public static ConfigEntry<float> ScavMinLootThreshold;
    public static ConfigEntry<float> ScavMaxLootThreshold;

    /// <summary>
    /// Shared valuator instance. Constructed by <see cref="Init"/>; the
    /// handbook prices it reads load asynchronously (see
    /// <c>ItemValuator.UpdatePricesAsync</c>) so the instance is safe to
    /// query immediately but will return 0 for everything until the
    /// async update fires. Call sites use null-conditional access to
    /// degrade gracefully if Init wasn't run.
    /// </summary>
    public static ItemValuator ItemValuator;

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
        ScavMinLootThreshold = config.Bind(settings, "Scav: min item value (₽)", 5000f,
            "Floor below which a scav bot ignores a candidate item. 0 = grab anything.");
        ScavMaxLootThreshold = config.Bind(settings, "Scav: max item value (₽)", 0f,
            "Ceiling above which a scav bot skips an item. 0 = no ceiling.");

        ItemValuator = new ItemValuator();

        Log.Info($"LootConfig.Init: DONE — containers={ContainerLootingEnabled.Value}, loose={LooseItemLootingEnabled.Value}, corpses={CorpseLootingEnabled.Value}, distContainer={DetectContainerDistance.Value}m, distItem={DetectItemDistance.Value}m, distCorpse={DetectCorpseDistance.Value}m");
    }
}
