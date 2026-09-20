using System;
using System.Collections.Generic;

namespace Orbit.Helpers;

internal static class HuntFactionPolicy
{
    private static readonly string[] Owners = { "untar", "ruaf", "remnant", "blackDiv", "ISB", "Combine" };

    // UNTAR 3.x AddRaiderHuntToMap creates pmcBot waves with TriggerId exactly "hunt".
    // Other hunt ids must identify their owner; the word hunt alone is not a takeover permission.
    internal static string Owner(string spawnId, bool legacyUntarHunts)
    {
        if (string.IsNullOrEmpty(spawnId)) return null;
        if (legacyUntarHunts && spawnId.Equals("hunt", StringComparison.OrdinalIgnoreCase)) return "untar";
        foreach (var owner in Owners)
            if (spawnId.IndexOf(owner, StringComparison.OrdinalIgnoreCase) >= 0) return owner;
        return null;
    }

    internal static bool IsExcluded(string spawnId, bool legacyUntarHunts, ISet<string> excludedOwners)
    {
        var owner = Owner(spawnId, legacyUntarHunts);
        // Preserve an unidentified mod's hunt instead of silently replacing its behaviour.
        return owner == null || excludedOwners.Contains(owner);
    }
}
