using System;

namespace Orbit.Navigation;

internal static class PreparedExtractionChoice
{
    internal static float Chance(float personalityChance, float multiplier, float nearbyDistance,
        float ordinaryRoute, float preparedEstimate)
    {
        // An unavailable or partial ordinary route is not a usable nearby alternative.
        if (float.IsNaN(ordinaryRoute) || float.IsInfinity(ordinaryRoute) || ordinaryRoute < 0)
            return personalityChance * multiplier;
        // Allow a small difference in route estimates so a nearby prepared exit remains sensible.
        if (nearbyDistance > 0 && ordinaryRoute <= nearbyDistance && preparedEstimate > ordinaryRoute + 25f)
            return 0;
        // Prepared distance is a cheap lower bound through the unfinished steps. A large
        // detour reduces its chance, but keeps variety when no ordinary exit is right beside us.
        var detour = preparedEstimate > 0 ? Math.Max(.35f, Math.Min(1f, ordinaryRoute / preparedEstimate)) : 1f;
        return personalityChance * multiplier * detour;
    }
}
