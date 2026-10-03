#nullable disable
using System;
using System.Collections.Generic;

namespace Orbit.Settings;

public sealed class MovementSettings
{
    public Dictionary<string, float> SprintByType { get; set; } = Defaults();

    public static Dictionary<string, float> Defaults()
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var type in Orbit.Zones.ZoneBotTypes.Keys)
            if (type != "PMC") result[type] = type == "Scav" ? 0f : 0.5f;
        return result;
    }

    public float For(string type)
        => SprintByType != null && SprintByType.TryGetValue(type, out var value)
            && !float.IsNaN(value) && !float.IsInfinity(value) ? Math.Max(0f, Math.Min(1f, value))
            : type == "Scav" ? 0f : 0.5f;
}

// Null means inherit. Applied once per raid, global -> base map -> detected variant.
public sealed class MapBehaviorOverride
{
    public float? SleepDistance { get; set; }
    public float? WakeDistance { get; set; }
    public float? HostileWakeDistance { get; set; }
    public float? ScopedWakeMaxDistance { get; set; }
    public float? GuardDurationMin { get; set; }
    public float? GuardDurationMax { get; set; }
    public float? SyntheticGuardDurationMin { get; set; }
    public float? SyntheticGuardDurationMax { get; set; }
    public float? QuestWeightScale { get; set; }
    public float? KillsWeightScale { get; set; }
    public float? LootWeightScale { get; set; }

    public MapBehaviorOverride Overlay(MapBehaviorOverride child) => new MapBehaviorOverride
    {
        SleepDistance = child?.SleepDistance ?? SleepDistance,
        WakeDistance = child?.WakeDistance ?? WakeDistance,
        HostileWakeDistance = child?.HostileWakeDistance ?? HostileWakeDistance,
        ScopedWakeMaxDistance = child?.ScopedWakeMaxDistance ?? ScopedWakeMaxDistance,
        GuardDurationMin = child?.GuardDurationMin ?? GuardDurationMin,
        GuardDurationMax = child?.GuardDurationMax ?? GuardDurationMax,
        SyntheticGuardDurationMin = child?.SyntheticGuardDurationMin ?? SyntheticGuardDurationMin,
        SyntheticGuardDurationMax = child?.SyntheticGuardDurationMax ?? SyntheticGuardDurationMax,
        QuestWeightScale = child?.QuestWeightScale ?? QuestWeightScale,
        KillsWeightScale = child?.KillsWeightScale ?? KillsWeightScale,
        LootWeightScale = child?.LootWeightScale ?? LootWeightScale,
    };

    public static MapBehaviorOverride Resolve(Dictionary<string, MapBehaviorOverride> maps,
        string mapId, string variantKey, MapBehaviorOverride global)
    {
        var result = global.Overlay(Find(maps, mapId));
        return string.Equals(mapId, variantKey, StringComparison.OrdinalIgnoreCase)
            ? result : result.Overlay(Find(maps, variantKey));
    }

    private static MapBehaviorOverride Find(Dictionary<string, MapBehaviorOverride> maps, string key)
    {
        if (maps == null || key == null) return null;
        foreach (var pair in maps)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return null;
    }

    public void Validate()
    {
        Range(SleepDistance, 50, 1500, "Ghost sleep distance");
        Range(WakeDistance, 25, 1500, "Ghost wake distance");
        Range(HostileWakeDistance, 0, 150, "Hostile wake distance");
        Range(ScopedWakeMaxDistance, 200, 1500, "Scoped wake distance");
        Range(GuardDurationMin, 0, 1800, "Guard minimum");
        Range(GuardDurationMax, 0, 1800, "Guard maximum");
        Range(SyntheticGuardDurationMin, 0, 1800, "Patrol guard minimum");
        Range(SyntheticGuardDurationMax, 0, 1800, "Patrol guard maximum");
        Range(QuestWeightScale, 0, 5, "Quest weight scale");
        Range(KillsWeightScale, 0, 5, "Kills weight scale");
        Range(LootWeightScale, 0, 5, "Loot weight scale");
        if (WakeDistance > SleepDistance) throw new ArgumentException("Ghost wake distance must not exceed sleep distance.");
        if (GuardDurationMin > GuardDurationMax || SyntheticGuardDurationMin > SyntheticGuardDurationMax)
            throw new ArgumentException("Guard minimum must not exceed maximum.");
    }

    public static void Range(float? value, float min, float max, string label)
    {
        if (value.HasValue && (float.IsNaN(value.Value) || float.IsInfinity(value.Value) || value < min || value > max))
            throw new ArgumentException($"{label} must be between {min} and {max}.");
    }
}
