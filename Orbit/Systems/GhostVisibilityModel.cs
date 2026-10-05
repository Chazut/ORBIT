using UnityEngine;

namespace Orbit.Systems;

// Weather modifies the clear-weather reach once. Native floors belong to the native
// baseline, not to the final Ghost reach, where they could erase darkness or fog.
internal static class GhostVisibilityModel
{
    internal readonly struct Result(float reach, float acquisition)
    {
        internal readonly float Reach = reach;
        internal readonly float Acquisition = acquisition;
    }

    internal static float WeatherFraction(float baseline, float coefficient, float minimum)
    {
        if (!(baseline > 0f)) return Mathf.Clamp01(coefficient);
        var limited = Mathf.Clamp(baseline * Mathf.Clamp01(coefficient), Mathf.Min(minimum, baseline), baseline);
        return Mathf.Clamp01(limited / baseline);
    }

    internal static Result Calculate(float rawReach, float darkness, bool nightCapable,
        float baseline, float timeMultiplier, float weather, float minimum, float acquisition)
    {
        var nightReach = nightCapable ? rawReach : Mathf.Lerp(rawReach, Mathf.Min(rawReach, 35f), Mathf.Clamp01(darkness));
        // Two night models are limits, not two multipliers. The established Ghost night
        // limit remains in force even if a native minimum or a permissive preset is higher.
        if (!nightCapable && timeMultiplier < 1f && baseline > 0f)
            nightReach = Mathf.Min(nightReach, baseline * Mathf.Clamp01(timeMultiplier));
        return new Result(nightReach * WeatherFraction(baseline, weather, minimum), Mathf.Clamp01(acquisition));
    }
}
