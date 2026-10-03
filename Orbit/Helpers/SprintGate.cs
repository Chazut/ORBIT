using Orbit.Entities;

namespace Orbit.Helpers;

/// <summary>
/// ORBIT movement sprint propensity. PMC personalities remain authoritative for PMCs.
/// Other categories use server settings; combat movement stays with its owning brain.
/// </summary>
public static class SprintGate
{
    public static float Propensity(Agent agent)
        => agent.BotCategory == "PMC" ? agent.Squad?.Personality?.SprintPropensity ?? 0.5f
            : ServerConfig.Movement.For(agent.BotCategory);

    public static bool IsAllowedByFaction(Agent agent) => Propensity(agent) > 0.001f;
}
