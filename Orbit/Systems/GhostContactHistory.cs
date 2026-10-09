using System.Collections.Generic;

namespace Orbit.Systems;

// A missed detection can be reconsidered at close range. An actual fight keeps its
// full cooldown, including when both squads stayed beside the same cover.
internal sealed class GhostContactHistory
{
    internal const float GuaranteedRange = 20f;
    private const float ShadowCooldown = 30f;
    private readonly Dictionary<string, (float At, bool Fought)> _pairs = new();

    internal bool CanAttempt(string pair, float now, float fightCooldown, bool guaranteed)
        => !_pairs.TryGetValue(pair, out var last)
           || (last.Fought ? now - last.At >= fightCooldown
               : guaranteed || now - last.At >= ShadowCooldown);

    internal void Record(string pair, float now, bool fought) => _pairs[pair] = (now, fought);

    internal static float Chance(bool guaranteed, float distanceChance, float acquisition)
        => guaranteed ? 1f : distanceChance * acquisition;
}
