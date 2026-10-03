using Orbit.Settings;
using Orbit.Server.Zones;

namespace Orbit.Server.Config;

public static class BehaviorValidation
{
    public static MapBehaviorOverride Global(OrbitServerConfig c) => new()
    {
        SleepDistance = c.GhostMode.SleepDistance, WakeDistance = c.GhostMode.WakeDistance,
        HostileWakeDistance = c.GhostMode.HostileWakeDistance, ScopedWakeMaxDistance = c.GhostMode.ScopedWakeMaxDistance,
        GuardDurationMin = c.PoiGuard.GuardDurationMin, GuardDurationMax = c.PoiGuard.GuardDurationMax,
        SyntheticGuardDurationMin = c.PoiGuard.SyntheticGuardDurationMin, SyntheticGuardDurationMax = c.PoiGuard.SyntheticGuardDurationMax,
        QuestWeightScale = c.MainObjectives.QuestWeightScale, KillsWeightScale = c.MainObjectives.KillsWeightScale,
        LootWeightScale = c.MainObjectives.LootWeightScale,
    };

    public static void Validate(OrbitServerConfig c)
    {
        if (c.Movement?.SprintByType == null || c.MapOverrides == null)
            throw new InvalidDataException("Movement and map overrides must be objects.");
        foreach (var pair in c.Movement.SprintByType)
        {
            if (pair.Key == "PMC" || !Orbit.Zones.ZoneBotTypes.Keys.Contains(pair.Key))
                throw new InvalidDataException($"Unknown sprint category: {pair.Key}");
            MapBehaviorOverride.Range(pair.Value, 0, 1, $"{pair.Key} sprint propensity");
        }
        MapBehaviorOverride.Range(c.MainObjectives.QuestWeightScale, 0, 5, "Quest weight scale");
        MapBehaviorOverride.Range(c.MainObjectives.KillsWeightScale, 0, 5, "Kills weight scale");
        MapBehaviorOverride.Range(c.MainObjectives.LootWeightScale, 0, 5, "Loot weight scale");
        foreach (var pair in c.MapOverrides)
        {
            if (!ZoneStoreService.MapIds.Contains(pair.Key) || pair.Value == null)
                throw new InvalidDataException($"Unknown or empty map override: {pair.Key}");
            pair.Value.Validate();
            // Validate inherited pairs too, including changes to a base map used by a variant.
            MapBehaviorOverride.Resolve(c.MapOverrides, ZoneStoreService.BaseMapId(pair.Key), pair.Key, Global(c)).Validate();
        }
    }
}
