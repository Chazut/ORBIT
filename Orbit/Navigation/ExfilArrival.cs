using System.Runtime.CompilerServices;
using EFT.Interactive;
using Orbit.Entities;
using UnityEngine;

namespace Orbit.Navigation;

/// <summary>Arrival checks shared by navigation and the car countdown. No scene scans or per-tick allocations.</summary>
internal static class ExfilArrival
{
    private sealed class Trigger(ExfiltrationPoint exfil)
    {
        public readonly Collider Collider = exfil.GetComponent<Collider>();
        public Bounds LastBounds;
        public bool HasBounds;
    }

    private static readonly ConditionalWeakTable<ExfiltrationPoint, Trigger> Triggers = new();

    internal static bool IsSharedTimer(ExfiltrationPoint exfil) =>
        exfil?.Settings?.ExfiltrationType == EExfiltrationType.SharedTimer;

    internal static bool IsUnavailable(ExfiltrationPoint exfil) =>
        exfil.Status == EExfiltrationStatus.NotPresent
        || exfil.Status == EExfiltrationStatus.Hidden
        || exfil.Status == EExfiltrationStatus.AwaitsManualActivation;

    internal static bool IsInside(Agent agent, Waypoint location)
    {
        if (location?.Target is not ExfiltrationPoint exfil) return false;
        var trigger = Triggers.GetValue(exfil, static point => new Trigger(point));
        var collider = trigger.Collider;
        if (collider != null)
        {
            var bounds = collider.bounds;
            if (!IsUnavailable(exfil))
            {
                trigger.LastBounds = bounds;
                trigger.HasBounds = true;
            }
            return bounds.Contains(agent.Position);
        }
        // Match navigation's trigger bounds. A missing collider still requires the normal 15m arrival radius.
        return (agent.Position - location.Position).sqrMagnitude <= 225f;
    }

    // NotPresent disables the collider before OnStatusChanged fires. Use the last live
    // bounds with the bot's current position only for an already registered departure.
    internal static bool IsInsideDepartingCar(Agent agent, Waypoint location)
    {
        if (location?.Target is not ExfiltrationPoint exfil
            || !Triggers.TryGetValue(exfil, out var trigger)) return false;
        return trigger.Collider != null
            ? trigger.HasBounds && trigger.LastBounds.Contains(agent.Position)
            : (agent.Position - location.Position).sqrMagnitude <= 225f;
    }

    internal static void Abandon(Agent agent, Waypoint location)
    {
        var squad = agent.Squad;
        if (squad != null)
        {
            squad.CompletedPoiIds.Add(location.Id);
            squad.NearestExfilCached = null;
            squad.NearestExfilCachedAt = -999f;
            if (squad.Objective.Location?.Id == location.Id)
                squad.Objective.Location = null;
        }
        if (agent.SoloExtractTarget?.Id == location.Id)
            agent.SoloExtractTarget = null;
        agent.Objective.Location = null;
        agent.Objective.SplinterParent = null;
        agent.Objective.ArrivalPath = null;
        agent.Objective.ExfilOutsideTriggerSince = -1f;
        agent.Objective.DispatchTime = Time.time;
        agent.Objective.Status = ObjectiveStatus.None;
        // Preserve solo/squad extraction intent; the dispatcher selects another eligible exit.
    }
}
