using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

internal sealed class AmbushDirector(WaypointSystem waypoints)
{
    private readonly List<Squad> _active = new();
    private readonly List<AmbushSite> _candidates = new();
    private readonly List<Vector3> _occupied = new();
    private readonly Dictionary<Agent, CoverPoint> _positions = new();
    private float _nextPlan;

    internal static bool Available(Squad squad)
    {
        if (squad.Leader == null || squad.Size == 0 || squad.Operation?.Active == true || squad.ExtractRequested || squad.SainResolutionPending
            || squad.CombatCallerMemberIdx >= 0 || Time.time < squad.GhostFightUntil || squad.CorpseEscort.Active
            || squad.PreInterruptObjectiveLocation != null || squad.InvestigateNoisePosition.HasValue
            || squad.Objective.Location?.Category is WaypointCategory.Exfil or WaypointCategory.Corpse) return false;
        foreach (var agent in squad.Members)
            if (agent == null || !agent.IsActive || agent.Player?.HealthController?.IsAlive != true
                || agent.SoloExtractRequested || agent.LootExtractSweep != null
                || (CorpseEscort.InFlight(agent) && squad.Camp.Looter != agent)
                || agent.Bot?.Memory?.HaveEnemy == true || agent.Bot?.Memory?.IsUnderFire == true) return false;
        return true;
    }

    internal void Prune(IList<Squad> squads)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var squad = _active[i];
            if (!squads.Contains(squad)) squad.Camp.End(squad, "group removed");
            if (!squad.Camp.Active) _active.RemoveAt(i);
        }
    }

    internal static AmbushStyleSettings Style(Squad squad)
        => ServerConfig.Ambush.Style(squad.Personality?.Archetype.ToString(), squad.Leader?.BotCategory == "PlayerScav");

    internal bool TryStart(Squad squad)
    {
        var cfg = ServerConfig.Ambush;
        if (squad.Camp.Active || !cfg.Allows(squad.Leader?.BotCategory) || !Available(squad)) return false;
        if (Time.time < _nextPlan) return false;

        // A main owns both its target and its mode. No periodic roll can turn another zone into a hotspot camp.
        MainObjective pending = null;
        var nearest = float.MaxValue;
        if (squad.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
            {
                if (main.Completed || !main.IsCampMain || Time.time < main.CampRetryAt) continue;
                var rule = cfg.For(main.Type == MainObjectiveType.ExtractCamp ? CampSiteKind.Extract : CampSiteKind.Hotspot);
                var delta = main.Position - squad.Leader.Position;
                var distance = delta.x * delta.x + delta.z * delta.z;
                if (!rule.Enabled || distance > rule.SearchRadius * rule.SearchRadius || distance >= nearest) continue;
                nearest = distance;
                pending = main;
            }
        if (pending != null)
        {
            _nextPlan = Time.time + .5f;
            pending.CampRetryAt = Time.time + 15f;
            pending.CampSite ??= waypoints.CreateKillMainCampSite(pending);
            if (pending.CampSite != null && TryFormation(squad, pending.CampSite, pending)) return true;
            Log.Debug($"AMBUSH: {squad} main={pending.Type} waiting for reachable cover at {pending.Position}");
            return false;
        }

        // Only landed airdrops are opportunistic detours. Main camps ignore this cooldown.
        if (!cfg.Airdrops.Enabled) return false;
        if (squad.Camp.NextCheck == 0)
        {
            squad.Camp.NextCheck = Time.time + Random.Range(15f, cfg.CheckInterval);
            return false;
        }
        if (Time.time < squad.Camp.NextCheck) return false;
        squad.Camp.NextCheck = Time.time + cfg.CheckInterval * Random.Range(.8f, 1.2f);
        waypoints.CollectAmbushSites(squad, CampSiteKind.Airdrop, cfg.Airdrops.SearchRadius, _candidates);
        _candidates.RemoveAll(site => squad.Camp.VisitedAirdrops.Contains(site));
        if (_candidates.Count == 0 || Random.value >= Style(squad).AirdropChance) return false;
        _nextPlan = Time.time + .5f;
        // One bounded formation attempt per opportunity, irrespective of nearby drop count.
        return TryFormation(squad, _candidates[Random.Range(0, _candidates.Count)], null);
    }

    private bool TryFormation(Squad squad, AmbushSite site, MainObjective main)
    {
        if (!waypoints.IsAmbushSiteAvailable(site, squad.Leader.BotCategory)) return false;
        var rule = ServerConfig.Ambush.For(site.Kind);
        var pathBudget = 24;
        _occupied.Clear();
        _positions.Clear();
        // Spacing belongs to this formation only. Other squads never reserve a target or its surroundings.
        foreach (var member in squad.Members)
        {
            if (!waypoints.TryPickAmbushCover(member, site, rule, _occupied, ref pathBudget, out var cover)) break;
            _positions.Add(member, cover);
            _occupied.Add(cover.Position);
        }
        if (_positions.Count != squad.Size) return false;
        squad.Camp.Begin(squad, site, _positions, waypoints, main);
        if (!_active.Contains(squad)) _active.Add(squad);
        return true;
    }
}
