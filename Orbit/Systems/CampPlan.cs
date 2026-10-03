using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

/// <summary>A bounded squad detour. The original mission stays intact while members hold separate positions.</summary>
internal sealed class CampPlan
{
    internal readonly Dictionary<Agent, Waypoint> Slots = new();
    internal AmbushSite Site;
    internal bool Active => Site != null;
    internal bool Holding { get; private set; }
    internal float NextCheck;
    internal int Visits;
    private float _startedAt, _lastTick, _holdUntil, _duration;
    private Waypoint _mission;

    internal bool Owns(Agent agent) => Active && Slots.TryGetValue(agent, out var point) && agent.Objective.Location == point;

    internal void Begin(Squad squad, AmbushSite site, Dictionary<Agent, CoverPoint> positions, WaypointSystem waypoints)
    {
        Site = site;
        Holding = false;
        _mission = squad.Objective.Location;
        _startedAt = _lastTick = Time.time;
        var rule = ServerConfig.Ambush.For(site.Kind);
        _duration = Random.Range(rule.DurationMin, rule.DurationMax);
        Visits++;
        foreach (var pair in positions)
        {
            var agent = pair.Key;
            var point = new Waypoint(waypoints.NewRuntimeWaypointId(), WaypointCategory.Synthetic,
                "Ambush_" + site.Kind, pair.Value.Position, 2.25f, new(), new(), null);
            if (agent.Objective.Location != null) waypoints.ReleaseClaim(agent.Objective.Location.Id, agent.Id);
            Slots.Add(agent, point);
            agent.Objective.Location = point;
            agent.Objective.SplinterParent = null;
            agent.Objective.ArrivalPath = null;
            agent.Objective.Status = ObjectiveStatus.None;
            agent.Objective.DispatchTime = Time.time;
            agent.Guard.CoverPoint = pair.Value;
        }
        Log.Info($"AMBUSH: {squad} approach kind={site.Kind} target={site.Position} members={Slots.Count} hold={_duration:F0}s");
    }

    internal bool Tick(Squad squad, WaypointSystem waypoints)
    {
        if (!Active) return false;
        PauseMission(squad);
        var cfg = ServerConfig.Ambush;
        var reason = !cfg.Allows(squad.Leader?.BotCategory) || !cfg.For(Site.Kind).Enabled ? "disabled"
            : !AmbushDirector.Available(squad) ? "combat, extraction or member unavailable"
            : squad.Objective.Location != _mission ? "mission changed"
            : !waypoints.IsAmbushSiteAvailable(Site, squad.Leader.BotCategory) ? "target unavailable" : null;
        if (reason != null) { End(squad, reason); return false; }
        // A late member needs a new formation. Release the detour instead of leaving it behind.
        if (Slots.Count != squad.Size) { End(squad, "group changed"); return false; }
        var arrived = false;
        foreach (var pair in Slots)
        {
            var agent = pair.Key;
            if (!squad.Members.Contains(agent) || !Owns(agent) || agent.Objective.Status == ObjectiveStatus.Failed)
            { End(squad, "route failed or assignment changed"); return false; }
            if (agent.Objective.Status == ObjectiveStatus.Finished)
            {
                if ((agent.Position - pair.Value.Position).sqrMagnitude > 16f)
                { End(squad, "member displaced"); return false; }
                arrived = true;
            }
            else if (Time.time - _startedAt >= cfg.TravelTimeout)
            { End(squad, "travel timeout"); return false; }
        }
        if (!Holding && arrived)
        {
            Holding = true;
            _holdUntil = Time.time + _duration;
            Log.Info($"AMBUSH: {squad} holding kind={Site.Kind} duration={_duration:F0}s");
        }
        if (Holding && Time.time >= _holdUntil) { End(squad, "duration completed"); return false; }
        return true;
    }

    private void PauseMission(Squad squad)
    {
        var elapsed = Mathf.Max(0f, Time.time - _lastTick);
        _lastTick = Time.time;
        if (squad.Objective.Location == _mission) squad.Objective.StartTime += elapsed;
        if (squad.MainObjectives == null) return;
        foreach (var main in squad.MainObjectives)
        {
            if (main.Completed) continue;
            if (main.KillsRoamStartedAt > 0) main.KillsRoamStartedAt += elapsed;
            main.KillsFloorLastTick = 0;
            main.LootValueLastEngagedAt = 0;
        }
    }

    internal void End(Squad squad, string reason)
    {
        if (!Active) return;
        PauseMission(squad);
        Log.Info($"AMBUSH: {squad} ended kind={Site.Kind} reason={reason}");
        foreach (var pair in Slots)
        {
            var agent = pair.Key;
            if (agent.Objective.Location == pair.Value)
            {
                agent.Objective.Location = null;
                agent.Objective.SplinterParent = null;
                agent.Objective.Status = ObjectiveStatus.None;
                agent.Guard.CoverPoint = null;
                agent.Look.Target = null;
            }
            squad.CompletedPoiIds.Remove(pair.Value.Id);
            squad.RecentlyVisitedPoiCooldowns.Remove(pair.Value.Id);
            agent.ArrivalFailures.Forget(pair.Value.Id);
        }
        Slots.Clear();
        Site = null;
        Holding = false;
        NextCheck = Time.time + ServerConfig.Ambush.Cooldown;
    }
}
