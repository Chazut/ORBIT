using Orbit.Entities;

namespace Orbit.Helpers;

/// <summary>
/// Faction- and personality-level gate that decides whether a bot is
/// ever allowed to sprint. Scavs (assault / assaultGroup) never sprint —
/// they wander, they don't hustle. PMCs with SprintPropensity rolled to
/// (near-)zero (Timmy) also never sprint. Distance-based ramp-up is
/// handled at the call site.
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
