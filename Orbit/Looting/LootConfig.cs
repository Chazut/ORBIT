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

        const string section = "04. Looting";

        LootingEnabled = config.Bind("01. Essentials", "Enable looting", LootingFactionsDefault,
            new ConfigDescription(
                "Which factions loot containers, loose items and corpses. Only the listed factions are affected.",
                null, new Orbit.Config.ConfigurationManagerAttributes { Order = 10 }));
        DetectDistance = config.Bind(section, "Detect loot distance (m)", 80f,
            new ConfigDescription(
                "How far from the squad leader a loot spot (container / item / corpse) is still considered. 0 = no limit.",
                null, new Orbit.Config.ConfigurationManagerAttributes { Order = 6 }));
        CorpseRequiresSightOrSquadKill = config.Bind(section, "Corpse requires LoS or squad kill", true,
            new ConfigDescription(
                "ON (default): a corpse is only looted if the squad saw it or made the kill, so bots don't magically know about bodies across the map.",
                null, new Orbit.Config.ConfigurationManagerAttributes { Order = 5 }));
        ExtractAllowedFor = config.Bind(section, "Extract allowed for", ExtractFaction.Pmc | ExtractFaction.PlayerScav,
            new ConfigDescription(
                "Which factions ORBIT routes to an exfil. Gates EVERY extract — the squad loot/time triggers AND the solo emergency (wounded) and loot-threshold extracts. None = nobody extracts. Only PMC and PlayerScav have extract logic.",
                null, new Orbit.Config.ConfigurationManagerAttributes { Order = 4 }));
        ExtractAtLootValuePlayerScav = config.Bind("03. PlayerScav", "Extract at loot value (₽)", 200000f,
            new ConfigDescription(
                "Once a PlayerScav squad has looted this much (₽ across living members), it heads for the nearest exfil. 0 = never.",
                null, new Orbit.Config.ConfigurationManagerAttributes { Order = 60 }));
        SoloLootExtractChancePct = config.Bind(section, "Solo extract on own loot threshold (%)", 50,
            new ConfigDescription(
                "When a PMC/PlayerScav's OWN loot crosses its OWN extract threshold, the chance it leaves ALONE while the squad keeps playing (rolled once per member). 0 = stay with squad, 100 = always leave. (Wounded emergency extract is separate, in Essentials.)",
                new AcceptableValueRange<int>(0, 100), new Orbit.Config.ConfigurationManagerAttributes { Order = 2 }));
        // Bound into "01. Essentials" (a behaviour toggle, grouped with Squad rally), not the Looting section.
        EmergencyExtractEnabled = config.Bind("01. Essentials", "Emergency extract when wounded", true,
            new ConfigDescription(
                "ON (default): a badly wounded PMC / PlayerScav with no usable meds left breaks off and extracts on its own instead of dying where it stands, and rejoins the squad if it heals back up. OFF: members only leave on the loot / time / squad triggers.",
                null, new Orbit.Config.ConfigurationManagerAttributes { Order = 8 }));
        ScavLootChancePct = config.Bind(section, "Scav: per-item loot chance (%)", 30,
            new ConfigDescription(
                "Bot scavs (not PlayerScavs) skip the value gate and just roll this chance per item, like vanilla opportunistic scavs. PMCs/PlayerScavs unaffected.",
                new AcceptableValueRange<int>(0, 100), new Orbit.Config.ConfigurationManagerAttributes { Order = 1 }));
        Log.Info($"LootConfig.Init: DONE — looting={LootingEnabled.Value}, detectDist={DetectDistance.Value}m, swapMargin={SwapMargin:F2} (const)");
    }
}
