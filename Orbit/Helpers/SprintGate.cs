using Orbit.Entities;

namespace Orbit.Helpers;

/// <summary>
/// Faction- and personality-level sprint eligibility. Scavs never sprint; PMCs with near-zero
/// SprintPropensity (Timmy) never sprint. Distance-based ramp-up is handled at the call site.
/// </summary>
public static class SprintGate
{
    public static bool IsAllowedByFaction(Agent agent)
    {
        var role = agent.Bot?.Profile?.Info?.Settings?.Role;
        if (role.HasValue && role.Value.IsScav()) return false;
        var propensity = agent.Squad?.Personality?.SprintPropensity ?? 0.5f;
        return propensity > 0.001f;
    }
}
