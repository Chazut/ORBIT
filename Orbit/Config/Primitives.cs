using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using UnityEngine;

namespace Orbit.Config;

/// <summary>
/// Inclusive [Min..Max] range with sample helpers — used widely across
/// config (loot thresholds, roam durations, etc.). Gaussian sampling
/// targets ~99.7% of values in-range via 6-sigma scaling, then clamps to
/// be safe.
/// </summary>
public struct Range(float min, float max)
{
    public static Range Zero => new(0f, 0f);
    public static Range ZeroOne => new(0f, 1f);

    [JsonRequired] public float Min { get; set; } = min;
    [JsonRequired] public float Max { get; set; } = max;

    public float SampleGaussian()
    {
        // Box-Muller transform using Unity's RNG.
        var u1 = 1.0f - Random.value;
        var u2 = 1.0f - Random.value;
        var stdNormal = Mathf.Sqrt(-2.0f * Mathf.Log(u1)) * Mathf.Sin(2.0f * Mathf.PI * u2);

        // Scale to range: sigma ≈ (max-min)/6 so ~99.7% of samples land in [min, max].
        var mean = (Min + Max) * 0.5f;
        var sigma = (Max - Min) / 6.0f;

        return Mathf.Clamp(mean + stdNormal * sigma, Min, Max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float SampleUniform() => Random.Range(Min, Max);
}
