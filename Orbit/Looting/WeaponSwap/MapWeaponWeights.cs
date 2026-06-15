using System.Collections.Generic;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Per-map weighting profile applied to the weapon-score formula. Long maps favour effective distance and
/// ammo penetration over ergo; CQC maps invert that. Resolved once at swap time from
/// <c>OrbitManager.MapId</c>.
/// </summary>
public readonly struct WeaponWeights
{
    public readonly float Ergo;
    public readonly float Recoil;
    public readonly float EffectiveDist;
    public readonly float AmmoQuality;

    public WeaponWeights(float ergo, float recoil, float effectiveDist, float ammoQuality)
    {
        Ergo = ergo;
        Recoil = recoil;
        EffectiveDist = effectiveDist;
        AmmoQuality = ammoQuality;
    }
}

public static class MapWeaponWeights
{
    private static readonly WeaponWeights LongRange = new(ergo: 0.5f, recoil: 0.2f, effectiveDist: 0.25f, ammoQuality: 1.5f);
    private static readonly WeaponWeights Balanced = new(ergo: 1.0f, recoil: 0.3f, effectiveDist: 0.1f, ammoQuality: 1.0f);
    private static readonly WeaponWeights CloseQuarters = new(ergo: 1.5f, recoil: 0.5f, effectiveDist: 0.02f, ammoQuality: 0.8f);

    // Map id strings come from GameWorld.LocationId. Substring matched lowercase so map variants (e.g.
    // customs_preset) still resolve.
    private static readonly Dictionary<string, WeaponWeights> Profiles = new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "lighthouse", LongRange },
        { "woods", LongRange },

        { "bigmap", Balanced },        // Customs
        { "rezervbase", Balanced },    // Reserve
        { "tarkovstreets", Balanced },
        { "shoreline", Balanced },     // long sightlines outside but Resort fights dominate engagement count
        { "interchange", Balanced },   // parking/exterior medium-long, interior short-long

        { "factory4_day", CloseQuarters },
        { "factory4_night", CloseQuarters },
        { "laboratory", CloseQuarters },
        { "sandbox", CloseQuarters },      // Ground Zero
        { "sandbox_high", CloseQuarters },
    };

    public static WeaponWeights Resolve(string mapId)
    {
        if (string.IsNullOrEmpty(mapId)) return Balanced;
        if (Profiles.TryGetValue(mapId, out var weights)) return weights;
        // Best-effort substring match for unrecognised location ids.
        var lower = mapId.ToLowerInvariant();
        foreach (var kv in Profiles)
            if (lower.Contains(kv.Key)) return kv.Value;
        return Balanced;
    }
}
